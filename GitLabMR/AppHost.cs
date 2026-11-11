using System.Collections.Concurrent;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using Photino.NET;

namespace GitLabMR;

/// <summary>Cầu nối giữa ui.html (JS) và logic GitLab. Message JSON 2 chiều qua Photino.</summary>
public sealed class AppHost
{
    private const int MaxParallel = 4;
    private const string SelKey = "*";
    private static readonly JsonSerializerOptions Js = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly PhotinoWindow _window;
    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly object _sendLock = new();
    private GitLabClient? _client;
    private string _clientKey = "";
    private ProjectRef? _ref;
    private Dictionary<long, GlProject> _projects = new();
    private CancellationTokenSource? _cts;
    private volatile bool _ready;

    public AppHost(PhotinoWindow window) => _window = window;

    public void Shutdown()
    {
        _cts?.Cancel();
        TrySave();
        _client?.Dispose();
    }

    // ================================================================ messaging
    private void Send(object payload)
    {
        if (!_ready) return;
        var json = JsonSerializer.Serialize(payload, Js);
        lock (_sendLock)
        {
            try { _window.SendWebMessage(json); } catch { /* cửa sổ đã đóng */ }
        }
    }

    private void Log(string text, LogKind kind) =>
        Send(new { type = "log", kind = kind.ToString().ToLowerInvariant(), text, time = DateTime.Now.ToString("HH:mm:ss") });

    public async void OnMessage(string json)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(json); }
        catch { return; }
        if (m == null) return;

        try
        {
            switch ((string?)m["type"])
            {
                case "ready":
                    _ready = true;
                    SendInit();
                    break;
                case "connect":
                    await GuardAsync(ct => ConnectAsync((string?)m["urls"] ?? "", Str(m, "token"), Bool(m, "ignoreSsl"), ct));
                    break;
                case "run":
                    await GuardAsync(ct => RunBatchAsync(m, ct));
                    break;
                case "loadBranches":
                    var ids = Ids(m);
                    await GuardAsync(ct => LoadBranchesAsync(ids, ct));
                    break;
                case "stop":
                    _cts?.Cancel();
                    Log("Đang dừng… (project đang chạy dở sẽ xong bước hiện tại)", LogKind.Warn);
                    break;
                case "open":
                    Platform.OpenUrl(Str(m, "url"));
                    break;
                case "copy":
                    var text = (string?)m["text"] ?? "";
                    if (text.Length > 0) Platform.CopyText(text);
                    break;
                case "options":
                    ApplyOptions(m);
                    break;
                case "selection":
                    if (_ref != null)
                        _cfg.Selected[SelKey] = m["paths"]?.AsArray().Select(x => (string?)x ?? "").ToList() ?? new();
                    break;
                case "favorite":
                    var favPath = Str(m, "path");
                    _cfg.Favorites.RemoveAll(x => x.Equals(favPath, StringComparison.OrdinalIgnoreCase));
                    if (Bool(m, "on") && favPath.Length > 0) _cfg.Favorites.Add(favPath);
                    TrySave();
                    break;
            }
        }
        catch (Exception ex)
        {
            Send(new { type = "error", text = Describe(ex) });
        }
    }

    private static string Str(JsonNode m, string key) => ((string?)m[key] ?? "").Trim();
    private static bool Bool(JsonNode m, string key) => m[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static List<long> Ids(JsonNode m) => m["ids"]?.AsArray().Select(x => (long)x!).ToList() ?? new();

    private void SendInit() => Send(new
    {
        type = "init",
        sources = _cfg.Sources,
        tokenHosts = _cfg.Tokens.Keys,
        ignoreSsl = _cfg.IgnoreSsl,
        source = _cfg.LastSourceBranch,
        target = _cfg.LastTargetBranch,
        title = _cfg.TitleTemplate,
        merge = _cfg.Merge,
        autoMerge = _cfg.AutoMerge,
        removeSource = _cfg.RemoveSource,
        squash = _cfg.Squash,
        branches = _cfg.BranchHistory,
    });

    private void ApplyOptions(JsonNode m)
    {
        _cfg.LastSourceBranch = Str(m, "source");
        _cfg.LastTargetBranch = Str(m, "target");
        if (Str(m, "title").Length > 0) _cfg.TitleTemplate = Str(m, "title");
        _cfg.Merge = Bool(m, "merge");
        _cfg.AutoMerge = Bool(m, "autoMerge");
        _cfg.RemoveSource = Bool(m, "removeSource");
        _cfg.Squash = Bool(m, "squash");
        _cfg.IgnoreSsl = Bool(m, "ignoreSsl");
    }

    private void TrySave()
    {
        try { _cfg.Save(); } catch { /* ignore */ }
    }

    private async Task GuardAsync(Func<CancellationToken, Task> action)
    {
        if (_cts != null) return; // đang chạy việc khác
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        _cts = cts;
        Send(new { type = "busy", value = true });
        try
        {
            await action(cts.Token);
        }
        catch (Exception ex)
        {
            var text = ex is OperationCanceledException ? "Đã dừng / hết thời gian chờ." : Describe(ex);
            Log(text, LogKind.Error);
            Send(new { type = "error", text });
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            Send(new { type = "busy", value = false });
        }
    }

    // ================================================================ GitLab actions
    private GitLabClient EnsureClient(ProjectRef r, string token, bool ignoreSsl)
    {
        var key = $"{r.BaseUrl}|{token}|{ignoreSsl}";
        if (_client == null || key != _clientKey)
        {
            _client?.Dispose();
            _client = new GitLabClient(r.BaseUrl, token, ignoreSsl);
            _clientKey = key;
        }
        return _client;
    }

    /// <summary>Kết nối nhiều nguồn (mỗi dòng 1 URL group hoặc repo, cùng 1 GitLab host), gộp project và bỏ trùng.</summary>
    private async Task ConnectAsync(string urlsText, string token, bool ignoreSsl, CancellationToken ct)
    {
        var lines = urlsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (lines.Count == 0) throw new GitLabException("Nhập ít nhất 1 URL group hoặc repo.");

        var refs = new List<(string Url, ProjectRef Ref)>();
        foreach (var line in lines)
        {
            if (!ProjectRef.TryParse(line, out var pr, out var err)) throw new GitLabException($"{line}: {err}");
            refs.Add((line, pr!));
        }
        var first = refs[0].Ref;
        var otherHost = refs.FirstOrDefault(x => x.Ref.Host != first.Host);
        if (otherHost.Ref != null)
            throw new GitLabException($"Mọi URL phải cùng 1 GitLab ({first.Host}); '{otherHost.Url}' khác host.");

        if (token.Length == 0) token = _cfg.GetToken(first.Host) ?? "";
        if (token.Length == 0) throw new GitLabException("Chưa nhập access token.");

        var gl = EnsureClient(first, token, ignoreSsl);
        Log($"Kết nối {first.BaseUrl} …", LogKind.Info);
        var user = await gl.GetCurrentUserAsync(ct);

        var all = new Dictionary<long, GlProject>();
        var hidden = 0;
        foreach (var (url, r) in refs)
        {
            var (list, isSingle) = await ResolveSourceAsync(gl, r, ct);
            if (!isSingle) // chỉ ẩn library/shared khi lấy cả group; repo add tay thì giữ
            {
                hidden += list.Count(p => _cfg.IsExcluded(p.PathWithNamespace));
                list = list.Where(p => !_cfg.IsExcluded(p.PathWithNamespace)).ToList();
            }
            foreach (var p in list) all[p.Id] = p;
            Log($"{r.Path}: {list.Count} project", LogKind.Info);
        }
        var projects = all.Values.OrderBy(p => p.PathWithNamespace, StringComparer.OrdinalIgnoreCase).ToList();

        _ref = first;
        _projects = projects.ToDictionary(p => p.Id);

        _cfg.SetToken(first.Host, token);
        _cfg.Sources = lines;
        _cfg.LastProject = lines[0];
        foreach (var l in lines.AsEnumerable().Reverse()) _cfg.RememberProject(l);
        _cfg.LastProject = lines[0];
        _cfg.IgnoreSsl = ignoreSsl;
        TrySave();

        var favs = _cfg.Favorites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var saved = _cfg.Selected.TryGetValue(SelKey, out var sel) ? sel.ToHashSet(StringComparer.OrdinalIgnoreCase) : new();
        var hasFav = projects.Any(p => favs.Contains(p.PathWithNamespace));
        // Có favorite -> tự tick sẵn favorite; chưa có -> dùng lựa chọn lần trước
        bool IsSelected(GlProject p) => projects.Count == 1 || (hasFav ? favs.Contains(p.PathWithNamespace) : saved.Contains(p.PathWithNamespace));

        var prefix = CommonNamespace(projects.Select(p => p.PathWithNamespace));
        Send(new
        {
            type = "connected",
            scope = refs.Count == 1 ? first.Path : $"{refs.Count} nguồn",
            user = user.Username,
            hidden,
            sources = lines,
            tokenHosts = _cfg.Tokens.Keys,
            projects = projects.Select(p =>
            {
                var rel = p.PathWithNamespace[prefix.Length..];
                var cut = rel.LastIndexOf('/');
                return new
                {
                    id = p.Id,
                    path = p.PathWithNamespace,
                    ns = cut >= 0 ? rel[..(cut + 1)] : "",
                    name = cut >= 0 ? rel[(cut + 1)..] : rel,
                    defaultBranch = p.DefaultBranch,
                    webUrl = p.WebUrl,
                    fav = favs.Contains(p.PathWithNamespace),
                    selected = IsSelected(p),
                };
            }),
        });
        Log($"@{user.Username} · {projects.Count} project" + (hidden > 0 ? $" (ẩn {hidden})" : ""), LogKind.Success);

        // Tải luôn branch của các project đang tick (favorite) để gợi ý
        await LoadBranchesAsync(projects.Where(IsSelected).Select(p => p.Id).ToList(), ct);
    }

    /// <summary>URL là group -> mọi project; là repo -> 1 project; group không đọc được (chỉ member subgroup) -> lọc theo quyền.</summary>
    private static async Task<(List<GlProject> List, bool IsSingle)> ResolveSourceAsync(GitLabClient gl, ProjectRef r, CancellationToken ct)
    {
        try
        {
            var group = await gl.GetGroupAsync(r.Path, ct);
            return (await gl.GetGroupProjectsAsync(group.Id, ct), false);
        }
        catch (GitLabException ex) when (ex.Status == HttpStatusCode.NotFound) { }

        try
        {
            return (new List<GlProject> { await gl.GetProjectAsync(r.Path, ct) }, true);
        }
        catch (GitLabException ex) when (ex.Status == HttpStatusCode.NotFound) { }

        var under = await gl.GetProjectsUnderPathAsync(r.Path, ct);
        if (under.Count == 0)
            throw new GitLabException($"Không tìm thấy '{r.Path}', hoặc bạn không có quyền Developer ở project nào bên trong.");
        return (under, false);
    }

    /// <summary>Phần namespace chung của mọi project (vd "fis/peoplex/") để hiển thị đường dẫn ngắn gọn.</summary>
    private static string CommonNamespace(IEnumerable<string> paths)
    {
        string[]? common = null;
        foreach (var path in paths)
        {
            var segs = path.Split('/')[..^1];
            if (common == null) { common = segs; continue; }
            var n = 0;
            while (n < common.Length && n < segs.Length && common[n].Equals(segs[n], StringComparison.OrdinalIgnoreCase)) n++;
            common = common[..n];
        }
        return common is { Length: > 0 } ? string.Join('/', common) + "/" : "";
    }

    /// <summary>Gom tên branch của các project (chưa chọn project nào thì lấy tất cả), kèm số project có branch đó.</summary>
    private async Task LoadBranchesAsync(List<long> ids, CancellationToken ct)
    {
        if (_client == null || _projects.Count == 0) return;
        var gl = _client;
        var list = ids.Where(_projects.ContainsKey).Select(id => _projects[id]).ToList();
        if (list.Count == 0) list = _projects.Values.ToList();

        Send(new { type = "branchesLoading" });
        var counts = new ConcurrentDictionary<string, int>();
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(list.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try
            {
                foreach (var b in await gl.GetBranchesAsync(p.Id, ct))
                    counts.AddOrUpdate(b.Name, 1, (_, c) => c + 1);
            }
            catch (GitLabException ex)
            {
                Log($"[{p.PathWithNamespace}] Không lấy được branch: {ex.Message}", LogKind.Warn);
            }
            finally
            {
                gate.Release();
            }
        }));

        Send(new
        {
            type = "remoteBranches",
            total = list.Count,
            items = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new { name = kv.Key, count = kv.Value }),
        });
        Log($"Đã tải {counts.Count} branch từ {list.Count} project", LogKind.Info);
    }

    private async Task RunBatchAsync(JsonNode m, CancellationToken ct)
    {
        if (_client == null || _ref == null) throw new GitLabException("Kết nối trước đã.");
        var gl = _client;
        ApplyOptions(m);

        var dryRun = Bool(m, "dryRun");
        var source = Str(m, "source");
        var target = Str(m, "target");
        if (source.Length == 0 || target.Length == 0) throw new GitLabException("Nhập branch nguồn và branch đích.");
        if (source == target) throw new GitLabException("Branch nguồn và đích phải khác nhau.");

        var targets = Ids(m).Where(_projects.ContainsKey).Select(id => _projects[id]).ToList();
        if (targets.Count == 0) throw new GitLabException("Chưa chọn project nào.");

        var opts = new MrOptions(source, target,
            Str(m, "title").Length > 0 ? Str(m, "title") : "Merge branch '{source}' into '{target}'",
            (string?)m["description"] ?? "", Bool(m, "merge"), Bool(m, "autoMerge"), Bool(m, "removeSource"), Bool(m, "squash"));

        _cfg.RememberBranch(target);
        _cfg.RememberBranch(source);
        _cfg.Selected[SelKey] = targets.Select(p => p.PathWithNamespace).ToList();
        TrySave();
        Send(new { type = "branches", branches = _cfg.BranchHistory });

        var mode = dryRun ? "Kiểm tra" : opts.Merge ? "Tạo MR + merge" : "Tạo MR";
        Log($"── {mode}: {source} → {target} · {targets.Count} project", LogKind.Info);
        foreach (var p in targets) Send(new { type = "row", id = p.Id, state = "wait" });

        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = targets.Select(async p =>
        {
            void PLog(string msg, LogKind k) => Log($"[{p.PathWithNamespace}] {msg}", k);
            MrResult res;
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                res = new MrResult(MrOutcome.Failed, "Đã dừng");
                SendRow(p, res);
                return res;
            }
            try
            {
                Send(new { type = "row", id = p.Id, state = "run" });
                res = dryRun
                    ? await MrWorkflow.CheckAsync(gl, p, source, target, ct)
                    : await MrWorkflow.RunAsync(gl, p, opts, PLog, ct);
                if (dryRun) PLog(res.Message, res.IsGood ? LogKind.Success : LogKind.Info);
            }
            catch (Exception ex)
            {
                res = new MrResult(MrOutcome.Failed, ex is OperationCanceledException ? "Đã dừng" : Describe(ex));
                PLog(res.Message, LogKind.Error);
            }
            finally
            {
                gate.Release();
            }
            SendRow(p, res);
            return res;
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var counts = results.GroupBy(x => x.Outcome).ToDictionary(g => g.Key.ToString(), g => g.Count());
        Send(new { type = "done", dryRun, counts });
        Log($"── Xong {results.Length} project: " +
            string.Join(", ", results.GroupBy(x => x.Outcome).Select(g => $"{g.Key} {g.Count()}")), LogKind.Success);
    }

    private void SendRow(GlProject p, MrResult r) =>
        Send(new { type = "row", id = p.Id, state = "done", outcome = r.Outcome.ToString(), text = r.Message, url = r.Url });

    private static string Describe(Exception ex) => ex switch
    {
        GitLabException g => g.Message,
        HttpRequestException h when h.InnerException is AuthenticationException =>
            "Lỗi chứng chỉ SSL — thử bật 'Bỏ qua SSL'.",
        HttpRequestException h => $"Không kết nối được GitLab: {h.Message}",
        TaskCanceledException => "Hết thời gian chờ.",
        _ => ex.Message,
    };
}
