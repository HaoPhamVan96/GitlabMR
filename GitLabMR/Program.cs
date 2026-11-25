using System.Reflection;
using Photino.NET;
using Velopack;

namespace GitLabMR;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Phải chạy đầu tiên: xử lý hook cài / gỡ / cập nhật của Velopack.
        // Tắt tự áp dụng lúc mở app: Velopack sẽ đóng cưỡng bức các cửa sổ khác của app (có thể đang chạy batch).
        // Bản mới được áp dụng khi đóng app (Updater.ApplyOnExit) hoặc khi bấm nút Cập nhật.
        // App không cài qua Velopack (macOS, chạy exe trực tiếp) thì không làm gì.
        try { VelopackApp.Build().SetAutoApplyOnStartup(false).Run(); } catch { }

        // 1 máy chỉ chạy 1 app: mở lần nữa thì đưa cửa sổ đang chạy lên trước rồi thoát
        // (2 app cùng chạy Auto sẽ submit / merge / báo Zalo trùng và giành tin nhắn của bot).
        // Chờ ngắn để bản cũ kịp thoát khi tự cập nhật khởi động lại.
        using var single = new Mutex(false, @"Local\GitLabMR.SingleInstance" + InstanceSuffix);
        bool owned;
        try { owned = single.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { owned = true; } // bản trước bị tắt ngang: vẫn nhận được
        if (!owned)
        {
            // App đang chạy (có thể đang ẩn ở khay): báo nó hiện cửa sổ lên
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); }
            catch { ActivateRunning(); }
            return;
        }

        string html;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html")!)
        using (var reader = new StreamReader(stream))
            html = reader.ReadToEnd();

        // Mở lại đúng kích thước lần trước; mặc định đủ rộng cho tên branch dài
        var saved = AppConfig.Load();
        var width = saved.WindowWidth >= 600 ? Math.Min(saved.WindowWidth, 4000) : 900;
        var height = saved.WindowHeight >= 460 ? Math.Min(saved.WindowHeight, 3000) : 640;

        var window = new PhotinoWindow()
            .SetTitle("GitLab MR")
            .SetUseOsDefaultSize(false)
            .SetSize(width, height)
            .SetMinSize(600, 460)
            .SetResizable(true)
            .SetContextMenuEnabled(true)
#if !DEBUG
            .SetDevToolsEnabled(false)
#endif
            .Center();

        // Icon cửa sổ / taskbar (Windows cần file .ico trên đĩa; macOS lấy icon từ .app bundle)
        var iconPath = OperatingSystem.IsWindows() ? ExtractIcon() : null;
        if (iconPath != null) window.SetIconFile(iconPath);

        if (saved.WindowMaximized) window.SetMaximized(true);

        var host = new AppHost(window);
        window.RegisterSizeChangedHandler((_, size) => host.RememberWindow(size.Width, size.Height, SafeMaximized(window)));
        window.RegisterWebMessageReceivedHandler((_, message) => host.OnMessage(message));

        // Windows: bấm ✕ thì ẩn xuống khay hệ thống, app vẫn chạy nền (Auto, Zalo, pipeline…). Thoát hẳn từ menu khay.
        TrayIcon? tray = null;
        System.Threading.Timer? refresh = null;
        var quitting = false;
        var told = false;
        void ShowWin() => window.Invoke(() =>
        {
            var h = window.WindowHandle;
            ShowWindow(h, 5 /*SW_SHOW*/);
            if (IsIconic(h)) ShowWindow(h, 9 /*SW_RESTORE*/);
            SetForegroundWindow(h);
        });
        void Quit() { quitting = true; window.Invoke(() => window.Close()); }
        if (OperatingSystem.IsWindows())
        {
            tray = new TrayIcon(iconPath, host.TrayTooltip, () => host.TrayMenu(ShowWin, Quit), ShowWin);
            tray.Start();
            window.RegisterWindowClosingHandler((_, _) =>
            {
                if (quitting) return false;
                ShowWindow(window.WindowHandle, 0 /*SW_HIDE*/);
                if (!told) { told = true; tray.Balloon("GitLab MR vẫn chạy nền", "Auto vẫn quét Gerrit, merge GitLab, báo Zalo. Bấm icon ở khay để xem, chọn \"Thoát hẳn\" để tắt."); }
                return true; // không đóng
            });
            // Mở app lần nữa (shortcut / Start) khi đang ẩn: hiện lại cửa sổ
            var showEv = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() => { while (showEv.WaitOne()) ShowWin(); }) { IsBackground = true, Name = "ShowSignal" }.Start();
            refresh = new System.Threading.Timer(_ => tray.Refresh(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
        }

        window.LoadRawString(html);
        window.WaitForClose();
        refresh?.Dispose();
        tray?.Dispose();
        host.Shutdown();
        single.ReleaseMutex();
    }

    private static string ShowEventName => @"Local\GitLabMR.Show" + InstanceSuffix;
    /// <summary>Chỉ để test: GITLABMR_INSTANCE=x chạy 1 bản riêng song song với bản đang dùng.</summary>
    private static string InstanceSuffix => Environment.GetEnvironmentVariable("GITLABMR_INSTANCE") is { Length: > 0 } x ? "." + x : "";

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);

    /// <summary>Đưa cửa sổ GitLab MR đang chạy lên trước (Windows). macOS tự lo việc chỉ mở 1 app.</summary>
    private static void ActivateRunning()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                using (p)
                {
                    if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
            }
        }
        catch { /* không đưa lên được thì thôi */ }
    }

    private static bool SafeMaximized(PhotinoWindow w)
    {
        try { return w.Maximized; } catch { return false; }
    }

    private static string? ExtractIcon()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "GitLabMR");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "icon.ico");
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico");
            if (src == null) return null;
            using (var dst = File.Create(path)) src.CopyTo(dst);
            return path;
        }
        catch
        {
            return null; // thiếu icon thì dùng icon mặc định, không sao
        }
    }
}
