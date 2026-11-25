using System.Text.Json.Nodes;

namespace GitLabMR;

/// <summary>
/// Auto flow (workspace Gerrit), cứ mỗi IntervalSec giây:
///   1. Tìm change đang mở của các dev đã đặt (Owners) mà reviewer (Reviewers) đã Code-Review +1.
///   2. +2 & submit (không đụng change có phiếu âm / còn thiếu Verified…; conflict thì báo Zalo).
///   3. Submit xong: chờ code sync sang GitLab rồi tạo (và merge) MR {branch} → GitLabTarget.
/// Mỗi change chỉ xử lý 1 lần cho mỗi patchset; patchset mới (sau khi dev sửa) thì xét lại.
/// Chạy thử (DryRun): chỉ ghi nhật ký những gì sẽ làm.
/// </summary>
public sealed partial class AppHost
{
    private static readonly TimeSpan SyncWait = TimeSpan.FromMinutes(15);
    /// <summary>Quét đầy đủ định kỳ (giữa các lần chỉ hỏi change mới cập nhật) để không sót.</summary>
    private static readonly TimeSpan FullEvery = TimeSpan.FromMinutes(5);
    /// <summary>Luồng GitLab: còn MR / pipeline chờ thì 10 giây 1 lần, rảnh thì chờ được đánh thức (tối đa 60 giây).</summary>
    private static readonly TimeSpan GlFast = TimeSpan.FromSeconds(10), GlIdle = TimeSpan.FromSeconds(60);

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
        public readonly SemaphoreSlim GlGate = new(1, 1);
        public readonly SemaphoreSlim Wake = new(0, 1);              // đánh thức luồng GitLab ngay
        public DateTime? LastRun, LastFull, LastDelta;
        public int DevCount;
        public string LastText = "", GlText = "";
        public readonly List<OtpFix> Otps = new();                   // conflict chờ member fix (khoá bằng lock Jobs)
        public readonly List<ZaloSeen> Seen = new();                 // tin nhắn bot vừa nhận (cho "Lấy chat ID")
        public bool Listening;                                       // luồng nghe Zalo đang chạy
        public string? GcSelf;                                       // username Gerrit của mình
        public readonly Dictionary<string, string> XlsxChecked = new(); // change -> revision đã kiểm tra file Excel
        public readonly HashSet<string> XlsxBlocked = new();         // change mình đã −2 vì file Excel (khoá bằng lock Jobs)
        public DateTime? XlsxDelta;
        public bool LastError;
        public bool Restored;                                        // đã nạp việc đang chờ từ file
        public GerritClient? Gc; public string GcKey = "";
        public GitLabClient? Gl; public string GlKey = "";
        public readonly List<DeployWatch> Deploys = new();          // chờ Argo deploy xong (khoá bằng lock Jobs)
        public ArgoClient? Argo; public string ArgoKey = "";
        public DateTime? ArgoMon;                                    // lần canh Argo gần nhất
        public readonly Dictionary<string, DateTime> ArgoNotReady = new(); // pod -> lúc thấy chưa Ready
        public readonly HashSet<string> ArgoBad = new();             // app đang lỗi, đã báo (khoá bằng lock Jobs)
    }

    private sealed class AutoJob
    {
        public required GlProject Project;
        public required string Source, Target;
        public string? Sha;                       // commit cần có bên GitLab trước khi tạo MR
        public DateTime Since = DateTime.Now;
        public List<string> Changes = new();
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
        if (_autoPaused) return; // tạm dừng từ khay hệ thống
        foreach (var ws in _cfg.Workspaces.Where(w => w.Kind == "gerrit" && w.Auto?.Enabled == true))
            if (AutoOf(ws.Id).Loop == null) StartAuto(ws);
        WatchAutoLock();
    }

    // ---------------------------------------------------------------- chỉ 1 cửa sổ chạy Auto
    // Mở nhiều cửa sổ GitLab MR: chỉ cửa sổ giữ được file khoá mới chạy Auto (tránh submit / merge / báo Zalo trùng,
    // 2 luồng nghe Zalo giành tin). Khoá là file mở độc quyền: process thoát hay crash thì hệ điều hành tự nhả.
    private FileStream? _autoLock;
    private bool _lockWatch;

    private static string AutoLockFile => Path.Combine(
        Platform.AppData, "GitLabMR", "auto.lock");

    private bool AutoOwner()
    {
        if (_autoLock != null) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutoLockFile)!);
            _autoLock = new FileStream(AutoLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _autoLock.SetLength(0);
            var pid = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            _autoLock.Write(pid); _autoLock.Flush();
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Cửa sổ không giữ khoá: 30 giây thử lại 1 lần, cửa sổ kia đóng thì nhận chạy Auto.</summary>
    private void WatchAutoLock()
    {
        if (_lockWatch || _autoLock != null) return;
        _lockWatch = true;
        _ = Task.Run(async () =>
        {
            while (!_life.IsCancellationRequested && _autoLock == null)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), _life.Token); } catch { break; }
                if (!_cfg.Workspaces.Any(w => w.Kind == "gerrit" && w.Auto?.Enabled == true) || !AutoOwner()) continue;
                Log("[Auto] Cửa sổ chạy Auto trước đã đóng → cửa sổ này nhận chạy Auto", LogKind.Info);
                foreach (var ws in _cfg.Workspaces.Where(w => w.Kind == "gerrit" && w.Auto?.Enabled == true))
                    if (AutoOf(ws.Id).Loop == null) StartAuto(ws);
            }
            _lockWatch = false;
        });
    }

    private void StartAuto(Workspace ws)
    {
        StopAuto(ws.Id);
        var a = AutoOf(ws.Id);
        if (!AutoOwner())
        {
            Once(a, "elsewhere", $"[Auto {ws.Name}] Auto đang chạy ở cửa sổ GitLab MR khác – cửa sổ này không chạy để tránh submit / merge / báo trùng", LogKind.Warn);
            WatchAutoLock();
            SendAutoState(ws);
            return;
        }
        a.Noted.Remove("elsewhere");
        RestoreWork(ws, a);
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
        // Luồng GitLab riêng: tạo / merge MR, theo dõi pipeline – không chờ lượt quét Gerrit
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await GitLabTickAsync(ws, cts.Token);
                bool busy;
                lock (a.Jobs) busy = a.Jobs.Count > 0 || a.Pipes.Count > 0 || !a.NewPipes.IsEmpty || a.Deploys.Count > 0;
                try { await a.Wake.WaitAsync(busy ? GlFast : GlIdle, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        // Nghe tin nhắn Zalo: lệnh "<branch> <OTP>" merge branch fix conflict
        StartZaloListener(ws, a, cts.Token);
    }

    private static void WakeGitLab(AutoState a)
    {
        try { if (a.Wake.CurrentCount == 0) a.Wake.Release(); } catch (SemaphoreFullException) { }
    }

    private static int Interval(Workspace ws) => Math.Clamp(ws.Auto?.IntervalSec ?? 60, 10, 3600);

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
        // Giữ MR / pipeline đang chờ: Gerrit đã submit rồi, đổi cấu hình không được làm mất thông báo
        a.LastFull = a.LastDelta = a.XlsxDelta = null;
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
            gitlabMerge = f.GitLabMerge, notifySuccess = f.NotifySuccess,
            hasZalo = f.ZaloToken != null, zaloChats = f.ChatList, notifyPipeline = f.NotifyPipeline, autoResolve = f.AutoResolve, plusOneOnVeto = f.PlusOneOnVeto, plusOneKeyword = f.PlusOneKeyword, sassyBot = f.SassyBot, xlsxGuard = f.XlsxGuard, xlsxRepos = string.Join(", ", f.XlsxRepos),
            conflictFix = f.ConflictFix, conflictBranch = f.ConflictBranch, claudePath = f.ClaudePath ?? "", claudeModel = f.ClaudeModel ?? "",
            argoWatch = f.ArgoWatch, argoUrl = f.ArgoUrl ?? "", argoUser = f.ArgoUser ?? "", hasArgo = f.ArgoSecret != null, argoSsl = f.ArgoIgnoreSsl, argoApps = ArgoAppsText(f.ArgoApps), argoWait = f.ArgoWaitMin, argoMonitor = f.ArgoMonitor,
            running = a.Loop != null, elsewhere = f.Enabled && a.Loop == null && _autoLock == null, lastRun = a.LastRun, text = a.LastText + (a.GlText.Length > 0 ? ", " + a.GlText : ""), error = a.LastError, jobs = a.Jobs.Count,
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
        f.IntervalSec = Math.Clamp((int?)m["interval"] ?? 60, 10, 3600);
        f.Branch = Str(m, "branch");
        f.Reviewers = SplitList(Str(m, "reviewers"));
        f.Owners = SplitList(Str(m, "owners"));
        f.GitLabWs = Str(m, "gitlabWs");
        f.GitLabSource = Str(m, "gitlabSource") is { Length: > 0 } src ? src : "{branch}";
        f.GitLabTarget = Str(m, "gitlabTarget");
        f.GitLabMerge = Bool(m, "gitlabMerge");
        f.NotifySuccess = Bool(m, "notifySuccess");
        f.NotifyPipeline = Bool(m, "notifyPipeline");
        f.AutoResolve = Bool(m, "autoResolve");
        f.PlusOneOnVeto = Bool(m, "plusOneOnVeto");
        f.SassyBot = Bool(m, "sassyBot");
        if (m["xlsxGuard"] != null) f.XlsxGuard = Bool(m, "xlsxGuard");
        if (m["xlsxRepos"] != null) f.XlsxRepos = SplitList(Str(m, "xlsxRepos"));
        f.PlusOneKeyword = Str(m, "plusOneKeyword") is { Length: > 0 } kw ? kw : "IgnoreQueryFilter";
        f.ConflictFix = Bool(m, "conflictFix");
        f.ConflictBranch = Str(m, "conflictBranch") is { Length: > 0 } cb ? cb : "{branch}_fix_conflict";
        f.ClaudePath = Str(m, "claudePath") is { Length: > 0 } cp ? cp : null;
        f.ClaudeModel = Str(m, "claudeModel") is { Length: > 0 } cm ? cm : null;
        if (f.ClaudeModel?.Any(ch => char.IsWhiteSpace(ch) || ch == '"') == true) throw new GitLabException("Tên model không hợp lệ (vd opus, sonnet).");
        if (!f.ConflictBranch.Contains("{branch}"))
            throw new GitLabException("Tên branch fix conflict phải có {branch} (vd {branch}_fix_conflict), để mỗi feature 1 branch riêng.");
        var zalo = Str(m, "zaloToken");
        if (Bool(m, "clearZalo")) f.ZaloToken = null;
        else if (zalo.Length > 0) f.ZaloToken = Platform.ProtectToken(_ws.ZaloKey, zalo);
        f.ZaloChats = ParseChats(m);
        f.ZaloChatId = null;
        SaveArgo(m, f);
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
        if (!AutoOwner()) throw new GitLabException("Auto đang chạy ở cửa sổ GitLab MR khác – bấm Quét ngay ở cửa sổ đó.");
        _ = Task.Run(() => AutoTickAsync(ws, _life.Token));
    }

    private async Task AutoTestClaudeAsync(string path, string model)
    {
        Send(new { type = "toast", text = "Đang gọi thử Claude Code…" });
        var err = await ConflictFixer.CheckClaudeAsync(path.Length > 0 ? path : null, model, _life.Token);
        if (err != null) throw new GitLabException(err);
        Log($"[Claude] Chạy được: {ConflictFixer.FindClaude(path.Length > 0 ? path : null)} · model {(model.Length > 0 ? model : "mặc định")}", LogKind.Success);
        Send(new { type = "toast", text = "Claude Code chạy được – auto-fix conflict sẵn sàng" });
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
        var a = AutoOf(_ws.Id);
        if (a.Listening && typed.Length == 0)
        {
            // Luồng nghe đang đọc tin của bot: lấy từ đó, không gọi getUpdates song song
            var s = await WaitSeenAsync(a, TimeSpan.FromSeconds(25)) ?? throw new GitLabException("Chưa thấy tin nhắn nào tới bot – nhắn cho bot rồi bấm lại.");
            Log($"[Zalo] Chat ID {s.ChatId} · {s.ChatType} · {s.ChatName}: \"{s.Text}\"", LogKind.Info);
            Send(new { type = "zaloChat", id = s.ChatId, chatType = s.ChatType, name = s.ChatName });
            return;
        }
        var chats = await Zalo.GetRecentChatsAsync(token, _life.Token);
        if (chats.Count == 0) throw new GitLabException("Chưa thấy tin nhắn nào tới bot – nhắn cho bot rồi bấm lại.");
        var c = chats[0];
        Log($"[Zalo] Chat ID {c.Id} · {c.Type} · {c.Name}: \"{c.Text}\"", LogKind.Info);
        Send(new { type = "zaloChat", id = c.Id, chatType = c.Type, name = c.Name });
    }

    public const int MaxZaloChats = 3;

    private static List<ZaloChat> ParseChats(JsonNode m)
    {
        var list = new List<ZaloChat>();
        if (m["zaloChats"] is JsonArray arr)
            foreach (var n in arr)
            {
                var id = ((string?)n?["id"] ?? "").Trim();
                if (id.Length == 0 || list.Any(x => x.Id == id)) continue;
                list.Add(new ZaloChat { Id = id, Name = ((string?)n?["name"] ?? "").Trim(), Type = (string?)n?["type"] ?? "" });
            }
        if (list.Count > MaxZaloChats) throw new GitLabException($"1 bot chỉ gửi tối đa {MaxZaloChats} nơi (nhóm / người) – bỏ bớt rồi lưu.");
        return list;
    }

    private async Task AutoTestZaloAsync(JsonNode m)
    {
        var token = ZaloTokenOf(Str(m, "zaloToken"));
        var chats = ParseChats(m);
        if (chats.Count == 0) throw new GitLabException("Chưa có nơi nhận – bấm Lấy chat ID trước.");
        var fails = new List<string>();
        foreach (var c in chats)
        {
            try { await Zalo.SendAsync(token, c.Id, $"GitLab MR · Auto {_ws.Name}\nTin nhắn thử tới {(c.Name.Length > 0 ? c.Name : c.Id)}: Zalo Bot hoạt động.", _life.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { fails.Add($"{(c.Name.Length > 0 ? c.Name : c.Id)}: {Describe(ex)}"); }
        }
        if (fails.Count > 0) throw new GitLabException($"Gửi được {chats.Count - fails.Count}/{chats.Count}. Lỗi – " + string.Join("; ", fails));
        Send(new { type = "toast", text = $"Đã gửi tin thử tới {chats.Count} nơi" });
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

    private async Task GitLabTickAsync(Workspace ws, CancellationToken life)
    {
        var a = AutoOf(ws.Id);
        if (ws.Auto == null || !await a.GlGate.WaitAsync(0)) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(life);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        var before = a.GlText;
        try
        {
            if (ws.Auto.DryRun) { a.GlText = ""; return; }
            var jobs = await RunGitLabJobsAsync(ws, a, cts.Token);
            var pipes = await RunPipeWatchesAsync(ws, a, cts.Token);
            var deploys = await RunDeployWatchesAsync(ws, a, cts.Token);
            await RunArgoMonitorAsync(ws, a, cts.Token);
            a.GlText = string.Join(", ", new[] { jobs > 0 ? $"{jobs} MR chờ sync" : "", pipes > 0 ? $"{pipes} pipeline đang chờ" : "", deploys > 0 ? $"{deploys} app chờ deploy" : "" }.Where(x => x.Length > 0));
            a.Noted.RemoveWhere(k => k.StartsWith("glerr|"));
        }
        catch (OperationCanceledException) when (life.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var text = ex is OperationCanceledException ? "Quá 10 phút chưa xong lượt GitLab" : Describe(ex);
            a.GlText = "GitLab lỗi";
            if (a.Noted.Add("glerr|" + text)) await AlertAsync(ws, "Auto lỗi (GitLab)", text, null);
        }
        finally
        {
            SaveWork(ws.Id, a);
            a.GlGate.Release();
            if (a.GlText != before) SendAutoState(ws);
        }
    }

    // ---------------------------------------------------------------- việc đang chờ: ghi ra file để mở lại / cập nhật app vẫn theo dõi tiếp
    private sealed class SavedWork
    {
        public List<AutoJob> Jobs { get; set; } = new();
        public List<PipeWatch> Pipes { get; set; } = new();
        public List<OtpFix> Otps { get; set; } = new();
        public List<string> XlsxBlocked { get; set; } = new();
        public List<DeployWatch> Deploys { get; set; } = new();
        public List<string> ArgoBad { get; set; } = new();
    }

    private static readonly System.Text.Json.JsonSerializerOptions WorkJs = new() { IncludeFields = true };

    private static string WorkFile(string wsId) => Path.Combine(
        Platform.AppData, "GitLabMR", "auto", wsId + ".json");

    private static void SaveWork(string wsId, AutoState a)
    {
        try
        {
            SavedWork w;
            lock (a.Jobs) w = new SavedWork { Jobs = a.Jobs.ToList(), Pipes = a.Pipes.Concat(a.NewPipes).ToList(), Otps = a.Otps.ToList(), XlsxBlocked = a.XlsxBlocked.ToList(), Deploys = a.Deploys.ToList(), ArgoBad = a.ArgoBad.ToList() };
            var path = WorkFile(wsId);
            if (w.Jobs.Count == 0 && w.Pipes.Count == 0 && w.Otps.Count == 0 && w.XlsxBlocked.Count == 0 && w.Deploys.Count == 0 && w.ArgoBad.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(w, WorkJs));
            File.Move(tmp, path, true);
        }
        catch { /* không ghi được thì vẫn chạy trong bộ nhớ */ }
    }

    private void RestoreWork(Workspace ws, AutoState a)
    {
        if (a.Restored) return;
        a.Restored = true;
        try
        {
            var path = WorkFile(ws.Id);
            if (!File.Exists(path)) return;
            var w = System.Text.Json.JsonSerializer.Deserialize<SavedWork>(File.ReadAllText(path), WorkJs);
            if (w == null) return;
            lock (a.Jobs)
            {
                foreach (var j in w.Jobs) if (!a.Jobs.Any(x => x.Project.Id == j.Project.Id && x.Source == j.Source && x.Target == j.Target)) a.Jobs.Add(j);
                foreach (var pw in w.Pipes) if (!a.Pipes.Any(x => x.Project.Id == pw.Project.Id && x.Iid == pw.Iid)) a.Pipes.Add(pw);
                foreach (var o in w.Otps) if (DateTime.Now - o.Created <= OtpLife && !a.Otps.Any(x => x.Otp == o.Otp)) a.Otps.Add(o);
                a.XlsxBlocked.UnionWith(w.XlsxBlocked);
                a.ArgoBad.UnionWith(w.ArgoBad);
                foreach (var d in w.Deploys) if (!a.Deploys.Any(x => x.App == d.App && x.Sha == d.Sha)) a.Deploys.Add(d);
            }
            if (w.Jobs.Count + w.Pipes.Count > 0)
                Log($"[Auto {ws.Name}] Tiếp tục theo dõi {w.Jobs.Count} MR, {w.Pipes.Count} pipeline từ lần chạy trước", LogKind.Info);
        }
        catch (Exception ex) { Log($"[Auto {ws.Name}] Không đọc được việc đang chờ: {ex.Message}", LogKind.Warn); }
    }

    private async Task<string> AutoScanAsync(Workspace ws, AutoState a, CancellationToken ct)
    {
        var f = ws.Auto!;
        var tag = $"[Auto {ws.Name}]";
        if (f.Reviewers.Count == 0 || f.Owners.Count == 0) throw new GitLabException("Chưa nhập reviewer / account dev.");
        var gc = AutoGerrit(ws, a);

        var q = "status:open -is:wip (" + string.Join(" OR ", f.Owners.Select(o => $"owner:\"{o}\"")) + ")";
        if (!string.IsNullOrWhiteSpace(f.Branch)) q += $" branch:\"{f.Branch.Trim()}\"";
        // Giữa các lần quét đầy đủ chỉ hỏi change mới cập nhật (+1, patchset mới, Verified…) – rỗng thì gần như không tốn gì
        var started = DateTime.Now;
        var full = a.LastDelta == null || a.LastFull == null || started - a.LastFull > FullEvery;
        if (!full) q += $" -age:{(int)(started - a.LastDelta!.Value).TotalSeconds + 60}s";
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
            if (f.PlusOneOnVeto && f.PlusOneKeyword.Trim().Length > 0
                && Matches(f.Owners, c.Owner?.Username, c.Owner?.Email, c.Owner?.Name)
                && votes.FirstOrDefault(v => v.Value <= -2 && Matches(f.Reviewers, v.Username, v.Email, v.Name)) is { } veto
                && a.Noted.Add($"p1|{key}|{rev}"))
                await PlusOneOnVetoAsync(ws, a, gc, c, votes, veto, ct);
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
            var onlyComments = missing.Count > 0 && missing.All(IsUnresolvedReq);
            if (onlyComments && !f.AutoResolve)
            {
                // Chỉ vướng comment chưa resolve: báo 1 lần để dev resolve
                if (a.Noted.Add($"cmt|{key}|{rev}"))
                {
                    Log($"{tag} {label}: {who} đã +1 nhưng còn comment chưa resolve", LogKind.Warn);
                    await NotifyAsync(ws, "Gerrit: còn comment chưa resolve", $"{label}: {c.Subject}\nDev: {owner} · {who} đã +1\nResolve comment thì app submit ở lượt quét sau.", url, pending: true);
                }
                continue;
            }
            if (missing.Count > 0 && !onlyComments)
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
            var res = await SubmitOneAsync(gc, c, (msg, k) => Log($"{tag} {label}: {msg}", k), ct,
                f.AutoResolve, GerritClient.ParseTime(by.Date) ?? DateTime.UtcNow, "Done");
            if (res.Outcome == MrOutcome.NotMerged)
            {
                // Có comment mới hơn lúc +1: chưa submit, xét lại lượt sau (dev resolve xong là chạy), chỉ báo 1 lần
                if (a.Noted.Add($"newcmt|{key}|{rev}"))
                {
                    Log($"{tag} {label}: {res.Message}", LogKind.Warn);
                    await NotifyAsync(ws, "Gerrit: có comment mới sau lúc +1", $"{label}: {c.Subject}\nDev: {owner} · {who} +1\n{res.Message}\nResolve xong app tự submit.", url, pending: true);
                }
                continue;
            }
            a.Handled[key] = rev;
            if (res.Outcome == MrOutcome.Submitted)
            {
                submitted++;
                Log($"{tag} {label}: {res.Message} {url}", LogKind.Success);
                // Tự resolve comment thì luôn báo (ghi chú chỉ ở Zalo, trên Gerrit chỉ trả lời "Done")
                var resolved = System.Text.RegularExpressions.Regex.Match(res.Message, @"resolve (\d+) comment");
                if (resolved.Success)
                    await NotifyAsync(ws, $"Đã tự resolve {resolved.Groups[1].Value} comment rồi submit",
                        $"{label}: {c.Subject}\nDev: {owner} · {who} +1\nComment viết trước lúc +1 được trả lời \"Done\" và resolve.", url);
                else if (f.NotifySuccess) await NotifyAsync(ws, "Đã submit", $"{label}: {c.Subject}\nDev: {owner} · {who} +1", url);
                await EnqueueGitLabAsync(ws, a, gc, c, ct);
                WakeGitLab(a);
            }
            else
            {
                var conflict = res.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase);
                await AlertAsync(ws, conflict ? "Conflict trên Gerrit – cần fix" : "Không submit được",
                    $"{label}: {c.Subject}\nDev: {owner}\n{res.Message}", url);
            }
        }

        var xl = await XlsxGuardAsync(ws, a, gc, full, started, ct);
        if (full) { a.LastFull = started; a.DevCount = changes.Count; }
        a.LastDelta = started;
        return $"{a.DevCount} change của dev" + (!full && changes.Count > 0 ? $" ({changes.Count} vừa cập nhật)" : "")
               + (submitted > 0 ? $", vừa submit {submitted}" : "") + (xl > 0 ? $", −2 {xl} change vì file Excel" : "");
    }

    /// <summary>Reviewer −2 mà lý do (tin nhắn / comment của họ) có từ khoá cấu hình (vd IgnoreQueryFilter): mình +1.</summary>
    private async Task PlusOneOnVetoAsync(Workspace ws, AutoState a, GerritClient gc, GerritClient.ChangeInfo c,
        List<GerritClient.ApprovalInfo> votes, GerritClient.ApprovalInfo veto, CancellationToken ct)
    {
        var f = ws.Auto!;
        var tag = $"[Auto {ws.Name}]";
        var label = $"{c.Project} #{c.Number}";
        var kw = f.PlusOneKeyword.Trim();
        try
        {
            a.GcSelf ??= await gc.GetSelfAsync(ct);
            if (votes.Any(v => v.Value >= 1 && string.Equals(v.Username, a.GcSelf, StringComparison.OrdinalIgnoreCase))) return; // đã +1 rồi
            bool ByVeto(GerritClient.Account? x) => x != null && Matches(new() { veto.Username ?? veto.Name ?? "" }, x.Username, x.Email, x.Name);
            var msgs = await gc.GetMessagesAsync(c.Project, c.Number, ct);
            var hit = msgs.Any(m => ByVeto(m.Author) && m.Message?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true);
            if (!hit)
                hit = (await gc.GetCommentsAsync(c.Project, c.Number, ct)).Any(m => ByVeto(m.Author) && m.Message?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true);
            if (!hit) return;
            var who = veto.Username ?? veto.Name ?? "reviewer";
            if (f.DryRun) { Log($"{tag} [Thử] Sẽ +1 {label} ({who} −2 vì {kw})", LogKind.Success); return; }
            await gc.VoteAsync(c.Project, c.Number, 1, ct);
            Log($"{tag} {label}: {who} −2 vì {kw} → đã +1", LogKind.Success);
            await NotifyAsync(ws, $"Đã +1 ({who} −2 vì {kw})", $"{label}: {c.Subject}\nDev: {c.Owner?.Name ?? c.Owner?.Username}", gc.ChangeUrl(c.Project, c.Number));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            a.Noted.Remove($"p1|{ChangeKey(c.Project, c.Number)}|{c.CurrentRevision ?? c.Updated ?? ""}"); // lỗi tạm: thử lại lượt sau
            Once(a, "p1err|" + Describe(ex), $"{tag} {label}: chưa +1 được – {Describe(ex)}", LogKind.Warn);
        }
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
            var sha = await gc.GetBranchRevisionAsync(c.Project, c.Branch, ct);
            lock (a.Jobs)
            {
                var job = a.Jobs.FirstOrDefault(j => j.Project.Id == p.Id && j.Source == src && j.Target == f.GitLabTarget);
                if (job == null) a.Jobs.Add(job = new AutoJob { Project = ToGl(p), Source = src, Target = f.GitLabTarget! });
                job.Sha = sha ?? job.Sha;
                job.Changes.Add($"#{c.Number}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AlertAsync(ws, "Lỗi chuẩn bị MR GitLab", $"{c.Project} #{c.Number}: {Describe(ex)}", null);
        }
    }

    /// <summary>Tạo / merge MR cho các repo vừa submit (khi code đã sync sang GitLab). Trả về số MR còn chờ.</summary>
    private async Task<int> RunGitLabJobsAsync(Workspace ws, AutoState a, CancellationToken ct)
    {
        List<AutoJob> list;
        lock (a.Jobs) list = a.Jobs.ToList();
        if (list.Count == 0) return 0;
        var tag = $"[Auto {ws.Name}]";
        var g = AutoGitLab(ws, a);
        if (g == null) { lock (a.Jobs) a.Jobs.Clear(); return 0; }
        var f = ws.Auto!;
        foreach (var job in list)
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
                lock (a.Jobs) a.Jobs.Remove(job);
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
                    else await AlertAsync(ws, conflict ? "Conflict trên GitLab – cần fix" : "GitLab chưa merge được", $"{label}\n{res.Message}"
                        + (conflict ? OtpHint(ws, a, p, job.Source, job.Target, label, changes, null) : ""), res.Url);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Lỗi mạng tạm thời: giữ lại thử lượt sau, quá thời gian chờ thì báo và bỏ
                if (late) { lock (a.Jobs) a.Jobs.Remove(job); await AlertAsync(ws, "GitLab lỗi", $"{label}\n{Describe(ex)}", null); }
                else Once(a, $"jerr|{p.Id}|{Describe(ex)}", $"{tag} {label}: {Describe(ex)} – thử lại lượt sau", LogKind.Warn);
            }
        }
        lock (a.Jobs) return a.Jobs.Count;
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
                var res = await ConflictFixer.FixAsync(r!.BaseUrl, token, gws.IgnoreSsl, p, source, target, fix, f.ClaudePath, f.ClaudeModel,
                    async files =>
                    {
                        told = true;
                        await AlertAsync(ws, "Conflict trên GitLab – đang auto-fix",
                            $"{label}\nGerrit {changes}\nConflict ở: {FileList(files)}\nĐang tạo branch {fix} (merge {target} vào) và nhờ Claude sửa, {source} giữ nguyên.", mrUrl);
                    }, L, ct);
                if (!res.Ok)
                {
                    await AlertAsync(ws, "Auto-fix conflict không được – cần fix tay",
                        $"{label}\nGerrit {changes}\n{res.Message}" + (res.Files.Count > 0 ? $"\nFile: {FileList(res.Files)}" : "")
                        + (res.Summary is { } s0 ? $"\nClaude: {s0}" : "") + OtpHint(ws, a, p, source, target, label, changes, null), mrUrl);
                    return;
                }

                var desc = $"Auto-fix conflict {source} → {target} (Gerrit {changes}).\n\nFile conflict: {FileList(res.Files)}\n\n{res.Summary}";
                var opts = new MrOptions(fix, target, $"merge branch {source} to {target}", desc, f.GitLabMerge && res.Safe, _cfg.AutoMerge, true, false);
                var mr = await MrWorkflow.RunAsync(gl, p, opts, L, ct);
                var fixLabel = $"{p.PathWithNamespace} {fix} → {target}";
                var text = $"{fixLabel}\nGerrit {changes}\n{res.Message}" + (res.Files.Count > 0 ? $"\nĐã sửa: {FileList(res.Files)}" : "")
                           + (res.Summary is { } s ? $"\nClaude: {s}" : "");
                if (f.NotifyPipeline && mr.Outcome is MrOutcome.Merged or MrOutcome.AutoMerge && MrIid(mr.Url) is { } iid)
                {
                    a.NewPipes.Enqueue(new PipeWatch { Project = p, Iid = iid, Label = fixLabel, MrUrl = mr.Url!, Changes = changes });
                    WakeGitLab(a);
                }
                if (!res.Safe && mr.Outcome is MrOutcome.Created or MrOutcome.AlreadyOpen)
                    await AlertAsync(ws, "Đã fix conflict nhưng chưa merge – cần duyệt", $"{text}\nApp không tự merge: xem diff MR rồi merge tay." + OtpHint(ws, a, p, source, target, label, changes, fix), mr.Url);
                else if (mr.Outcome == MrOutcome.Merged)
                {
                    L($"Auto-fix conflict xong, {mr.Message}", LogKind.Success);
                    await NotifyAsync(ws, "Đã auto-fix conflict & merge", text, mr.Url);
                }
                else if (mr.Outcome is MrOutcome.AutoMerge or MrOutcome.Created or MrOutcome.AlreadyOpen)
                    await NotifyAsync(ws, "Đã auto-fix conflict, MR chưa merge", $"{text}\n{mr.Message}", mr.Url, pending: true);
                else
                    await AlertAsync(ws, "Auto-fix conflict xong nhưng chưa merge được", $"{text}\n{mr.Message}" + OtpHint(ws, a, p, source, target, label, changes, fix), mr.Url ?? mrUrl);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await AlertAsync(ws, told ? "Auto-fix conflict lỗi – cần fix tay" : "Conflict trên GitLab – auto-fix lỗi",
                    $"{label}\n{Describe(ex)}" + OtpHint(ws, a, p, source, target, label, changes, null), mrUrl);
            }
            finally { lock (a.Fixing) a.Fixing.Remove(key); }
        });
    }

    /// <summary>Auto-fix conflict chỉ dành cho branch môi trường testing2 (quy trình ppx-merge-testing).</summary>
    private static bool IsFixTarget(string target) => target.Equals("testing2", StringComparison.OrdinalIgnoreCase);

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
                    // Có theo dõi Argo: chưa nhắn, đợi pod chạy / lỗi rồi nhắn 1 tin (đỡ tốn lượt Zalo)
                    if (!AddDeployWatches(ws, a, p, pl, w.Label))
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
        return ChannelsAsync(ws, title, text, url, true);
    }

    /// <summary>Thông báo không phải lỗi: ✅ xong, ⏳ (pending) đã submit nhưng GitLab chưa merge xong.</summary>
    private Task NotifyAsync(Workspace ws, string title, string text, string? url, bool pending = false) =>
        ChannelsAsync(ws, title, text, url, false, pending ? "⏳" : null);

    /// <summary>Gửi tới kênh đã cấu hình (Zalo Bot). Lỗi gửi chỉ ghi nhật ký.</summary>
    private async Task ChannelsAsync(Workspace ws, string title, string text, string? url, bool error, string? icon = null)
    {
        var f = ws.Auto;
        if (f == null) return;
        var head = $"{icon ?? (error ? "⚠️" : "✅")} {title}";
        Remember(head, url); // cho mini popup ở khay
        var zalo = f.ZaloToken == null ? null : Platform.UnprotectToken(ws.ZaloKey, f.ZaloToken);
        if (string.IsNullOrEmpty(zalo)) return;
        var msg = $"{head} · {ws.Name}\n{text}" + (url != null ? $"\n{url}" : "");
        foreach (var c in f.ChatList)
        {
            try { await Zalo.SendAsync(zalo, c.Id, msg, _life.Token); }
            catch (Exception ex) { Log($"[Auto {ws.Name}] Không gửi được Zalo tới {(c.Name.Length > 0 ? c.Name : c.Id)}: {Describe(ex)}", LogKind.Warn); }
        }
    }
}

