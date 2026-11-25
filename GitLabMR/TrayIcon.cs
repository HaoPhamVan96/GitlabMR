using System.Runtime.InteropServices;

namespace GitLabMR;

/// <summary>
/// Icon ở khay hệ thống (Windows, Win32 thuần – không cần WinForms).
/// Bấm (trái hoặc phải) vào icon: hiện mini popup gồm trạng thái Auto, việc đang chờ, sự kiện gần đây và các nút.
/// Chạy trên 1 thread riêng có cửa sổ ẩn nhận tin nhắn của khay.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>1 dòng trong mini popup.</summary>
    public sealed record Item(string Text, Action? OnClick = null, bool Bold = false, bool Separator = false, bool Checked = false);

    private readonly string? _iconPath;
    private readonly Func<string> _tooltip;
    private readonly Func<List<Item>> _menu;
    private readonly Action _open;
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _hicon;
    private WndProc? _proc;              // giữ tham chiếu để GC không thu
    private readonly Dictionary<int, Action> _cmds = new();
    private const int WM_TRAY = 0x8000 + 1;
    private const uint ID = 1;

    public TrayIcon(string? iconPath, Func<string> tooltip, Func<List<Item>> menu, Action open)
    {
        _iconPath = iconPath; _tooltip = tooltip; _menu = menu; _open = open;
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread != null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "TrayIcon" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(3000);
    }

    /// <summary>Cập nhật tooltip (trạng thái) của icon.</summary>
    public void Refresh()
    {
        if (_hwnd == IntPtr.Zero) return;
        var d = Data(NIF_TIP);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    /// <summary>Bong bóng thông báo nhỏ cạnh khay.</summary>
    public void Balloon(string title, string text)
    {
        if (_hwnd == IntPtr.Zero) return;
        var d = Data(NIF_INFO);
        d.szInfoTitle = Cut(title, 63); d.szInfo = Cut(text, 255); d.dwInfoFlags = 1; // NIIF_INFO
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        var d = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        PostMessage(_hwnd, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
        _hwnd = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- Win32
    private void Loop(ManualResetEventSlim ready)
    {
        _proc = Proc;
        var cls = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null), lpszClassName = "GitLabMR.Tray",
        };
        RegisterClassEx(ref cls);
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "GitLabMR.Tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        _hicon = _iconPath != null ? LoadImage(IntPtr.Zero, _iconPath, 1 /*IMAGE_ICON*/, 16, 16, 0x10 /*LR_LOADFROMFILE*/) : IntPtr.Zero;
        if (_hicon == IntPtr.Zero) _hicon = LoadIcon(IntPtr.Zero, (IntPtr)32512 /*IDI_APPLICATION*/);
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIcon(NIM_ADD, ref d);
        ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
    }

    private IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_TRAY)
        {
            var ev = (int)l & 0xFFFF;
            if (ev is 0x0202 /*LBUTTONUP*/ or 0x0205 /*RBUTTONUP*/) ShowPopup();
            else if (ev == 0x0203 /*LBUTTONDBLCLK*/) _open();
            return IntPtr.Zero;
        }
        if (m == 0x0111 /*WM_COMMAND*/ && _cmds.TryGetValue((int)w & 0xFFFF, out var act))
        {
            try { act(); } catch { }
            return IntPtr.Zero;
        }
        if (m == 0x0002 /*WM_DESTROY*/) { PostQuitMessage(0); return IntPtr.Zero; }
        if (m == 0x0010 /*WM_CLOSE*/) { DestroyWindow(h); return IntPtr.Zero; }
        return DefWindowProc(h, m, w, l);
    }

    /// <summary>Mini popup: menu ngay trên khay, dòng đầu là trạng thái, sau là việc chờ / sự kiện / nút.</summary>
    private void ShowPopup()
    {
        var menu = CreatePopupMenu();
        _cmds.Clear();
        var id = 100;
        foreach (var it in _menu())
        {
            if (it.Separator) { AppendMenu(menu, 0x0800 /*MF_SEPARATOR*/, 0, null); continue; }
            uint flags = 0;
            if (it.OnClick == null) flags |= 0x0001 /*MF_GRAYED*/;
            if (it.Checked) flags |= 0x0008 /*MF_CHECKED*/;
            AppendMenu(menu, flags, (uint)id, it.Text);
            if (it.Bold) SetMenuDefaultItem(menu, (uint)id, 0);
            if (it.OnClick != null) _cmds[id] = it.OnClick;
            id++;
        }
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // để menu tự đóng khi bấm ra ngoài
        TrackPopupMenu(menu, 0x0008 /*TPM_BOTTOMALIGN*/ | 0x0004 /*TPM_CENTERALIGN*/, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = ID, uFlags = flags,
        uCallbackMessage = WM_TRAY, hIcon = _hicon, szTip = Cut(SafeTip(), 127), szInfo = "", szInfoTitle = "",
    };

    private string SafeTip() { try { return _tooltip(); } catch { return "GitLab MR"; } }
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public int uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState; public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public int style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint id, uint byPos);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int r, IntPtr h, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
