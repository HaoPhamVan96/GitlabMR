using System.Text.Json.Nodes;

namespace GitLabMR;

/// <summary>
/// Sau khi pipeline pass: hỏi Argo CD xem bản mới đã lên pod chưa.
///   • Pod mới (tạo sau lúc pipeline bắt đầu) CrashLoopBackOff / ImagePullBackOff / lỗi config… → báo ngay, kèm event + log cuối.
///   • Pod mới đủ container Ready (+ URL kiểm tra port trả về &lt; 500 nếu có) → báo deploy xong.
///   • Quá ArgoWaitMin phút chưa xong → báo kèm trạng thái hiện tại.
/// Chỉ đọc: không sync / restart gì trên Argo.
/// </summary>
public sealed partial class AppHost
{
    private sealed class DeployWatch
    {
        public required string Repo, App, Label, PipeUrl;
        public string? Url;                       // URL kiểm tra port
        public string Sha = "";
        public DateTime Start;                    // lúc pipeline bắt đầu (UTC): pod tạo sau lúc này là bản mới
        public DateTime Since = DateTime.Now;
        public bool Refreshed;
        public string LastState = "";
    }

    private static readonly HttpClient PortHttp = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    }) { Timeout = TimeSpan.FromSeconds(15) };

    private static bool RepoMatch(string key, string project) =>
        project.Equals(key.Trim('/'), StringComparison.OrdinalIgnoreCase)
        || project.EndsWith("/" + key.Trim('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Pipeline pass: thêm việc theo dõi Argo cho các app ứng với repo. Trả về true nếu có.</summary>
    private bool AddDeployWatches(Workspace ws, AutoState a, GlProject p, GlPipeline pl, string label)
    {
        var f = ws.Auto;
        if (f is not { ArgoWatch: true } || string.IsNullOrEmpty(f.ArgoUrl) || f.ArgoSecret == null) return false;
        var maps = f.ArgoApps.Where(m => RepoMatch(m.Repo, p.PathWithNamespace)).ToList();
        foreach (var m in maps)
            lock (a.Jobs)
                if (!a.Deploys.Any(d => d.App == m.App && d.Sha == pl.Sha))
                    a.Deploys.Add(new DeployWatch
                    {
                        Repo = p.PathWithNamespace, App = m.App, Url = m.Url, Label = label, PipeUrl = pl.WebUrl, Sha = pl.Sha,
                        Start = (pl.CreatedAt?.ToUniversalTime() ?? DateTime.UtcNow).AddMinutes(-1),
                    });
        if (maps.Count > 0) Log($"[Auto {ws.Name}] {label}: pipeline pass, chờ Argo deploy {string.Join(", ", maps.Select(m => m.App))}…", LogKind.Info);
        return maps.Count > 0;
    }

    private ArgoClient? AutoArgo(Workspace ws, AutoState a)
    {
        var f = ws.Auto;
        if (f == null || string.IsNullOrEmpty(f.ArgoUrl) || f.ArgoSecret == null) return null;
        var secret = Platform.UnprotectToken(ws.ArgoKey, f.ArgoSecret);
        if (string.IsNullOrEmpty(secret)) return null;
        var key = $"{f.ArgoUrl}|{f.ArgoUser}|{f.ArgoIgnoreSsl}|{f.ArgoSecret}";
        if (a.Argo == null || a.ArgoKey != key)
        {
            a.Argo?.Dispose();
            a.Argo = new ArgoClient(f.ArgoUrl, f.ArgoUser, secret, f.ArgoIgnoreSsl);
            a.ArgoKey = key;
        }
        return a.Argo;
    }

    /// <summary>Trả về số app còn đang chờ deploy.</summary>
    private async Task<int> RunDeployWatchesAsync(Workspace ws, AutoState a, CancellationToken ct)
    {
        List<DeployWatch> list;
        lock (a.Jobs) list = a.Deploys.ToList();
        if (list.Count == 0) return 0;
        var argo = AutoArgo(ws, a);
        if (argo == null) { lock (a.Jobs) a.Deploys.Clear(); return 0; }
        var wait = TimeSpan.FromMinutes(Math.Clamp(ws.Auto!.ArgoWaitMin, 3, 120));
        var tag = $"[Auto {ws.Name}]";
        foreach (var w in list)
        {
            try
            {
                var done = await CheckDeployAsync(ws, a, argo, w, wait, tag, ct);
                if (done) lock (a.Jobs) a.Deploys.Remove(w);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Once(a, $"argoerr|{w.App}|{Describe(ex)}", $"{tag} Argo {w.App}: {Describe(ex)}", LogKind.Warn);
                if (DateTime.Now - w.Since > wait)
                {
                    lock (a.Jobs) a.Deploys.Remove(w);
                    await AlertAsync(ws, "Không kiểm tra được Argo", $"{w.Label}\nApp {w.App}: {Describe(ex)}", argo.AppUrl(w.App));
                }
            }
        }
        lock (a.Jobs) return a.Deploys.Count;
    }

    private static string PodLine(ArgoPod p) =>
        $"{p.Name}: {(p.Reason.Length > 0 ? p.Reason : p.Health)} {p.Ready}/{p.Total}" + (p.Restarts > 0 ? $", restart {p.Restarts}" : "");

    private static string ShortSha(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>true = đã xong (báo rồi), false = còn chờ.</summary>
    private async Task<bool> CheckDeployAsync(Workspace ws, AutoState a, ArgoClient argo, DeployWatch w, TimeSpan wait, string tag, CancellationToken ct)
    {
        var app = await argo.GetAppAsync(w.App, !w.Refreshed, ct);
        w.Refreshed = true;
        var pods = await argo.GetPodsAsync(w.App, ct);
        var sha = ShortSha(w.Sha);
        // Bản mới: pod tạo sau lúc pipeline chạy, hoặc image có tag là commit vừa merge
        var fresh = pods.Where(p => p.Created >= w.Start || (sha.Length >= 7 && p.Images.Any(i => i.Contains(sha, StringComparison.OrdinalIgnoreCase)))).ToList();
        var head = $"{w.Label}\nArgo {w.App}: {app.Health}/{app.Sync}";

        var broken = fresh.FirstOrDefault(p => p.Broken);
        if (broken != null)
        {
            await DeployFailAsync(ws, argo, w.App, broken, fresh, head, "Pipeline pass nhưng pod KHÔNG lên", "Pipeline: " + w.PipeUrl, ct);
            lock (a.Jobs) a.ArgoBad.Add(w.App);
            return true;
        }

        var state = fresh.Count == 0 ? $"chưa có pod mới ({app.Health}/{app.Sync})" : string.Join("; ", fresh.Select(PodLine));
        if (state != w.LastState) { w.LastState = state; Log($"{tag} Argo {w.App}: {state}", LogKind.Info); }

        if (fresh.Count > 0 && fresh.All(p => p.AllReady) && app.Health != "Progressing")
        {
            string? port = null;
            if (!string.IsNullOrWhiteSpace(w.Url))
            {
                port = await PortCheckAsync(w.Url!, ct);
                if (port.StartsWith('✗'))
                {
                    if (DateTime.Now - w.Since <= wait) { w.LastState = "port " + port; return false; }
                    await AlertAsync(ws, "Pod Ready nhưng không gọi được port", $"{head}\n{string.Join("\n", fresh.Select(PodLine))}\n{w.Url} → {port}", argo.AppUrl(w.App));
                    return true;
                }
            }
            lock (a.Jobs) a.ArgoBad.Remove(w.App);
            Log($"{tag} {w.Label}: Argo {w.App} deploy xong – {state}" + (port != null ? $", {w.Url} {port}" : ""), LogKind.Success);
            await NotifyAsync(ws, "Deploy xong – pod đã chạy",
                $"{head}\n{string.Join("\n", fresh.Select(PodLine))}" + (port != null ? $"\nPort: {w.Url} {port}" : "") + $"\nPipeline: {w.PipeUrl}",
                argo.AppUrl(w.App));
            return true;
        }

        if (DateTime.Now - w.Since <= wait) return false;
        // Quá giờ: pod còn chạy nhưng chưa Ready (readiness probe fail…) hoặc Argo chưa rollout
        var stuck = fresh.FirstOrDefault(p => !p.AllReady);
        if (stuck != null)
        {
            await DeployFailAsync(ws, argo, w.App, stuck, fresh, head, $"Quá {wait.TotalMinutes:0} phút pod chưa Ready", "Pipeline: " + w.PipeUrl, ct);
            lock (a.Jobs) a.ArgoBad.Add(w.App);
        }
        else
            await AlertAsync(ws, $"Quá {wait.TotalMinutes:0} phút chưa thấy bản mới trên Argo",
                $"{head}" + (app.OpMessage is { Length: > 0 } om ? $"\nSync: {app.OpPhase} – {om}" : "") +
                $"\nChưa có pod nào tạo sau lúc pipeline chạy – Argo chưa sync / image tag không đổi?\nPipeline: {w.PipeUrl}", argo.AppUrl(w.App));
        return true;
    }

    private async Task DeployFailAsync(Workspace ws, ArgoClient argo, string appName, ArgoPod pod, List<ArgoPod> fresh,
        string head, string title, string? tail, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder(head);
        foreach (var p in fresh.Take(4)) text.Append('\n').Append(PodLine(p));
        if (pod.HealthMsg.Length > 0) text.Append("\nLý do: ").Append(pod.HealthMsg);
        try
        {
            var ev = await argo.GetPodEventsAsync(appName, pod, ct);
            if (ev.Count > 0) text.Append("\nEvent: ").Append(string.Join("\n  ", ev));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { text.Append("\n(Không đọc được event: ").Append(Describe(ex)).Append(')'); }
        if (!pod.Reason.Contains("ImagePull", StringComparison.OrdinalIgnoreCase) && !pod.Reason.Contains("ErrImage", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var log = await argo.GetPodLogsAsync(appName, pod, previous: pod.Restarts > 0, tail: 40, ct);
                if (log.Length > 0)
                {
                    // Giữ phần cuối (lỗi thật thường ở cuối), tin Zalo tối đa ~2000 ký tự
                    var lines = log.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(15).Select(l => l.Length > 220 ? l[..220] + "…" : l);
                    var last = string.Join("\n", lines);
                    if (last.Length > 900) last = "…" + last[^900..];
                    text.Append("\nLog cuối:\n").Append(last);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { text.Append("\n(Không đọc được log: ").Append(Describe(ex)).Append(')'); }
        }
        if (tail != null) text.Append('\n').Append(tail);
        await AlertAsync(ws, title, text.ToString(), argo.AppUrl(appName));
    }

    // ---------------------------------------------------------------- canh liên tục (cả khi người khác merge tay)
    private static readonly TimeSpan ArgoMonEvery = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Mỗi ~90 giây xem pod của các app đã map. Pod CrashLoop / ImagePull / quá giờ chưa Ready → báo 1 lần (kèm commit mới nhất
    /// trên branch đích GitLab = ai vừa merge), khoẻ lại thì báo đã chạy lại. App đang được theo dõi deploy (Auto vừa merge) thì bỏ qua.
    /// </summary>
    private async Task RunArgoMonitorAsync(Workspace ws, AutoState a, CancellationToken ct)
    {
        var f = ws.Auto;
        if (f is not { ArgoWatch: true, ArgoMonitor: true } || f.ArgoApps.Count == 0) return;
        if (a.ArgoMon is { } t && DateTime.Now - t < ArgoMonEvery) return;
        a.ArgoMon = DateTime.Now;
        var argo = AutoArgo(ws, a);
        if (argo == null) return;
        var wait = TimeSpan.FromMinutes(Math.Clamp(f.ArgoWaitMin, 3, 120));
        var tag = $"[Auto {ws.Name}]";
        foreach (var m in f.ArgoApps.GroupBy(x => x.App).Select(g => g.First()))
        {
            bool watching, bad;
            lock (a.Jobs) { watching = a.Deploys.Any(d => d.App == m.App); bad = a.ArgoBad.Contains(m.App); }
            if (watching) continue;
            try
            {
                var app = await argo.GetAppAsync(m.App, false, ct);
                var pods = await argo.GetPodsAsync(m.App, ct);
                var now = DateTime.Now;
                var prefix = m.App + "|";
                var keys = pods.Select(p => prefix + p.Name).ToHashSet();
                foreach (var k in a.ArgoNotReady.Keys.Where(k => k.StartsWith(prefix) && !keys.Contains(k)).ToList()) a.ArgoNotReady.Remove(k);
                foreach (var p in pods)
                {
                    if (p.AllReady || p.Broken) a.ArgoNotReady.Remove(prefix + p.Name);
                    else a.ArgoNotReady.TryAdd(prefix + p.Name, now);
                }
                var broken = pods.FirstOrDefault(p => p.Broken)
                             ?? pods.FirstOrDefault(p => a.ArgoNotReady.TryGetValue(prefix + p.Name, out var since) && now - since > wait);
                if (broken != null && !bad)
                {
                    lock (a.Jobs) a.ArgoBad.Add(m.App);
                    var merge = await LastMergeAsync(ws, a, m.Repo, ct);
                    var head = $"Argo {m.App}: {app.Health}/{app.Sync}" + (merge != null ? "\n" + merge : "");
                    var title = broken.Broken ? "Pod trên Argo KHÔNG chạy" : $"Pod trên Argo quá {wait.TotalMinutes:0} phút chưa Ready";
                    await DeployFailAsync(ws, argo, m.App, broken, pods, head, title, null, ct);
                }
                else if (broken == null && bad && pods.Count > 0 && pods.All(p => p.AllReady))
                {
                    lock (a.Jobs) a.ArgoBad.Remove(m.App);
                    Log($"{tag} Argo {m.App} đã chạy lại", LogKind.Success);
                    await NotifyAsync(ws, "Pod đã chạy lại", $"Argo {m.App}: {app.Health}/{app.Sync}\n" + string.Join("\n", pods.Take(4).Select(PodLine)), argo.AppUrl(m.App));
                }
                a.Noted.RemoveWhere(k => k.StartsWith("argomon|" + prefix));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Once(a, $"argomon|{m.App}|{Describe(ex)}", $"{tag} Canh Argo {m.App}: {Describe(ex)}", LogKind.Warn);
            }
        }
    }

    /// <summary>"testing2 mới nhất: abc1234 Merge branch 'x' into 'testing2' – Tên (10:32 25/11)" – để biết ai vừa merge.</summary>
    private async Task<string?> LastMergeAsync(Workspace ws, AutoState a, string repo, CancellationToken ct)
    {
        try
        {
            var g = AutoGitLab(ws, a);
            if (g == null) return null;
            var p = g.Value.Projects.FirstOrDefault(x => RepoMatch(repo, x.Path));
            if (p == null) return null;
            var target = ws.Auto!.GitLabTarget!;
            var c = await g.Value.Gl.GetCommitAsync(p.Id, target, ct);
            return $"{target} mới nhất: {c.ShortId} {c.Title} – {c.AuthorName}" + (c.CommittedDate is { } d ? $" ({d.ToLocalTime():HH:mm dd/MM})" : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>"✓ HTTP 200" khi gọi được (mọi mã &lt; 500 nghĩa là app đã nghe port), "✗ …" khi chưa.</summary>
    private static async Task<string> PortCheckAsync(string url, CancellationToken ct)
    {
        try
        {
            using var res = await PortHttp.GetAsync(url.Trim(), HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)res.StatusCode;
            return code < 500 ? $"✓ HTTP {code}" : $"✗ HTTP {code}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "✗ quá 15 giây không phản hồi"; }
        catch (HttpRequestException ex) { return "✗ " + (ex.InnerException?.Message ?? ex.Message); }
    }

    // ---------------------------------------------------------------- cấu hình
    /// <summary>Mỗi dòng: repo = app [url]. Vd: core-pr = ppx-core-testing2 https://core-testing2.xxx/health</summary>
    private static List<ArgoMap> ParseArgoApps(string text)
    {
        var list = new List<ArgoMap>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) throw new GitLabException($"Dòng Argo \"{line}\" sai cú pháp – cần: repo = app [url kiểm tra port]");
            var repo = line[..eq].Trim();
            var rest = line[(eq + 1)..].Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries);
            if (repo.Length == 0 || rest.Length == 0) throw new GitLabException($"Dòng Argo \"{line}\" thiếu repo hoặc app");
            var url = rest.Length > 1 ? rest[1] : null;
            if (url != null && !Uri.TryCreate(url, UriKind.Absolute, out _)) throw new GitLabException($"URL kiểm tra port \"{url}\" không hợp lệ");
            list.Add(new ArgoMap { Repo = repo, App = AppName(rest[0]), Url = url });
        }
        return list;
    }

    /// <summary>Dán link app từ trình duyệt (…/applications/argocd/ten-app?resource=) cũng được: lấy tên app.</summary>
    private static string AppName(string s)
    {
        if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(s, UriKind.Absolute, out var u)) return s;
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals("applications", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && parts.Length > i + 1 ? Uri.UnescapeDataString(parts[^1]) : s;
    }

    private static string ArgoAppsText(List<ArgoMap> maps) =>
        string.Join("\n", maps.Select(m => $"{m.Repo} = {m.App}" + (m.Url is { Length: > 0 } ? " " + m.Url : "")));

    private void SaveArgo(JsonNode m, AutoFlow f)
    {
        if (m["argoWatch"] == null) return;
        f.ArgoWatch = Bool(m, "argoWatch");
        f.ArgoUrl = Str(m, "argoUrl") is { Length: > 0 } u ? ArgoClient.NormalizeUrl(u) : null;
        f.ArgoUser = Str(m, "argoUser") is { Length: > 0 } us ? us : null;
        f.ArgoIgnoreSsl = Bool(m, "argoSsl");
        f.ArgoApps = ParseArgoApps(Str(m, "argoApps"));
        f.ArgoWaitMin = Math.Clamp((int?)m["argoWait"] ?? 15, 3, 120);
        if (m["argoMonitor"] != null) f.ArgoMonitor = Bool(m, "argoMonitor");
        var sec = Str(m, "argoSecret");
        if (sec == "-") f.ArgoSecret = null;
        else if (sec.Length > 0) f.ArgoSecret = Platform.ProtectToken(_ws.ArgoKey, sec);
        if (f.ArgoWatch && (f.ArgoUrl == null || f.ArgoSecret == null))
            throw new GitLabException("Nhập URL và mật khẩu / token Argo trước khi bật theo dõi deploy.");
        if (f.ArgoWatch && f.ArgoApps.Count == 0)
            throw new GitLabException("Nhập ít nhất 1 dòng repo = app Argo.");
    }

    /// <summary>Đăng nhập thử, liệt kê app account xem được, đối chiếu với bảng repo = app.</summary>
    private async Task AutoTestArgoAsync(JsonNode m)
    {
        var url = Str(m, "argoUrl");
        if (url.Length == 0) throw new GitLabException("Nhập URL Argo CD trước đã.");
        var sec = Str(m, "argoSecret");
        if (sec.Length == 0 || sec == "-")
            sec = (_ws.Auto?.ArgoSecret is { } st ? Platform.UnprotectToken(_ws.ArgoKey, st) : null) ?? throw new GitLabException("Nhập mật khẩu / token Argo.");
        var maps = ParseArgoApps(Str(m, "argoApps"));
        Send(new { type = "toast", text = "Đang đăng nhập Argo…" });
        using var argo = new ArgoClient(url, Str(m, "argoUser"), sec, Bool(m, "argoSsl"));
        var apps = await argo.ListAppsAsync(_life.Token);
        Log($"[Argo] Đăng nhập được, thấy {apps.Count} app: " + string.Join(", ", apps.Take(60).Select(x => $"{x.Name} ({x.Health}/{x.Sync})")) + (apps.Count > 60 ? " …" : ""), LogKind.Success);
        var missing = maps.Where(x => apps.All(y => !y.Name.Equals(x.App, StringComparison.Ordinal))).Select(x => x.App).ToList();
        foreach (var x in maps.Where(x => !missing.Contains(x.App)))
        {
            try
            {
                var pods = await argo.GetPodsAsync(x.App, _life.Token);
                Log($"[Argo] {x.Repo} → {x.App}: {pods.Count} pod – " + string.Join("; ", pods.Take(5).Select(PodLine)), LogKind.Info);
                var bad = pods.FirstOrDefault(p => p.Broken);
                if (bad != null)
                {
                    try
                    {
                        var log = await argo.GetPodLogsAsync(x.App, bad, bad.Restarts > 0, 5, _life.Token);
                        Log($"[Argo] Đọc được log {bad.Name} (quyền logs OK): {log.Split('\n').LastOrDefault()}", LogKind.Info);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log($"[Argo] Không đọc được log – thiếu quyền logs/get? {Describe(ex)}", LogKind.Warn); }
                }
                if (!string.IsNullOrWhiteSpace(x.Url)) Log($"[Argo] {x.Url} → {await PortCheckAsync(x.Url!, _life.Token)}", LogKind.Info);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log($"[Argo] {x.App}: {Describe(ex)}", LogKind.Warn); }
        }
        if (missing.Count > 0) throw new GitLabException($"Đăng nhập được, nhưng không thấy app: {string.Join(", ", missing)} – xem tên app trong nhật ký.");
        Send(new { type = "toast", text = $"Argo OK – thấy {apps.Count} app" + (maps.Count > 0 ? $", {maps.Count} app đã map đều có" : "") });
    }
}
