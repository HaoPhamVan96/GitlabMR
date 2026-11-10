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

        var host = new AppHost(window);
        window.RegisterWebMessageReceivedHandler((_, message) => host.OnMessage(message));
        window.LoadRawString(html);
        window.WaitForClose();
        host.Shutdown();
    }
}
