using System.Text.Json;

namespace GitLabMR;

/// <summary>
/// Cấu hình lưu tại %APPDATA%\GitLabMR\config.json (Windows) hoặc ~/.config/GitLabMR/config.json (macOS).
/// Token: Windows mã hoá DPAPI, macOS cất trong Keychain.
/// </summary>
public sealed class AppConfig
{
    public List<string> Projects { get; set; } = new();
    public string? LastProject { get; set; }
    /// <summary>host (lowercase) -> token đã bảo vệ (xem Platform.ProtectToken)</summary>
    public Dictionary<string, string> Tokens { get; set; } = new();
    /// <summary>group/project path -> danh sách project đã tick lần trước</summary>
    public Dictionary<string, List<string>> Selected { get; set; } = new();
    public List<string> BranchHistory { get; set; } = new() { "develop", "pre-uat", "uat", "staging", "master", "main" };
    public string? LastSourceBranch { get; set; }
    public string? LastTargetBranch { get; set; }
    public string TitleTemplate { get; set; } = "Merge branch '{source}' into '{target}'";

    /// <summary>Ẩn project có đường dẫn chứa một trong các chuỗi này (không phân biệt hoa thường).</summary>
    public List<string> Exclude { get; set; } = new()
    {
        "/libraries/",
        "/peoplex-catalog", "/peoplex.cform", "/peoplex.corebiz", "/peoplex-entitysync",
        "/peoplex.http", "/peoplex-security", "/peoplex-workflow", "/peoplex.xreport",
    };

    public bool IgnoreSsl { get; set; }
    public bool Merge { get; set; } = true;
    public bool AutoMerge { get; set; } = true;
    public bool RemoveSource { get; set; }
    public bool Squash { get; set; }

    public bool IsExcluded(string path) =>
        Exclude.Any(x => x.Length > 0 && (path + "/").Contains(x, StringComparison.OrdinalIgnoreCase));

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitLabMR", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
        }
        catch
        {
            // file hỏng -> dùng cấu hình mặc định
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public string? GetToken(string host)
    {
        host = host.ToLowerInvariant();
        return Tokens.TryGetValue(host, out var stored) ? Platform.UnprotectToken(host, stored) : null;
    }

    public void SetToken(string host, string token)
    {
        host = host.ToLowerInvariant();
        Tokens[host] = Platform.ProtectToken(host, token);
    }

    public void RememberProject(string url)
    {
        Projects.RemoveAll(p => string.Equals(p, url, StringComparison.OrdinalIgnoreCase));
        Projects.Insert(0, url);
        if (Projects.Count > 30) Projects.RemoveRange(30, Projects.Count - 30);
        LastProject = url;
    }

    public void RememberBranch(string branch)
    {
        BranchHistory.RemoveAll(b => b == branch);
        BranchHistory.Insert(0, branch);
        if (BranchHistory.Count > 30) BranchHistory.RemoveRange(30, BranchHistory.Count - 30);
    }
}
