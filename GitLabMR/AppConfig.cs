using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLabMR;

/// <summary>Một workspace = một GitLab (link group/repo + access token riêng), hiện thành 1 tab.</summary>
public sealed class Workspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Workspace";
    /// <summary>Mỗi phần tử là URL group hoặc repo (cùng 1 GitLab host).</summary>
    public List<string> Sources { get; set; } = new();
    /// <summary>Token đã bảo vệ (Platform.ProtectToken): Windows DPAPI, macOS Keychain.</summary>
    public string? Token { get; set; }
    public bool IgnoreSsl { get; set; }
    public List<string> Favorites { get; set; } = new();
    public List<string> Selected { get; set; } = new();
    public string? SourceBranch { get; set; }
    public string? TargetBranch { get; set; }
    /// <summary>Chế độ đang mở: "mr" (tạo/merge MR) hoặc "branch" (tạo branch hàng loạt).</summary>
    public string Mode { get; set; } = "mr";
    public string? BranchBase { get; set; }
    public string? BranchName { get; set; }

    [JsonIgnore] public string SecretKey => "ws-" + Id;
}

/// <summary>
/// Cấu hình lưu tại %APPDATA%\GitLabMR\config.json (Windows) hoặc ~/.config/GitLabMR/config.json (macOS).
/// </summary>
public sealed class AppConfig
{
    public List<Workspace> Workspaces { get; set; } = new();
    public string? ActiveWorkspace { get; set; }

    public List<string> BranchHistory { get; set; } = new() { "develop", "pre-uat", "uat", "staging", "master", "main" };
    public string TitleTemplate { get; set; } = "Merge branch '{source}' into '{target}'";

    /// <summary>Ẩn project có đường dẫn chứa một trong các chuỗi này khi lấy cả group (không phân biệt hoa thường).</summary>
    public List<string> Exclude { get; set; } = new()
    {
        "/libraries/",
        "/peoplex-catalog", "/peoplex.cform", "/peoplex.corebiz", "/peoplex-entitysync",
        "/peoplex.http", "/peoplex-security", "/peoplex-workflow", "/peoplex.xreport",
    };

    public bool Merge { get; set; } = true;
    public bool AutoMerge { get; set; } = true;
    public bool RemoveSource { get; set; }
    public bool Squash { get; set; }

    // ---- Trường của bản cũ (1 GitLab duy nhất) – chỉ đọc để chuyển sang Workspace, không ghi lại nữa
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LastProject { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Sources { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Favorites { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, string>? Tokens { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, List<string>>? Selected { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LastSourceBranch { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LastTargetBranch { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? IgnoreSsl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Projects { get; set; }

    public bool IsExcluded(string path) =>
        Exclude.Any(x => x.Length > 0 && (path + "/").Contains(x, StringComparison.OrdinalIgnoreCase));

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitLabMR", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        AppConfig cfg;
        try
        {
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig()
                : new AppConfig();
        }
        catch
        {
            cfg = new AppConfig(); // file hỏng -> mặc định
        }
        cfg.MigrateLegacy();
        if (cfg.Workspaces.Count == 0) cfg.Workspaces.Add(new Workspace { Name = "Workspace 1" });
        if (cfg.Workspaces.All(w => w.Id != cfg.ActiveWorkspace)) cfg.ActiveWorkspace = cfg.Workspaces[0].Id;
        return cfg;
    }

    /// <summary>Bản cũ chỉ có 1 GitLab -> gom thành workspace đầu tiên (giữ token, favorite, lựa chọn).</summary>
    private void MigrateLegacy()
    {
        if (Workspaces.Count == 0)
        {
            var sources = Sources is { Count: > 0 } ? Sources
                : !string.IsNullOrWhiteSpace(LastProject) ? new List<string> { LastProject! } : new List<string>();
            if (sources.Count > 0 || Tokens is { Count: > 0 })
            {
                var ws = new Workspace
                {
                    Sources = sources,
                    IgnoreSsl = IgnoreSsl ?? false,
                    Favorites = Favorites ?? new(),
                    Selected = Selected?.Values.FirstOrDefault() ?? new(),
                    SourceBranch = LastSourceBranch,
                    TargetBranch = LastTargetBranch,
                };
                ProjectRef.TryParse(sources.FirstOrDefault(), out var r, out _);
                ws.Name = r?.Path.Split('/')[^1] ?? "Workspace 1";
                if (r != null && Tokens != null && Tokens.TryGetValue(r.Host, out var stored))
                {
                    var plain = Platform.UnprotectToken(r.Host, stored);
                    if (plain != null) SetToken(ws, plain);
                }
                Workspaces.Add(ws);
                ActiveWorkspace = ws.Id;
            }
        }
        LastProject = null; Sources = null; Favorites = null; Tokens = null; Selected = null;
        LastSourceBranch = null; LastTargetBranch = null; IgnoreSsl = null; Projects = null;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static string? GetToken(Workspace ws) =>
        ws.Token == null ? null : Platform.UnprotectToken(ws.SecretKey, ws.Token);

    public static void SetToken(Workspace ws, string token) => ws.Token = Platform.ProtectToken(ws.SecretKey, token);

    public void RememberBranch(string branch)
    {
        BranchHistory.RemoveAll(b => b == branch);
        BranchHistory.Insert(0, branch);
        if (BranchHistory.Count > 30) BranchHistory.RemoveRange(30, BranchHistory.Count - 30);
    }
}
