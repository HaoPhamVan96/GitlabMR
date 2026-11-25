using System.IO.Compression;
using System.Text.RegularExpressions;

namespace GitLabMR;

/// <summary>
/// Chặn file Excel sinh bằng code (Python / AI) trên Gerrit: Black Duck so dấu vân tay file và gán nhầm thành component
/// (vd excel_writer) khi file .xlsx chưa được mở bằng Excel và Save lại.
///  - Change đang mở trong repo của workspace có file .xlsx / .xlsm / .xltx thì tải về, đọc docProps/app.xml.
///  - Không có thông tin ứng dụng, hoặc ứng dụng không phải Microsoft Excel (Openpyxl, …): Code-Review −2 + comment vào file + báo Zalo.
///  - Patchset mới đã save lại bằng Excel: gỡ −2 của mình (−2 thường được giữ sang patchset mới).
/// </summary>
public sealed partial class AppHost
{
    private static readonly string[] XlsxExt = { ".xlsx", ".xlsm", ".xltx", ".xltm" };

    private const string XlsxNote =
        "File Excel này sinh bằng code (Python / AI) và chưa được mở bằng Excel để Save lại, nên Black Duck nhận nhầm thành thư viện (vd excel_writer). " +
        "Cần mở file bằng Microsoft Excel, Save lại rồi push patchset mới.";

    /// <summary>Lý do file Excel bị coi là sinh bằng code, null = file đã được Excel save (hoặc không đọc được).</summary>
    public static string? XlsxGenerated(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var app = zip.GetEntry("docProps/app.xml");
            if (app == null) return "không có thông tin ứng dụng tạo file (sinh bằng code)";
            using var r = new StreamReader(app.Open());
            var m = Regex.Match(r.ReadToEnd(), "<Application>(.*?)</Application>");
            var name = m.Success ? m.Groups[1].Value.Trim() : "";
            if (name.Length == 0) return "không ghi ứng dụng tạo file (sinh bằng code)";
            if (name.Contains("openpyxl", StringComparison.OrdinalIgnoreCase) || !name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
                return $"tạo bởi \"{name}\", chưa save lại bằng Excel";
            return null;
        }
        catch { return null; } // không phải zip hợp lệ: bỏ qua
    }

    /// <summary>"core-pr" khớp "peoplex/api/core-pr"; đường dẫn đầy đủ thì khớp đúng.</summary>
    private static bool XlsxRepoMatch(List<string> repos, string project) =>
        repos.Any(r => project.Equals(r.Trim('/'), StringComparison.OrdinalIgnoreCase)
                       || project.EndsWith("/" + r.Trim('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Quét change đang mở có file Excel trong repo của workspace. Trả về số change vừa −2.</summary>
    private async Task<int> XlsxGuardAsync(Workspace ws, AutoState a, GerritClient gc, bool full, DateTime started, CancellationToken ct)
    {
        var f = ws.Auto!;
        if (!f.XlsxGuard) return 0;
        var tag = $"[Auto {ws.Name}]";
        var q = "status:open -is:wip file:\"^.*\\.(xlsx|xlsm|xltx|xltm)$\"";
        if (!full && a.XlsxDelta is { } d) q += $" -age:{(int)(started - d).TotalSeconds + 60}s";
        var changes = await gc.QueryChangesAsync(q, ct);
        a.XlsxDelta = started;
        var inWs = WsCache.Load(ws.Id)?.Projects.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        if (inWs is { Count: > 0 }) changes = changes.Where(c => inWs.Contains(c.Project)).ToList();
        // Chỉ các repo đã chọn cho phần quét Excel (trống = mọi repo của tab)
        if (f.XlsxRepos.Count > 0) changes = changes.Where(c => XlsxRepoMatch(f.XlsxRepos, c.Project)).ToList();

        a.GcSelf ??= await gc.GetSelfAsync(ct);
        var blocked = 0;
        foreach (var c in changes)
        {
            ct.ThrowIfCancellationRequested();
            var key = ChangeKey(c.Project, c.Number);
            var rev = c.CurrentRevision ?? c.Updated ?? "";
            if (a.XlsxChecked.TryGetValue(key, out var done) && done == rev) continue;
            var label = $"{c.Project} #{c.Number}";
            var url = gc.ChangeUrl(c.Project, c.Number);
            try
            {
                var files = await gc.GetFilesAsync(c.Project, c.Number, ct);
                var bad = new Dictionary<string, string>();
                foreach (var (path, info) in files)
                {
                    if (info.Status == "D" || !XlsxExt.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                    if (XlsxGenerated(await gc.GetFileContentAsync(c.Project, c.Number, path, ct)) is { } why) bad[path] = why;
                }
                var mine = (c.Labels?.GetValueOrDefault("Code-Review")?.All ?? new())
                    .FirstOrDefault(v => string.Equals(v.Username, a.GcSelf, StringComparison.OrdinalIgnoreCase))?.Value ?? 0;
                bool wasMine; lock (a.Jobs) wasMine = a.XlsxBlocked.Contains(key);

                if (bad.Count > 0 && mine > -2)
                {
                    var list = string.Join("\n", bad.Select(kv => $"- {kv.Key}: {kv.Value}"));
                    if (f.DryRun) Log($"{tag} [Thử] Sẽ −2 {label} vì file Excel sinh bằng code:\n{list}", LogKind.Warn);
                    else
                    {
                        await gc.ReviewAsync(c.Project, c.Number, -2, $"−2: có file Excel sinh bằng code.\n{list}\n\n{XlsxNote}",
                            bad.ToDictionary(kv => kv.Key, kv => $"{kv.Value}. {XlsxNote}"), ct);
                        lock (a.Jobs) a.XlsxBlocked.Add(key);
                        blocked++;
                        Log($"{tag} {label}: −2 vì {bad.Count} file Excel sinh bằng code", LogKind.Warn);
                        await NotifyAsync(ws, "Đã −2: file Excel sinh bằng code",
                            $"{label}: {c.Subject}\nDev: {c.Owner?.Name ?? c.Owner?.Username}\n{list}\nMở bằng Excel → Save → push lại để không dính Black Duck.", url, pending: true);
                    }
                }
                else if (bad.Count == 0 && mine == -2 && wasMine)
                {
                    if (f.DryRun) Log($"{tag} [Thử] Sẽ gỡ −2 {label}: file Excel đã được save lại", LogKind.Info);
                    else
                    {
                        await gc.ReviewAsync(c.Project, c.Number, 0, "File Excel đã được save lại bằng Excel – gỡ −2.", null, ct);
                        lock (a.Jobs) a.XlsxBlocked.Remove(key);
                        Log($"{tag} {label}: file Excel đã save lại → gỡ −2", LogKind.Success);
                        await NotifyAsync(ws, "Đã gỡ −2: file Excel đã save lại", $"{label}: {c.Subject}", url);
                    }
                }
                a.XlsxChecked[key] = rev;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Once(a, $"xlerr|{key}|{Describe(ex)}", $"{tag} {label}: không kiểm tra được file Excel – {Describe(ex)}", LogKind.Warn);
            }
        }
        return blocked;
    }
}
