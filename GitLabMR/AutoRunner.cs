using System.Text.Json.Nodes;

namespace GitLabMR;

/// <summary>
/// Auto flow (workspace Gerrit), cứ mỗi IntervalSec giây:
///   1. Tìm change đang mở của các dev đã đặt (Owners) mà reviewer (Reviewers) đã Code-Review +1.
///   2. +2 & submit (không đụng change có phiếu âm / còn thiếu Verified…; conflict thì báo Teams).
///   3. Submit xong: chờ code sync sang GitLab rồi tạo (và merge) MR {branch} → GitLabTarget.
/// Mỗi change chỉ xử lý 1 lần cho mỗi patchset; patchset mới (sau khi dev sửa) thì xét lại.
/// Chạy thử (DryRun): chỉ ghi nhật ký những gì sẽ làm.
/// </summary>
public sealed partial class AppHost
{
    private static readonly TimeSpan SyncWait = TimeSpan.FromMinutes(15);

    private sealed class AutoState
    {
        public CancellationTokenSource? Loop;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Dictionary<string, string> Handled = new();  // change -> revision đã xử lý
        public readonly HashSet<string> Noted = new();               // đã ghi log / báo (chống lặp mỗi phút)
        public readonly List<AutoJob> Jobs = new();                  // MR GitLab chờ tạo / merge
        public readonly List<PipeWatch> Pipes = new();               // pipeline chờ build xong
        public readonly System.Collections.Concurrent.ConcurrentQueue<PipeWatch> NewPipes = new(); // từ luồng auto-fix
        public readonly HashSet<string> Fixing = new();              // branch fix conflict đang chạy
        public DateTime? LastRun;
        public string LastText = "";
        public bool LastError;
        public GerritClient? Gc; public string GcKey = "";
        public GitLabClient? Gl; public string GlKey = "";
    }

    private sealed class AutoJob
    {
        public required GlProject Project;
        public required string Source, Target;
        public string? Sha;                       // commit cần có bên GitLab trước khi tạo MR
        public DateTime Since = DateTime.Now;
        public readonly List<string> Changes = new();
    }

    /// <summary>MR đã merge (hoặc đang chờ auto-merge): theo dõi pipeline tới khi build xong.</summary>
    private sealed class PipeWatch
    {
        public required GlProject Project;
        public required long Iid;
        public required string Label, MrUrl, Changes;
        public string? Sha;                       // commit merge trên branch đích (biết khi MR đã merge)
        public DateTime Since = DateTime.Now;
    }

    private static readonly TimeSpan PipeWait = TimeSpan.FromHours(3);
    private static readonly HashSet<string> PipeDone = new() { "success", "failed", "canceled", "skipped" };

    private readonly Dictionary<string, AutoState> _auto = new();

    private AutoState AutoOf(string wsId)
    {
        lock (_auto) return _auto.TryGetValue(wsId, out var a) ? a : _auto[wsId] = new AutoState();
    }

    private void StartAllAuto()
    {
        foreach (var ws in _cfg.Workspaces.Where(w => w.Kind == "gerrit" && w.Auto?.Enabled == true))
            if (AutoOf(ws.Id).Loop == null) StartAuto(ws);
    }

    private void StartAuto(Workspace ws)
    {
        StopAuto(ws.Id);
        var a = AutoOf(ws.Id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        a.Loop = cts;
        Log($"[Auto {ws.Name}] Bật{(ws.Auto!.DryRun ? " (chạy thử – chỉ ghi nhật ký)" : "")}, quét mỗi {Interval(ws)} giây", LogKind.Info);
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await AutoTickAsync(ws, cts.Token);
                try { await Task.Delay(TimeSpan.FromSeconds(Interval(ws)), cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    private static int Interval(Workspace ws) => Math.Clamp(ws.Auto?.IntervalSec ?? 60, 30, 3600);

    private void StopAuto(string wsId)
    {
        var a = AutoOf(wsId);
        a.Loop?.Cancel();
        a.Loop = null;
    }

    /// <summary>Đổi cấu hình: quên những gì đã xử lý / đã báo để xét lại theo cấu hình mới.</summary>
    private static void ResetAuto(AutoState a)
    {
        a.Handled.Clear();
        a.Noted.Clear();
        a.Jobs.Clear();
        a.Pipes.Clear();
    }

    private object AutoView(Workspace ws)
    {
        var f = ws.Auto ?? new AutoFlow();
        var a = AutoOf(ws.Id);
        return new
        {
            enabled = f.Enabled, dryRun = f.DryRun, interval = f.IntervalSec, branch = f.Branch ?? "",
            reviewers = string.Join(", ", f.Reviewers), owners = string.Join(", ", f.Owners),
            gitlabWs = f.GitLabWs ?? "", gitlabSource = f.GitLabSource, gitlabTarget = f.GitLabTarget ?? "",
            gitlabMerge = f.GitLabMerge, hasTeams = f.TeamsWebhook != null, notifySuccess = f.NotifySuccess,
            hasZalo = f.ZaloToken != null, zaloChat = f.ZaloChatId ?? "", notifyPipeline = f.NotifyPipeline,
            conflictFix = f.ConflictFix, conflictBranch = f.ConflictBranch, claudePath = f.ClaudePath ?? "",
            running = a.Loop != null, lastRun = a.LastRun, text = a.LastText, error = a.LastError, jobs = a.Jobs.Count,
        };
    }

    private void SendAutoState(Workspace ws) => Send(new { type = "autoState", wsId = ws.Id, auto = AutoView(ws) });

    private static List<string> SplitList(string s) =>
        s.Split(new[] { ',', ';', '\n' },StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void SaveAuto(JsonNode m)
    {
        if (_ws.Kind != "gerrit") throw new GitLabException("Auto submit chỉ dùng cho workspace Gerrit.");
        var f = _ws.Auto ??= new AutoFlow();
        f.Enabled = Bool(m, "enabled");
        f.DryRun = Bool(m, "dryRun");
        f.IntervalSec = Math.Clamp((int?)m["interval"] ?? 60, 30, 3600);
        f.Branch = Str(m, "branch");
        f.Reviewers = SplitList(Str(m, "reviewers"));
        f.Owners = SplitList(Str(m, "owners"));
        f.GitLabWs = Str(m, "gitlabWs");
        f.GitLabSource = Str(m, "gitlabSource") is { Length: > 0 } src ? src : "{branch}";
        f.GitLabTarget = Str(m, "gitlabTarget");
        f.GitLabMerge = Bool(m, "gitlabMerge");
        f.NotifySuccess = Bool(m, "notifySuccess");
        f.NotifyPipeline = Bool(m, "notifyPipeline");
        f.ConflictFix = Bool(m, "conflictFix");
        f.ConflictBranch = Str(m, "conflictBranch") is { Length: > 0 } cb ? cb : "{branch}_fix_conflict";
        f.ClaudePath = Str(m, "claudePath") is { Length: > 0 } cp ? cp : null;
        if (!f.ConflictBranch.Contains("{branch}"))
            throw new GitLabException("Tên branch fix conflict phải có {branch} (vd {branch}_fix_conflict), để mỗi feature 1 branch riêng.");
        var teams = Str(m, "teams");
        if (Bool(m, "clearTeams")) f.TeamsWebhook = null;
        else if (teams.Length > 0)
        {
            if (!Uri.TryCreate(teams, UriKind.Absolute, out var u) || u.Scheme != "https")
                throw new GitLabException("Link Teams webhook phải bắt đầu bằng https://");
            f.TeamsWebhook = Platform.ProtectToken(_ws.TeamsKey, teams);
        }
        var zalo = Str(m, "zaloToken");
        if (Bool(m, "clearZalo")) f.ZaloToken = null;
        else if (zalo.Length > 0) f.ZaloToken = Platform.ProtectToken(_ws.ZaloKey, zalo);
        f.ZaloChatId = Str(m, "zaloChat");
        if (f.Enabled && (f.Reviewers.Count == 0 || f.Owners.Count == 0))
            throw new GitLabException("Nhập reviewer (+1) và account dev trước khi bật Auto.");
        if (f.Enabled && f.GitLabWs?.Length > 0 && string.IsNullOrEmpty(f.GitLabTarget))
            throw new GitLabException("Nhập branch đích bên GitLab (vd testing2), hoặc chọn \"Không merge GitLab\".");
        TrySave();

        var a = AutoOf(_ws.Id);
        ResetAuto(a);
        if (f.Enabled) StartAuto(_ws);
        else if (a.Loop != null) { StopAuto(_ws.Id); Log($"[Auto {_ws.Name}] Đã tắt", LogKind.Warn); }
        SendWsList();
        SendAutoState(_ws);
        Send(new { type = "toast", text = f.Enabled ? (f.DryRun ? "Đã bật Auto (chạy thử)" : "Đã bật Auto") : "Đã lưu, Auto đang tắt" });
    }

    private void AutoRunNow()
    {
        var ws = _ws;
        if (ws.Kind != "gerrit" || ws.Auto == null) throw new GitLabException("Lưu cấu hình Auto trước đã.");
        _ = Task.Run(() => AutoTickAsync(ws, _life.Token));
    }

    private async Task AutoTestClaudeAsync(string path)
    {
        Send(new { type = "toast", text = "Đang gọi thử Claude Code…" });
        var err = await ConflictFixer.CheckClaudeAsync(path.Length > 0 ? path : null, _life.Token);
        if (err != null) throw new GitLabException(err);
        Log($"[Claude] Chạy được: {ConflictFixer.FindClaude(path.Length > 0 ? path : null)}", LogKind.Success);
        Send(new { type = "toast", text = "Claude Code chạy được – auto-fix conflict sẵn sàng" });
    }

    private async Task AutoTestTeamsAsync(string typed)
    {
        var url = typed.Length > 0 ? typed
            : _ws.Auto?.TeamsWebhook is { } stored ? Platform.UnprotectToken(_ws.TeamsKey, stored) : null;
        if (string.IsNullOrEmpty(url)) throw new GitLabException("Dán link Teams webhook trước đã.");
        await Teams.SendAsync(url, $"GitLab MR · Auto {_ws.Name}", "Tin nhắn thử: webhook hoạt động.", null, false, _life.Token);
        Send(new { type = "toast", text = "Đã gửi tin thử vào Teams" });
    }

    private string ZaloTokenOf(string typed) =>
        typed.Length > 0 ? typed
        : (_ws.Auto?.ZaloToken is { } stored ? Platform.UnprotectToken(_ws.ZaloKey, stored) : null)
          ?? throw new GitLabException("Dán bot token Zalo trước đã.");

    /// <summary>Chờ tối đa ~20 giây tin nhắn mới gửi tới bot, lấy chat ID của người / nhóm đó.</summary>
    private async Task AutoZaloChatAsync(string typed)
    {
        var token = ZaloTokenOf(typed);
        Send(new { type = "toast", text = "Đang chờ tin nhắn… nhắn riêng cho bot, hoặc trong nhóm gõ @ chọn tên bot rồi nhắn" });
        var chats = await Zalo.GetRecentChatsAsync(token, _life.Token);
        if (chats.Count == 0) throw new GitLabException("Chưa thấy tin nhắn nào tới bot – nhắn cho bot rồi bấm lại.");
        var c = chats[0];
        Log($"[Zalo] Chat ID {c.Id} · {c.Type} · {c.Name}: \"{c.Text}\"", LogKind.Info);
        Send(new { type = "zaloChat", id = c.Id, chatType = c.Type, name = c.Name });
    }

    private async Task AutoTestZaloAsync(string typed, string chatId)
    {
        await Zalo.SendAsync(ZaloTokenOf(typed), chatId, $"GitLab MR · Auto {_ws.Name}\nTin nhắn thử: Zalo Bot hoạt động.", _life.Token);
        Send(new { type = "toast", text = "Đã gửi tin thử qua Zalo" });
    }

    // ================================================================ 1 lượt quét
    private async Task AutoTickAsync(Workspace ws, CancellationToken life)
    {
        var a = AutoOf(ws.Id);
        if (ws.Auto == null || !await a.Gate.WaitAsync(0)) return; // lượt trước chưa xong
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(life);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            a.LastText = await AutoScanAsync(ws, a, cts.Token);
            a.LastError = false;
            a.Noted.RemoveWhere(k => k.StartsWith("err|"));
        }
        catch (OperationCanceledException) when (life.IsCancellationRequested) { }
        catch (Exception ex)
        {
            a.LastError = true;
            a.LastText = ex is OperationCanceledException ? "Quá 5 phút chưa xong lượt quét" : Describe(ex);
            if (a.Noted.Add("err|" + a.LastText)) // cùng 1 lỗi chỉ báo 1 lần tới khi hết lỗi
                await AlertAsync(ws, "Auto lỗi", a.LastText, null);
        }
        finally
        {
            a.LastRun = DateTime.Now;
            a.Gate.Release();
            SendAutoState(ws);
        }
    }

    private async Task<string> AutoScanAsync(Workspace ws, AutoState a, CancellationToken ct)
    {
        var f = ws.Auto!;
        var tag = $"[Auto {ws.Name}]";
        if (f.Reviewers.Count == 0 || f.Owners.Count == 0) throw new GitLabException("Chưa nhập reviewer / account dev.");
        var gc = AutoGerrit(ws, a);

        var q = "status:open -is:wip (" + string.Join(" OR ", f.Owners.Select(o => $"owner:\"{o}\"")) + ")";
        if (!string.IsNullOrWhiteSpace(f.Branch)) q += $" branch:\"{f.Branch.Trim()}\"";
        var changes = await gc.QueryChangesAsync(q, ct);

        // Chỉ repo thuộc workspace (theo link đã nhập); chưa có cache thì không lọc
        var inWs = WsCache.Load(ws.Id)?.Projects.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        if (inWs is { Count: > 0 }) changes = changes.Where(c => inWs.Contains(c.Project)).ToList();

        var submitted = 0;
        foreach (var c in changes.OrderBy(c => c.Project, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Number))
        {
            ct.ThrowIfCancellationRequested();
            var key = ChangeKey(c.Project, c.Number);
            var rev = c.CurrentRevision ?? c.Updated ?? "";
            if (a.Handled.TryGetValue(key, out var done) && done == rev) continue;

            var votes = c.Labels?.GetValueOrDefault("Code-Review")?.All ?? new();
            var by = votes.FirstOrDefault(v => v.Value >= 1 && Matches(f.Reviewers, v.Username, v.Email, v.Name));
            if (by == null) continue;                                          // reviewer chưa +1
            if (!Matches(f.Owners, c.Owner?.Username, c.Owner?.Email, c.Owner?.Name)) continue;

            var label = $"{c.Project} #{c.Number}";
            var url = gc.ChangeUrl(c.Project, c.Number);
            var who = by.Username ?? by.Name ?? "?";
            var owner = c.Owner?.Name ?? c.Owner?.Username ?? "?";

            var neg = votes.Where(v => v.Value < 0).Select(v => $"{v.Username ?? v.Name} {v.Value}").ToList();
            if (neg.Count > 0)
            {
                Once(a, $"neg|{key}|{rev}", $"{tag} {label}: có phiếu {string.Join(", ", neg)} → không tự submit", LogKind.Warn);
                continue;
            }
            if (c.Mergeable == false)
            {
                a.Handled[key] = rev;
                await AlertAsync(ws, "Conflict trên Gerrit – cần fix", $"{label}: {c.Subject}\nDev: {owner} · branch {c.Branch}", url);
                continue;
            }
            var missing = MissingRequirements(c);
            if (missing.Count > 0)
            {
                Once(a, $"wait|{key}|{rev}|{string.Join(",", missing)}", $"{tag} {label}: {who} đã +1, chờ {string.Join(", ", missing)}", LogKind.Info);
                continue;
            }

            if (f.DryRun)
            {
                a.Handled[key] = rev;
                Log($"{tag} [Thử] Sẽ +2 & submit {label} ({who} +1, dev {owner}): {c.Subject}", LogKind.Success);
                await PreviewGitLabAsync(ws, a, c, ct);
                continue;
            }

            Log($"{tag} {label}: {who} đã +1 → +2 & submit", LogKind.Info);
            var res = await SubmitOneAsync(gc, c, (msg, k) => Log($"{tag} {label}: {msg}", k), ct);
            a.Handled[key] = rev;
            if (res.Outcome == MrOutcome.Submitted)
            {
                submitted++;
                Log($"{tag} {label}: {res.Message} {url}", LogKind.Success);
                if (f.NotifySuccess) await NotifyAsync(ws, "Đã submit", $"{label}: {c.Subject}\nDev: {owner} · {who} +1", url);
                await EnqueueGitLabAsync(ws, a, gc, c, ct);
            }
            else
            {
                var conflict = res.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase);
                await AlertAsync(ws, conflict ? "Conflict trên Gerrit – cần fix" : "Không submit được",
                    $"{label}: {c.Subject}\nDev: {owner}\n{res.Message}", url);
            }
        }

        var jobs = await RunGitLabJobsAsync(ws, a, ct);
        var pipes = await RunPipeWatchesAsync(ws, a, ct);
        return $"{changes.Count} change của dev" + (submitted > 0 ? $", vừa submit {submitted}" : "") + (jobs > 0 ? $", {jobs} MR chờ sync" : "")
               + (pipes > 0 ? $", {pipes} pipeline đang chờ" : "");
    }

    private static bool Matches(List<string> who, params string?[] ids) =>
        who.Any(w => ids.Any(id => !string.IsNullOrEmpty(id) &&
            (id.Equals(w, StringComparison.OrdinalIgnoreCase)
             || (id.Contains('@') && id.Split('@')[0].Equals(w, StringComparison.OrdinalIgnoreCase)))));

    private void Once(AutoState a, string key, string text, LogKind kind)
    {
        if (a.Noted.Add(key)) Log(text, kind);
    }

    // ================================================================ GitLab sau khi submit
    private (Workspace Ws, GitLabClient Gl, List<CachedProject> Projects)? AutoGitLab(Workspace gws, AutoState a)
    {
        var f = gws.Auto!;
        if (string.IsNullOrEmpty(f.GitLabWs) || string.IsNullOrEmpty(f.GitLabTarget)) return null;
        var ws = _cfg.Workspaces.FirstOrDefault(w => w.Id == f.GitLabWs && w.Kind == "gitlab")
                 ?? throw new GitLabException("Workspace GitLab đã chọn cho Auto không còn – mở ⚙ chọn lại.");
        var token = AppConfig.GetToken(ws) ?? throw new GitLabException($"Workspace '{ws.Name}' chưa có token.");
        if (!ProjectRef.TryParse(ws.Sources.FirstOrDefault(), out var r, out var err)) throw new GitLabException(err);
        var key = $"{r!.BaseUrl}|{token}|{ws.IgnoreSsl}";
        if (a.Gl == null || a.GlKey != key) { a.Gl?.Dispose(); a.Gl = new GitLabClient(r.BaseUrl, token, ws.IgnoreSsl); a.GlKey = key; }
        var projects = WsCache.Load(ws.Id)?.Projects
                       ?? throw new GitLabException($"Mở tab '{ws.Name}' 1 lần để app tải danh sách repo GitLab.");
        return (ws, a.Gl, projects);
    }

    private GerritClient AutoGerrit(Workspace ws, AutoState a)
    {
        var token = AppConfig.GetToken(ws) ?? throw new GitLabException("Workspace Gerrit chưa có HTTP password.");
        if (!GerritRef.TryParse(ws.Sources.FirstOrDefault(), out var g, out var err)) throw new GitLabException(err);
        var user = ws.Username ?? g!.User ?? "";
        var key = $"{g!.BaseUrl}|{user}|{token}|{ws.IgnoreSsl}";
        if (a.Gc == null || a.GcKey != key) { a.Gc?.Dispose(); a.Gc = new GerritClient(g.BaseUrl, user, token, ws.IgnoreSsl); a.GcKey = key; }
        return a.Gc;
    }

    /// <summary>Repo GitLab ứng với project Gerrit: cùng đường dẫn, hoặc cùng phần sau group đầu (peoplex/api/x ↔ peoplex2024/api/x).</summary>
    private static CachedProject? MapProject(List<CachedProject> list, string gerritPath)
    {
        var exact = list.FirstOrDefault(p => p.Path.Equals(gerritPath, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        // Chỉ khác group đầu, cùng số cấp: peoplex/api/x -> peoplex2024/api/x (không lấy peoplex2024/release/api/x)
        var segs = gerritPath.Split('/');
        if (segs.Length < 2) return null;
        var rest = "/" + string.Join('/', segs[1..]);
        var hits = list.Where(p => p.Path.Split('/').Length == segs.Length
                                   && p.Path.EndsWith(rest, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count == 1 ? hits[0] : null; // 0 hoặc nhiều repo khớp: không đoán, báo để kiểm tra
    }

    private static GlProject ToGl(CachedProject p) => new()
    {
        Id = p.Id, Name = p.Name, PathWithNamespace = p.Path, WebUrl = p.WebUrl, DefaultBranch = p.DefaultBranch,
    };

    private async Task PreviewGitLabAsync(Workspace ws, AutoState a, GerritClient.ChangeInfo c, CancellationToken ct)
    {
        var tag = $"[Auto {ws.Name}]";
        var g = AutoGitLab(ws, a);
        if (g == null) return;
        var f = ws.Auto!;
        var src = f.GitLabSource.Replace("{branch}", c.Branch);
        var p = MapProject(g.Value.Projects, c.Project);
        if (p == null) { Log($"{tag} [Thử] Không thấy repo nào ứng với '{c.Project}' trong '{g.Value.Ws.Name}'", LogKind.Warn); return; }
        var check = await MrWorkflow.CheckAsync(g.Value.Gl, ToGl(p), src, f.GitLabTarget!, ct);
        Log($"{tag} [Thử] Rồi {(f.GitLabMerge ? "tạo & merge" : "tạo")} MR {src} → {f.GitLabTarget} ở {p.Path} (hiện tại: {check.Message})", LogKind.Info);
    }

    private async Task EnqueueGitLabAsync(Workspace ws, AutoState a, GerritClient gc, GerritClient.ChangeInfo c, CancellationToken ct)
    {
        var tag = $"[Auto {ws.Name}]";
        try
        {
            var g = AutoGitLab(ws, a);
            if (g == null) return;
            var f = ws.Auto!;
            var src = f.GitLabSource.Replace("{branch}", c.Branch);
            var p = MapProject(g.Value.Projects, c.Project);
            if (p == null)
            {
                await AlertAsync(ws, "Không tìm thấy repo GitLab", $"Không có repo nào trong '{g.Value.Ws.Name}' ứng với '{c.Project}' – chưa tạo MR.", null);
                return;
            }
            var job = a.Jobs.FirstOrDefault(j => j.Project.Id == p.Id && j.Source == src && j.Target == f.GitLabTarget);
            if (job == null) a.Jobs.Add(job = new AutoJob { Project = ToGl(p), Source = src, Target = f.GitLabTarget! });
            job.Sha = await gc.GetBranchRevisionAsync(c.Project, c.Branch, ct) ?? job.Sha;
            job.Changes.Add($"#{c.Number}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AlertAsync(ws, "Lỗi chuẩn bị MR GitLab", $"{c.Project} #{c.Number}: {Describe(ex)}", null);
        }
    }

    /// <summary>Tạo / merge MR cho các repo vừa submit (khi code đã sync sang GitLab). Trả về số MR còn chờ.</summary>
    private async Task<int> RunGitLabJobsAsync(Workspace ws, AutoState a, CancellationToken ct)
    {
        if (a.Jobs.Count == 0) return 0;
        var tag = $"[Auto {ws.Name}]";
        var g = AutoGitLab(ws, a);
        if (g == null) { a.Jobs.Clear(); return 0; }
        var f = ws.Auto!;
        foreach (var job in a.Jobs.ToList())
        {
            var p = job.Project;
            var label = $"{p.PathWithNamespace} {job.Source} → {job.Target}";
            var late = DateTime.Now - job.Since > SyncWait;
            try
            {
                if (job.Sha != null && !late && !await g.Value.Gl.CommitExistsAsync(p.Id, job.Sha, ct))
                {
                    Once(a, $"sync|{p.Id}|{job.Sha}", $"{tag} {label}: chờ code từ Gerrit sync sang GitLab…", LogKind.Info);
                    continue;
                }
                a.Jobs.Remove(job);
                if (late && job.Sha != null)
                {
                    Log($"{tag} {label}: sau {SyncWait.TotalMinutes:0} phút chưa thấy commit {job.Sha[..8]}, vẫn thử tạo MR", LogKind.Warn);
                    await NotifyAsync(ws, "GitLab: code chưa sync sang",
                        $"{label}\nSau {SyncWait.TotalMinutes:0} phút chưa thấy commit {job.Sha[..8]} của Gerrit {string.Join(", ", job.Changes)} – vẫn thử tạo MR.", null, pending: true);
                }

                var opts = new MrOptions(job.Source, job.Target, _cfg.TitleTemplate,
                    $"Tự tạo sau khi submit Gerrit {string.Join(", ", job.Changes)}", f.GitLabMerge, _cfg.AutoMerge, false, _cfg.Squash);
                var res = await MrWorkflow.RunAsync(g.Value.Gl, p, opts, (msg, k) => Log($"{tag} [{p.PathWithNamespace}] {msg}", k), ct);
                var changes = string.Join(", ", job.Changes);
                if (f.NotifyPipeline && res.Outcome is MrOutcome.Merged or MrOutcome.AutoMerge && MrIid(res.Url) is { } iid)
                    a.Pipes.Add(new PipeWatch { Project = p, Iid = iid, Label = label, MrUrl = res.Url!, Changes = changes });
                if (res.Outcome == MrOutcome.Merged)
                {
                    Log($"{tag} {label}: {res.Message}", LogKind.Success);
                    if (f.NotifySuccess) await NotifyAsync(ws, "GitLab: " + res.Message, $"{label}\nGerrit {changes}", res.Url);
                }
                else if (res.Outcome is MrOutcome.AutoMerge or MrOutcome.Created or MrOutcome.AlreadyOpen or MrOutcome.NoChanges)
                {
                    // Gerrit đã submit nhưng GitLab chưa merge xong: luôn báo để biết mà theo dõi
                    Log($"{tag} {label}: {res.Message}", LogKind.Warn);
                    var (title, note) = res.Outcome switch
                    {
                        MrOutcome.AutoMerge => ("GitLab: pipeline đang chạy, chưa merge", "Sẽ tự merge khi pipeline pass."),
                        MrOutcome.NoChanges => ("GitLab: chưa có commit để tạo MR",
                            $"Gerrit đã submit {changes} nhưng '{job.Source}' chưa có commit mới so với '{job.Target}' (chưa sync hoặc đã merge trước đó)."),
                        _ => ("GitLab: đã có MR, chưa merge", f.GitLabMerge ? res.Message : "Đang tắt Merge – merge tay khi sẵn sàng."),
                    };
                    await NotifyAsync(ws, title, $"{label}\nGerrit {changes}\n{note}", res.Url, pending: true);
                }
                else
                {
                    var conflict = res.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase);
                    if (conflict && f.ConflictFix && IsFixTarget(job.Target)) StartConflictFix(ws, a, g.Value.Ws, g.Value.Gl, p, job.Source, job.Target, changes, res.Url);
                    else await AlertAsync(ws, conflict ? "Conflict trên GitLab – cần fix" : "GitLab chưa merge được", $"{label}\n{res.Message}", res.Url);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Lỗi mạng tạm thời: giữ lại thử lượt sau, quá thời gian chờ thì báo và bỏ
                if (late) { a.Jobs.Remove(job); await AlertAsync(ws, "GitLab lỗi", $"{label}\n{Describe(ex)}", null); }
                else Once(a, $"jerr|{p.Id}|{Describe(ex)}", $"{tag} {label}: {Describe(ex)} – thử lại lượt sau", LogKind.Warn);
            }
        }
        return a.Jobs.Count;
    }

    /// <summary>
    /// Conflict: branch trung gian {branch}_{target} từ branch nguồn, merge branch đích vào, Claude sửa conflict,
    /// push rồi tạo MR branch đó → đích và merge. Branch nguồn (feature) không bị merge môi trường vào.
    /// Chạy nền (có thể mất vài phút), không chặn lượt quét.
    /// </summary>
    private void StartConflictFix(Workspace ws, AutoState a, Workspace gws, GitLabClient gl, GlProject p,
        string source, string target, string changes, string? mrUrl)
    {
        var f = ws.Auto!;
        var tag = $"[Auto {ws.Name}]";
        var fix = f.ConflictBranch.Replace("{branch}", source).Replace("{target}", target);
        var label = $"{p.PathWithNamespace} {source} → {target}";
        var key = $"{p.Id}|{fix}";
        lock (a.Fixing)
            if (!a.Fixing.Add(key)) { Log($"{tag} {label}: đang auto-fix qua {fix}, bỏ qua lần này", LogKind.Info); return; }
        void L(string msg, LogKind k) => Log($"{tag} [{p.PathWithNamespace}] {msg}", k);
        L($"Conflict → auto-fix qua branch {fix}", LogKind.Warn);
        _ = Task.Run(async () =>
        {
            var ct = _life.Token;
            var told = false;
            try
            {
                var token = AppConfig.GetToken(gws) ?? throw new GitLabException($"Workspace '{gws.Name}' chưa có token.");
                if (!ProjectRef.TryParse(gws.Sources.FirstOrDefault(), out var r, out var err)) throw new GitLabException(err);
                var res = await ConflictFixer.FixAsync(r!.BaseUrl, token, gws.IgnoreSsl, p, source, target, fix, f.ClaudePath,
                    async files =>
                    {
                        told = true;
                        await AlertAsync(ws, "Conflict trên GitLab – đang auto-fix",
                            $"{label}\nGerrit {changes}\nConflict ở: {FileList(files)}\nĐang tạo branch {fix} (merge {target} vào) và nhờ Claude sửa, {source} giữ nguyên.", mrUrl);
                    }, L, ct);
                if (!res.Ok)
                {
                    if (res.BranchExists)
                        await AlertAsync(ws, "Conflict trên GitLab – branch fix đã có, cần xem",
                            $"{label}\nGerrit {changes}\n{res.Message}", BranchUrl(p, fix) ?? mrUrl);
                    else
                        await AlertAsync(ws, "Auto-fix conflict không được – cần fix tay",
                            $"{label}\nGerrit {changes}\n{res.Message}" + (res.Files.Count > 0 ? $"\nFile: {FileList(res.Files)}" : "")
                            + (res.Summary is { } s0 ? $"\nClaude: {s0}" : ""), mrUrl);
                    return;
                }

                var desc = $"Auto-fix conflict {source} → {target} (Gerrit {changes}).\n\nFile conflict: {FileList(res.Files)}\n\n{res.Summary}";
                var opts = new MrOptions(fix, target, $"merge branch {source} to {target}", desc, f.GitLabMerge && res.Safe, _cfg.AutoMerge, true, false);
                var mr = await MrWorkflow.RunAsync(gl, p, opts, L, ct);
                var fixLabel = $"{p.PathWithNamespace} {fix} → {target}";
                var text = $"{fixLabel}\nGerrit {changes}" + (res.Files.Count > 0 ? $"\nĐã sửa: {FileList(res.Files)}" : "")
                           + (res.Summary is { } s ? $"\nClaude: {s}" : "");
                if (f.NotifyPipeline && mr.Outcome is MrOutcome.Merged or MrOutcome.AutoMerge && MrIid(mr.Url) is { } iid)
                    a.NewPipes.Enqueue(new PipeWatch { Project = p, Iid = iid, Label = fixLabel, MrUrl = mr.Url!, Changes = changes });
                if (!res.Safe && mr.Outcome is MrOutcome.Created or MrOutcome.AlreadyOpen)
                    await AlertAsync(ws, "Đã fix conflict nhưng chưa merge – cần duyệt", $"{text}\n{res.Message}\nApp không tự merge: xem diff MR rồi merge tay.", mr.Url);
                else if (mr.Outcome == MrOutcome.Merged)
                {
                    L($"Auto-fix conflict xong, {mr.Message}", LogKind.Success);
                    await NotifyAsync(ws, "Đã auto-fix conflict & merge", text, mr.Url);
                }
                else if (mr.Outcome is MrOutcome.AutoMerge or MrOutcome.Created or MrOutcome.AlreadyOpen)
                    await NotifyAsync(ws, "Đã auto-fix conflict, MR chưa merge", $"{text}\n{mr.Message}", mr.Url, pending: true);
                else
                    await AlertAsync(ws, "Auto-fix conflict xong nhưng chưa merge được", $"{text}\n{mr.Message}", mr.Url ?? mrUrl);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await AlertAsync(ws, told ? "Auto-fix conflict lỗi – cần fix tay" : "Conflict trên GitLab – auto-fix lỗi",
                    $"{label}\n{Describe(ex)}", mrUrl);
            }
            finally { lock (a.Fixing) a.Fixing.Remove(key); }
        });
    }

    /// <summary>Auto-fix conflict chỉ dành cho branch môi trường testing2 (quy trình ppx-merge-testing).</summary>
    private static bool IsFixTarget(string target) => target.Equals("testing2", StringComparison.OrdinalIgnoreCase);

    private static string? BranchUrl(GlProject p, string branch) =>
        string.IsNullOrEmpty(p.WebUrl) ? null : $"{p.WebUrl.TrimEnd('/')}/-/tree/{Uri.EscapeDataString(branch).Replace("%2F", "/")}";

    private static string FileList(List<string> files) =>
        files.Count <= 8 ? string.Join(", ", files) : string.Join(", ", files.Take(8)) + $" … (+{files.Count - 8})";

    private static long? MrIid(string? url)
    {
        var i = url?.LastIndexOf("/merge_requests/", StringComparison.Ordinal) ?? -1;
        return i >= 0 && long.TryParse(url![(i + 16)..].Split('/', '#', '?')[0], out var n) ? n : null;
    }

    /// <summary>Theo dõi pipeline sau khi merge: build xong (pass / fail / huỷ) thì báo 1 lần. Trả về số pipeline còn chờ.</summary>
    private async Task<int> RunPipeWatchesAsync(Workspace ws, AutoState a, CancellationToken ct)
    {
        while (a.NewPipes.TryDequeue(out var nw)) a.Pipes.Add(nw);
        if (a.Pipes.Count == 0) return 0;
        var g = AutoGitLab(ws, a);
        if (g == null) { a.Pipes.Clear(); return 0; }
        var gl = g.Value.Gl;
        foreach (var w in a.Pipes.ToList())
        {
            var p = w.Project;
            try
            {
                if (w.Sha == null)
                {
                    // Chưa merge (đang chờ auto-merge): MR merge xong mới có commit trên branch đích
                    var mr = await gl.GetMrAsync(p.Id, w.Iid, ct);
                    if (mr.State == "merged") w.Sha = mr.MergeCommitSha ?? mr.SquashCommitSha ?? mr.Sha;
                    else if (mr.State is "closed" or "locked") { a.Pipes.Remove(w); continue; }
                    else
                    {
                        if (mr.HeadPipeline?.Status is "failed" or "canceled")
                        {
                            a.Pipes.Remove(w);
                            await AlertAsync(ws, $"Pipeline MR {mr.HeadPipeline.Status} – chưa merge",
                                $"{w.Label}\nGerrit {w.Changes}\nAuto-merge sẽ không chạy cho tới khi pipeline pass.", mr.HeadPipeline.WebUrl);
                        }
                        else if (DateTime.Now - w.Since > PipeWait) a.Pipes.Remove(w);
                        continue;
                    }
                }
                if (w.Sha == null) { a.Pipes.Remove(w); continue; }

                var pl = (await gl.GetPipelinesAsync(p.Id, w.Sha, ct)).FirstOrDefault();
                if (pl == null || !PipeDone.Contains(pl.Status))
                {
                    if (DateTime.Now - w.Since > PipeWait)
                    {
                        a.Pipes.Remove(w);
                        await NotifyAsync(ws, "Pipeline: quá 3 giờ chưa xong", $"{w.Label}\nTrạng thái:{pl?.Status ?? "không thấy pipeline"}", pl?.WebUrl ?? w.MrUrl, pending: true);
                    }
                    else if (pl != null) Once(a, $"pipe|{pl.Id}|{pl.Status}", $"[Auto {ws.Name}] {w.Label}: pipeline #{pl.Id} {pl.Status}…", LogKind.Info);
                    continue;
                }

                a.Pipes.Remove(w);
                var text = $"{w.Label}\nGerrit{w.Changes} · pipeline #{pl.Id} trên {pl.Ref}";
                if (pl.Status == "success")
                {
                    Log($"[Auto {ws.Name}] {w.Label}: pipeline #{pl.Id} pass {pl.WebUrl}", LogKind.Success);
                    await NotifyAsync(ws, "Pipeline pass – build xong", text, pl.WebUrl);
                }
                else await AlertAsync(ws, $"Pipeline {pl.Status} – cần xem", text, pl.WebUrl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Once(a, $"perr|{p.Id}|{Describe(ex)}", $"[Auto {ws.Name}] {w.Label}: không đọc được pipeline – {Describe(ex)}", LogKind.Warn);
                if (DateTime.Now - w.Since > PipeWait) a.Pipes.Remove(w);
            }
        }
        return a.Pipes.Count;
    }

    // ================================================================ thông báo
    private Task AlertAsync(Workspace ws, string title, string text, string? url)
    {
        Log($"[Auto {ws.Name}] {title}: {text.Replace('\n', ' ')} {url}", LogKind.Error);
        Send(new { type = "toast", text = $"Auto: {title}", err = true });
        return TeamsAsync(ws, title, text, url, true);
    }

    /// <summary>Thông báo không phải lỗi: ✅ xong, ⏳ (pending) đã submit nhưng GitLab chưa merge xong.</summary>
    private Task NotifyAsync(Workspace ws, string title, string text, string? url, bool pending = false) =>
        TeamsAsync(ws, title, text, url, false, pending ? "⏳" : null);

    /// <summary>Gửi tới mọi kênh đã cấu hình (Teams, Zalo). Lỗi gửi chỉ ghi nhật ký.</summary>
    private async Task TeamsAsync(Workspace ws, string title, string text, string? url, bool error, string? icon = null)
    {
        var f = ws.Auto;
        if (f == null) return;
        var head = $"{icon ?? (error ? "⚠️" : "✅")} {title}";
        var hook = f.TeamsWebhook == null ? null : Platform.UnprotectToken(ws.TeamsKey, f.TeamsWebhook);
        if (!string.IsNullOrEmpty(hook))
        {
            try { await Teams.SendAsync(hook, head, text, url, error, _life.Token); }
            catch (Exception ex) { Log($"[Auto {ws.Name}] Không gửi được Teams: {Describe(ex)}", LogKind.Warn); }
        }
        var zalo = f.ZaloToken == null ? null : Platform.UnprotectToken(ws.ZaloKey, f.ZaloToken);
        if (!string.IsNullOrEmpty(zalo) && !string.IsNullOrEmpty(f.ZaloChatId))
        {
            try { await Zalo.SendAsync(zalo, f.ZaloChatId, $"{head} · {ws.Name}\n{text}" + (url != null ? $"\n{url}" : ""), _life.Token); }
            catch (Exception ex) { Log($"[Auto {ws.Name}] Không gửi được Zalo: {Describe(ex)}", LogKind.Warn); }
        }
    }
}

