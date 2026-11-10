using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GitLabMR;

/// <summary>Những thứ phụ thuộc hệ điều hành: lưu token an toàn, clipboard, mở link.</summary>
internal static class Platform
{
    private const string KeychainService = "GitLabMR";

    // ---------------------------------------------------------------- token
    /// <summary>
    /// Trả về chuỗi lưu vào config.json.
    /// Windows: "dpapi:&lt;base64&gt;" (chỉ user Windows hiện tại giải mã được).
    /// macOS: token cất trong Keychain, config chỉ lưu "keychain:".
    /// </summary>
    public static string ProtectToken(string host, string token)
    {
        if (OperatingSystem.IsWindows()) return "dpapi:" + Dpapi.Protect(token);
        if (OperatingSystem.IsMacOS() && Keychain.Set(host, token)) return "keychain:";
        return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
    }

    public static string? UnprotectToken(string host, string stored)
    {
        try
        {
            if (stored.StartsWith("keychain:")) return Keychain.Get(host);
            if (stored.StartsWith("plain:")) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
            if (OperatingSystem.IsWindows())
                return Dpapi.Unprotect(stored.StartsWith("dpapi:") ? stored[6..] : stored); // bản cũ không có prefix
        }
        catch
        {
            // token hỏng / không giải mã được -> coi như chưa lưu
        }
        return null;
    }

    // ---------------------------------------------------------------- clipboard
    public static void CopyText(string text)
    {
        if (OperatingSystem.IsWindows()) { WinClipboard.SetText(text); return; }
        if (OperatingSystem.IsMacOS()) { Pipe("pbcopy", "", text); return; }
        Pipe("xclip", "-selection clipboard", text);
    }

    // ---------------------------------------------------------------- open url
    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch
        {
            // ignore
        }
    }

    private static string Run(string file, IEnumerable<string> args, out int exitCode)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        exitCode = p.ExitCode;
        return output;
    }

    private static void Pipe(string file, string args, string input)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardInput = true, UseShellExecute = false };
        psi.Environment["LANG"] = "en_US.UTF-8";
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(input);
        p.StandardInput.Close();
        p.WaitForExit(5000);
    }

    // ---------------------------------------------------------------- macOS Keychain (qua lệnh `security`)
    private static class Keychain
    {
        public static bool Set(string account, string secret)
        {
            try
            {
                Run("security", new[] { "add-generic-password", "-U", "-s", KeychainService, "-a", account, "-w", secret }, out var code);
                return code == 0;
            }
            catch { return false; }
        }

        public static string? Get(string account)
        {
            var output = Run("security", new[] { "find-generic-password", "-s", KeychainService, "-a", account, "-w" }, out var code);
            return code == 0 ? output.TrimEnd('\r', '\n') : null;
        }
    }

    // ---------------------------------------------------------------- Windows DPAPI (P/Invoke, không cần NuGet)
    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static string Protect(string plain) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plain), true));
        public static string Unprotect(string b64) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(b64), false));

        private static byte[] Transform(byte[] data, bool protect)
        {
            var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
            var output = new DataBlob();
            try
            {
                Marshal.Copy(data, 0, input.pbData, data.Length);
                var ok = protect
                    ? CryptProtectData(ref input, "GitLabMR", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output);
                if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
    }

    // ---------------------------------------------------------------- Windows clipboard (CF_UNICODETEXT)
    private static class WinClipboard
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr data);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        public static void SetText(string text)
        {
            for (var attempt = 0; attempt < 10 && !OpenClipboard(IntPtr.Zero); attempt++) Thread.Sleep(50);
            try
            {
                EmptyClipboard();
                var bytes = (text.Length + 1) * 2;
                var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hGlobal == IntPtr.Zero) throw new OutOfMemoryException();
                var target = GlobalLock(hGlobal);
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    Marshal.WriteInt16(target, text.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }
                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
    }
}
