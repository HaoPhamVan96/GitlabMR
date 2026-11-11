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
    private static readonly JsonSerializerOptions Js = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Trạng thái runtime của 1 workspace: client + project đã load (để chuyển tab không phải tải lại).</summary>
    private sealed class WsState
    {
        public GitLabClient? Client;
        public string ClientKey = "";
        public Dictionary<long, GlProject> Projects = new();
        public object? Connected;  // payload "connected" gần nhất
        public object? Branches;   // payload "remoteBranches" gần nhất
    }

    private readonly PhotinoWindow _window;
    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly Dictionary<string, WsState> _states = new();
    private readonly object _sendLock = new();
    private Workspace _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _ready;

    public AppHost(PhotinoWindow window)
    {
        _window = window;
        _ws = _cfg.Workspaces.First(w => w.Id == _cfg.ActiveWorkspace);
    }

    private WsState St => State(_ws);
    private WsState State(Workspace ws) => _states.TryGetValue(ws.Id, out var s) ? s : _states[ws.Id] = new WsState();

    public void Shutdown()
    {
        _cts?.Cancel();
        TrySave();
        foreach (var s in _states.Values) s.Client?.Dispose();
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
                    await SwitchAsync(_ws.Id);
                    break;
                case "switch":
                    if (_cts == null) await SwitchAsync(Str(m, "id"));
                    break;
                case "addWs":
                    if (_cts == null) await AddWorkspaceAsync();
                    break;
                case "deleteWs":
                    if (_cts == null) await DeleteWorkspaceAsync(Str(m, "id"));
                    break;
                case "connect":
                    await GuardAsync(ct => ConnectAsync(Str(m, "name"), (string?)m["urls"] ?? "", Str(m, "token"), Bool(m, "ignoreSsl"), ct));
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
                    _ws.Selected = m["paths"]?.AsArray().Select(x => (string?)x ?? "").ToList() ?? new();
                    break;
                case "favorite":
                    var favPath = Str(m, "path");
                    _ws.Favorites.RemoveAll(x => x.Equals(favPath, StringComparison.OrdinalIgnoreCase));
                    if (Bool(m, "on") && favPath.Length > 0) _ws.Favorites.Add(favPath);
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
        title = _cfg.TitleTemplate,
        merge = _cfg.Merge,
        autoMerge = _cfg.AutoMerge,
        removeSource = _cfg.RemoveSource,
        squash = _cfg.Squash,
        branches = _cfg.BranchHistory,
    });

    private void SendWsList() => Send(new
    {
        type = "wsList",
        active = _ws.Id,
        items = _cfg.Workspaces.Select(w =>
        {
            ProjectRef.TryParse(w.Sources.FirstOrDefault(), out var r, out _);
            return new { id = w.Id, name = w.Name, host = r?.Host ?? "", shortHost = ShortHost(r?.Host), connected = State(w).Connected != null };
        }),
    });

    /// <summary>"gitlab.fpt.com" -> "gitlab.fpt", "git.company.com.vn" -> "git.company", bỏ "www." và port.</summary>
    private static string ShortHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var h = host.Split(':')[0];
        if (h.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) h = h[4..];
        var parts = h.Split('.').ToList();
        if (parts.Count <= 2 || parts.All(p => p.All(char.IsDigit))) return h; // IP hoặc tên ngắn: giữ nguyên
        parts.RemoveAt(parts.Count - 1); // TLD
        if (parts.Count > 2 && parts[^1] is "com" or "co" or "net" or "org" or "edu" or "gov") parts.RemoveAt(parts.Count - 1);
        return string.Join('.', parts);
    }

    private void ApplyOptions(JsonNode m)
    {
        _ws.SourceBranch = Str(m, "source");
        _ws.TargetBranch = Str(m, "target");
        _ws.BranchBase = Str(m, "base");
        _ws.BranchName = Str(m, "name");
        _ws.Mode = Str(m, "mode") == "branch" ? "branch" : "mr";
        if (Str(m, "title").Length > 0) _cfg.TitleTemplate = Str(m, "title");
        _cfg.Merge = Bool(m, "merge");
        _cfg.AutoMerge = Bool(m, "autoMerge");
        _cfg.RemoveSource = Bool(m, "removeSource");
        _cfg.Squash = Bool(m, "squash");
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

    // ================================================================ workspaces
    private async Task SwitchAsync(string id)
    {
        _ws = _cfg.Workspaces.FirstOrDefault(w => w.Id == id) ?? _cfg.Workspaces[0];
        _cfg.ActiveWorkspace = _ws.Id;
        TrySave();
        SendWsList();
        Send(new
        {
            type = "wsInfo",
            id = _ws.Id,
            name = _ws.Name,
            sources = _ws.Sources,
            ignoreSsl = _ws.IgnoreSsl,
            hasToken = _ws.Token != null,
            source = _ws.SourceBranch,
            target = _ws.TargetBranch,
            branchBase = _ws.BranchBase,
            branchName = _ws.BranchName,
            mode = _ws.Mode,
        });

        var st = St;
        if (st.Connected != null)
        {
            Send(st.Connected);                    // đã load rồi -> hiện ngay, không gọi API
            if (st.Branches != null) Send(st.Branches);
        }
        else if (_ws.Token != null && _ws.Sources.Count > 0)
        {
            await GuardAsync(ct => ConnectAsync(_ws.Name, string.Join('\n', _ws.Sources), "", _ws.IgnoreSsl, ct));
        }
    }

    private async Task AddWorkspaceAsync()
    {
        var n = 1;
        while (_cfg.Workspaces.Any(w => w.Name == $"Workspace {n}")) n++;
        var ws = new Workspace { Name = $"Workspace {n}", SourceBranch = _ws.SourceBranch, TargetBranch = _ws.TargetBranch };
        _cfg.Workspaces.Add(ws);
        await SwitchAsync(ws.Id);
    }

    private async Task DeleteWorkspaceAsync(string id)
    {
        var ws = _cfg.Workspaces.FirstOrDefault(w => w.Id == id);
        if (ws == null) return;
        _cfg.Workspaces.Remove(ws);
        if (_states.Remove(ws.Id, out var st)) st.Client?.Dispose();
        if (_cfg.Workspaces.Count == 0) _cfg.Workspaces.Add(new Workspace { Name = "Workspace 1" });
        Log($"Đã xoá workspace '{ws.Name}'", LogKind.Warn);
        await SwitchAsync(_ws.Id == id ? _cfg.Workspaces[0].Id : _ws.Id);
    }

    // ================================================================ GitLab actions
    private static GitLabClient EnsureClient(WsState st, ProjectRef r, string token, bool ignoreSsl)
    {
        var key = $"{r.BaseUrl}|{token}|{ignoreSsl}";
        if (st.Client == null || key != st.ClientKey)
        {
            st.Client?.Dispose();
            st.Client = new GitLabClient(r.BaseUrl, token, ignoreSsl);
            st.ClientKey = key;
        }
        return st.Client;
    }

    /// <summary>Kết nối workspace đang mở: nhiều nguồn (mỗi dòng 1 URL group/repo, cùng host), gộp project, bỏ trùng.</summary>
    private async Task ConnectAsync(string name, string urlsText, string token, bool ignoreSsl, CancellationToken ct)
    {
        var ws = _ws;
        var st = St;
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
            throw new GitLabException($"Trong 1 workspace mọi URL phải cùng GitLab ({first.Host}). '{otherHost.Url}' khác host → tạo workspace mới (+).");

        if (token.Length == 0) token = AppConfig.GetToken(ws) ?? "";
        if (token.Length == 0) throw new GitLabException("Chưa nhập access token cho workspace này.");

        var gl = EnsureClient(st, first, token, ignoreSsl);
        Log($"[{(name.Length > 0 ? name : ws.Name)}] Kết nối {first.BaseUrl} …", LogKind.Info);
        var user = await gl.GetCurrentUserAsync(ct);

        var all = new Dictionary<long, GlProject>();
        var hidden = 0;
        foreach (var (_, r) in refs)
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
        st.Projects = projects.ToDictionary(p => p.Id);

        // Lưu cấu hình workspace
        ws.Name = name.Length > 0 ? name : (ws.Name.StartsWith("Workspace") ? first.Path.Split('/')[^1] : ws.Name);
        ws.Sources = lines;
        ws.IgnoreSsl = ignoreSsl;
        AppConfig.SetToken(ws, token);
        TrySave();

        var favs = ws.Favorites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var saved = ws.Selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasFav = projects.Any(p => favs.Contains(p.PathWithNamespace));
        // Có favorite -> tự tick sẵn favorite; chưa có -> dùng lựa chọn lần trước
        bool IsSelected(GlProject p) => projects.Count == 1 || (hasFav ? favs.Contains(p.PathWithNamespace) : saved.Contains(p.PathWithNamespace));

        var prefix = CommonNamespace(projects.Select(p => p.PathWithNamespace));
        st.Connected = new
        {
            type = "connected",
            wsId = ws.Id,
            host = first.Host,
            user = user.Username,
            hidden,
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
            }).ToList(),
        };
        st.Branches = null;
        SendWsList();
        Send(st.Connected);
        Log($"[{ws.Name}] @{user.Username} · {projects.Count} project" + (hidden > 0 ? $" (ẩn {hidden})" : ""), LogKind.Success);

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
        var st = St;
        if (st.Client == null || st.Projects.Count == 0) return;
        var gl = st.Client;
        var list = ids.Where(st.Projects.ContainsKey).Select(id => st.Projects[id]).ToList();
        if (list.Count == 0) list = st.Projects.Values.ToList();

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

        st.Branches = new
        {
            type = "remoteBranches",
            total = list.Count,
            items = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new { name = kv.Key, count = kv.Value }).ToList(),
        };
        Send(st.Branches);
        Log($"Đã tải {counts.Count} branch từ {list.Count} project", LogKind.Info);
    }

    private async Task RunBatchAsync(JsonNode m, CancellationToken ct)
    {
        var st = St;
        if (st.Client == null || st.Connected == null) throw new GitLabException("Kết nối trước đã.");
        var gl = st.Client;
        ApplyOptions(m);

        var dryRun = Bool(m, "dryRun");
        var branchMode = Str(m, "mode") == "branch";
        string source, target;
        if (branchMode)
        {
            source = Str(m, "base");  // branch gốc
            target = Str(m, "name");  // branch mới
            if (source.Length == 0) throw new GitLabException("Nhập branch gốc (vd: prod).");
            if (BranchWorkflow.ValidateName(target) is { } nameErr) throw new GitLabException(nameErr);
            if (source == target) throw new GitLabException("Branch mới phải khác branch gốc.");
        }
        else
        {
            source = Str(m, "source");
            target = Str(m, "target");
            if (source.Length == 0 || target.Length == 0) throw new GitLabException("Nhập branch nguồn và branch đích.");
            if (source == target) throw new GitLabException("Branch nguồn và đích phải khác nhau.");
        }

        var targets = Ids(m).Where(st.Projects.ContainsKey).Select(id => st.Projects[id]).ToList();
        if (targets.Count == 0) throw new GitLabException("Chưa chọn project nào.");

        var opts = new MrOptions(source, target,
            Str(m, "title").Length > 0 ? Str(m, "title") : "Merge branch '{source}' into '{target}'",
            (string?)m["description"] ?? "", Bool(m, "merge"), Bool(m, "autoMerge"), Bool(m, "removeSource"), Bool(m, "squash"));

        _cfg.RememberBranch(target);
        _cfg.RememberBranch(source);
        TrySave();
        Send(new { type = "branches", branches = _cfg.BranchHistory });

        var mode = branchMode
            ? (dryRun ? "Kiểm tra tạo branch" : "Tạo branch")
            : (dryRun ? "Kiểm tra" : opts.Merge ? "Tạo MR + merge" : "Tạo MR");
        Log($"── [{_ws.Name}] {mode}: {source} → {target} · {targets.Count} project", LogKind.Info);
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
                res = (branchMode, dryRun) switch
                {
                    (true, true) => await BranchWorkflow.CheckAsync(gl, p, source, target, ct),
                    (true, false) => await BranchWorkflow.CreateAsync(gl, p, source, target, PLog, ct),
                    (false, true) => await MrWorkflow.CheckAsync(gl, p, source, target, ct),
                    _ => await MrWorkflow.RunAsync(gl, p, opts, PLog, ct),
                };
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
