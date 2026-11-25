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
        using var single = new Mutex(false, @"Local\GitLabMR.SingleInstance");
        bool owned;
        try { owned = single.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { owned = true; } // bản trước bị tắt ngang: vẫn nhận được
        if (!owned)
        {
            ActivateRunning();
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
        if (OperatingSystem.IsWindows() && ExtractIcon() is { } iconPath) window.SetIconFile(iconPath);

        if (saved.WindowMaximized) window.SetMaximized(true);

        var host = new AppHost(window);
        window.RegisterSizeChangedHandler((_, size) => host.RememberWindow(size.Width, size.Height, SafeMaximized(window)));
        window.RegisterWebMessageReceivedHandler((_, message) => host.OnMessage(message));
        window.LoadRawString(html);
        window.WaitForClose();
        host.Shutdown();
        single.ReleaseMutex();
    }

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
