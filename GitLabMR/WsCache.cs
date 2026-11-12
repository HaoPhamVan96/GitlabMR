using System.Text.Json;

namespace GitLabMR;

public sealed class BranchCount
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>Danh sách branch gợi ý (gom từ nhiều project) + thời điểm tải.</summary>
public sealed class BranchList
{
    public int Total { get; set; }
    public List<BranchCount> Items { get; set; } = new();
    public DateTime At { get; set; }
}

public sealed class CachedProject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string WebUrl { get; set; } = "";
    public string? DefaultBranch { get; set; }
}

/// <summary>
/// Cache danh sách project + branch của 1 workspace, lưu tại %APPDATA%\GitLabMR\cache\&lt;id&gt;.json.
/// Mở app / đổi tab hiện ngay từ cache, không gọi server; chỉ tải lại khi bấm ⟳ hoặc cache quá cũ.
/// </summary>
public sealed class WsCache
{
    public DateTime SavedAt { get; set; }
    public string Host { get; set; } = "";
    public string User { get; set; } = "";
    public int Hidden { get; set; }
    public List<CachedProject> Projects { get; set; } = new();
    public BranchList? Branches { get; set; }

    private static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitLabMR", "cache");

    private static string FileOf(string wsId) => System.IO.Path.Combine(Dir, wsId + ".json");

    public static WsCache? Load(string wsId)
    {
        try
        {
            var f = FileOf(wsId);
            return File.Exists(f) ? JsonSerializer.Deserialize<WsCache>(File.ReadAllText(f)) : null;
        }
        catch
        {
            return null; // cache hỏng -> coi như không có
        }
    }

    public void Save(string wsId)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FileOf(wsId), JsonSerializer.Serialize(this));
        }
        catch
        {
            // không ghi được cache thì thôi, lần sau tải lại
        }
    }

    public static void Delete(string wsId)
    {
        try { File.Delete(FileOf(wsId)); } catch { /* ignore */ }
    }
}
