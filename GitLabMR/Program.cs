using System.Reflection;
using Photino.NET;

namespace GitLabMR;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string html;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html")!)
        using (var reader = new StreamReader(stream))
            html = reader.ReadToEnd();

        var window = new PhotinoWindow()
            .SetTitle("GitLab MR")
            .SetUseOsDefaultSize(false)
            .SetSize(760, 640)
            .SetMinSize(560, 460)
            .SetResizable(true)
            .SetContextMenuEnabled(true)
#if !DEBUG
            .SetDevToolsEnabled(false)
#endif
            .Center();

        // Icon cửa sổ / taskbar (Windows cần file .ico trên đĩa; macOS lấy icon từ .app bundle)
        if (OperatingSystem.IsWindows() && ExtractIcon() is { } iconPath) window.SetIconFile(iconPath);

        var host = new AppHost(window);
        window.RegisterWebMessageReceivedHandler((_, message) => host.OnMessage(message));
        window.LoadRawString(html);
        window.WaitForClose();
        host.Shutdown();
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
