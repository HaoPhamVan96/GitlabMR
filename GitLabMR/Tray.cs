namespace GitLabMR;

/// <summary>Dữ liệu cho icon khay / mini popup: trạng thái Auto, việc đang chờ, sự kiện gần đây.</summary>
public sealed partial class AppHost
{
    private readonly List<(DateTime At, string Text, string? Url)> _recent = new();
    private bool _autoPaused;

    private void Remember(string text, string? url)
    {
        lock (_recent)
        {
            _recent.Insert(0, (DateTime.Now, text, url));
            if (_recent.Count > 6) _recent.RemoveAt(_recent.Count - 1);
        }
    }

    private IEnumerable<Workspace> AutoWorkspaces() =>
        _cfg.Workspaces.Where(w => w.Kind == "gerrit" && w.Auto?.Enabled == true);

    private string AutoLine(Workspace ws)
    {
        var a = AutoOf(ws.Id);
        var f = ws.Auto!;
        var st = _autoPaused ? "tạm dừng"
            : a.Loop == null ? (_autoLock == null ? "chạy ở cửa sổ khác" : "đang tắt")
            : a.LastError ? "lỗi" : f.DryRun ? "chạy thử" : "đang chạy";
        var when = a.LastRun is { } t ? $" · {t:HH:mm}" : "";
        return $"{ws.Name}: {st}{when}";
    }

    /// <summary>Tooltip của icon khay.</summary>
    public string TrayTooltip()
    {
        var ws = AutoWorkspaces().ToList();
        if (ws.Count == 0) return "GitLab MR · Auto đang tắt";
        var waiting = ws.Sum(w => { var a = AutoOf(w.Id); lock (a.Jobs) return a.Jobs.Count + a.Pipes.Count; });
        return "GitLab MR · " + string.Join(" | ", ws.Select(AutoLine)) + (waiting > 0 ? $" · {waiting} việc đang chờ" : "");
    }

    /// <summary>Nội dung mini popup.</summary>
    internal List<TrayIcon.Item> TrayMenu(Action open, Action quit)
    {
        var items = new List<TrayIcon.Item>();
        var ws = AutoWorkspaces().ToList();
        if (ws.Count == 0) items.Add(new("⚡ Auto đang tắt (bật trong app → ⚡ Auto)"));
        foreach (var w in ws)
        {
            var a = AutoOf(w.Id);
            items.Add(new("⚡ " + AutoLine(w)));
            if (a.LastText.Length > 0 || a.GlText.Length > 0)
                items.Add(new("     " + a.LastText + (a.GlText.Length > 0 ? ", " + a.GlText : "")));
            int jobs, pipes, otps;
            lock (a.Jobs) { jobs = a.Jobs.Count; pipes = a.Pipes.Count + a.NewPipes.Count; otps = a.Otps.Count; }
            if (jobs + pipes + otps > 0)
                items.Add(new($"     ⏳ {jobs} MR chờ merge · {pipes} pipeline đang chạy" + (otps > 0 ? $" · {otps} conflict chờ fix" : "")));
        }

        List<(DateTime At, string Text, string? Url)> recent;
        lock (_recent) recent = _recent.ToList();
        items.Add(new("", Separator: true));
        if (recent.Count == 0) items.Add(new("Chưa có sự kiện nào"));
        foreach (var r in recent)
        {
            var url = r.Url;
            items.Add(new($"{r.At:HH:mm}  {r.Text}" + (url != null ? "  ↗" : ""), url != null ? () => Platform.OpenUrl(url) : null));
        }

        items.Add(new("", Separator: true));
        items.Add(new("Mở GitLab MR", open, Bold: true));
        if (ws.Count > 0)
        {
            items.Add(new("Quét ngay", () => { foreach (var w in AutoWorkspaces()) _ = Task.Run(() => AutoTickAsync(w, _life.Token)); }));
            items.Add(new("Tạm dừng Auto", () =>
            {
                _autoPaused = !_autoPaused;
                if (_autoPaused) { foreach (var w in AutoWorkspaces()) StopAuto(w.Id); Log("[Auto] Tạm dừng từ khay hệ thống", LogKind.Warn); }
                else { Log("[Auto] Chạy lại từ khay hệ thống", LogKind.Info); StartAllAuto(); }
                foreach (var w in AutoWorkspaces()) SendAutoState(w);
            }, Checked: _autoPaused));
        }
        items.Add(new("Thoát hẳn", quit));
        return items;
    }
}
