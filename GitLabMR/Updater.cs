using System.Reflection;
using Velopack;

namespace GitLabMR;

/// <summary>
/// Tự cập nhật qua Velopack.
/// - Bản cài bằng Setup.exe (Windows): kiểm tra GitHub Releases, tải ngầm bản mới; bấm "Cập nhật" là khởi động lại
///   vào bản mới, không bấm thì bản mới được cài khi đóng app (ApplyOnExit).
/// - Bản không cài qua Velopack (macOS, exe chạy trực tiếp, bản build tay): chỉ báo có bản mới + link tải.
/// Không dùng GitHub API (bị giới hạn 60 lần/giờ theo IP công ty) mà đọc thẳng file qua link releases/latest/download.
/// Mọi lỗi đều nuốt: cập nhật hỏng không bao giờ được làm hỏng app.
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/HaoPhamVan96/GitlabMR";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly Action<object> _send;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManager? _mgr;
    private VelopackAsset? _ready;      // đã tải xong, chờ khởi động lại
    private object _last;               // trạng thái gửi UI gần nhất (gửi lại khi UI nạp lại)

    public Updater(Action<object> send)
    {
        _send = send;
        _last = State("idle");
        try
        {
            // GITLABMR_UPDATE_URL: trỏ sang thư mục / URL khác để test
            var feed = Environment.GetEnvironmentVariable("GITLABMR_UPDATE_URL");
            var mgr = new UpdateManager(string.IsNullOrWhiteSpace(feed) ? RepoUrl + "/releases/latest/download" : feed.Trim());
            if (mgr.IsInstalled) _mgr = mgr;
        }
        catch { _mgr = null; }
    }

    /// <summary>true = cài qua Velopack, tự cập nhật được; false = chỉ báo có bản mới.</summary>
    public bool CanSelfUpdate => _mgr != null;

    public string CurrentVersion
    {
        get
        {
            try { if (_mgr?.CurrentVersion is { } v) return v.ToString(); } catch { }
            var a = Assembly.GetExecutingAssembly().GetName().Version;
            return a == null ? "0.0.0" : $"{a.Major}.{a.Minor}.{Math.Max(0, a.Build)}";
        }
    }

    private object State(string state, string? version = null, int progress = 0, string? error = null) => new
    {
        type = "update", state, current = CurrentVersion, version, progress, error,
        canSelfUpdate = CanSelfUpdate, url = RepoUrl + "/releases/latest",
    };

    private void Emit(object s) { _last = s; _send(s); }
    public void Resend() => _send(_last);

    /// <summary>Kiểm tra lần đầu sau vài giây rồi định kỳ mỗi 6 giờ.</summary>
    public void Start(CancellationToken stop) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), stop);
            while (!stop.IsCancellationRequested)
            {
                await CheckAsync(manual: false);
                await Task.Delay(Interval, stop);
            }
        }
        catch (OperationCanceledException) { }
    });

    public async Task CheckAsync(bool manual)
    {
        if (!await _gate.WaitAsync(0)) return; // đang kiểm tra / đang tải
        try
        {
            if (_ready != null) { Emit(State("ready", _ready.Version.ToString())); return; }
            if (manual) Emit(State("checking"));
            if (_mgr != null) await CheckVelopackAsync(manual);
            else await CheckLatestTagAsync(manual);
        }
        catch (Exception ex)
        {
            // Tự kiểm tra lỗi (mất mạng, chặn GitHub…) thì im lặng; chỉ báo khi người dùng bấm kiểm tra
            Emit(manual ? State("error", error: ex.Message) : State("idle"));
        }
        finally { _gate.Release(); }
    }

    private async Task CheckVelopackAsync(bool manual)
    {
        var mgr = _mgr!;
        if (mgr.UpdatePendingRestart is { } pending) // đã tải ở lần trước, chưa áp dụng
        {
            _ready = pending;
            Emit(State("ready", pending.Version.ToString()));
            return;
        }
        var info = await mgr.CheckForUpdatesAsync();
        if (info == null) { Emit(State(manual ? "latest" : "idle")); return; }

        var ver = info.TargetFullRelease.Version.ToString();
        var lastPct = -1;
        Emit(State("downloading", ver));
        await mgr.DownloadUpdatesAsync(info, p =>
        {
            if (p - lastPct < 5 && p < 100) return; // giảm số lần gửi UI
            lastPct = p;
            Emit(State("downloading", ver, p));
        });
        _ready = info.TargetFullRelease;
        Emit(State("ready", ver));
    }

    /// <summary>Không cài qua Velopack: lấy tag của release mới nhất qua redirect của /releases/latest.</summary>
    private async Task CheckLatestTagAsync(bool manual)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GitLabMR-updater");
        using var req = new HttpRequestMessage(HttpMethod.Head, RepoUrl + "/releases/latest");
        using var res = await http.SendAsync(req);
        var final = res.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        var tag = final[(final.LastIndexOf('/') + 1)..].TrimStart('v', 'V');
        if (Version.TryParse(tag, out var latest) && Version.TryParse(CurrentVersion, out var cur) && latest > cur)
            Emit(State("available", latest.ToString()));
        else
            Emit(State(manual ? "latest" : "idle"));
    }

    /// <summary>
    /// Gọi lúc đóng app: có bản đã tải xong thì để Update.exe áp dụng ngay sau khi process thoát (không mở lại app).
    /// Bỏ qua nếu còn cửa sổ GitLab MR khác đang chạy, vì Velopack sẽ đóng cưỡng bức chúng; để lần đóng sau.
    /// </summary>
    public void ApplyOnExit()
    {
        try
        {
            if (_mgr == null || _ready == null || OtherInstancesRunning()) return;
            _mgr.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
        }
        catch { /* lần đóng sau thử lại */ }
    }

    private static bool OtherInstancesRunning()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id) continue;
                try
                {
                    if (string.Equals(Path.GetDirectoryName(p.MainModule?.FileName), dir, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; } // không đọc được đường dẫn -> coi như đang chạy cho chắc
            }
        }
        return false;
    }

    /// <summary>Thoát và khởi động lại vào bản mới. Trả false nếu chưa có gì để áp dụng.</summary>
    public bool ApplyAndRestart()
    {
        if (_mgr == null || _ready == null) return false;
        _mgr.ApplyUpdatesAndRestart(_ready);
        return true;
    }
}
