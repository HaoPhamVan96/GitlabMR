using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLabMR;

/// <summary>Một workspace = một GitLab (link group/repo + access token riêng), hiện thành 1 tab.</summary>
public sealed class Workspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Workspace";
    /// <summary>"gitlab" (MR + branch) hoặc "gerrit" (chỉ tạo branch).</summary>
    public string Kind { get; set; } = "gitlab";
    /// <summary>Username Gerrit (GitLab không cần).</summary>
    public string? Username { get; set; }
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
    /// <summary>Gerrit: tự +2 & submit change đã được reviewer +1, rồi merge bên GitLab (null = chưa cấu hình).</summary>
    public AutoFlow? Auto { get; set; }

    [JsonIgnore] public string SecretKey => "ws-" + Id;
    [JsonIgnore] public string ZaloKey => "zalo-" + Id;
}

/// <summary>
/// Auto flow của 1 workspace Gerrit: quét định kỳ change của các dev đã đặt, có reviewer +1 thì +2 & submit;
/// submit xong thì tạo / merge MR trên 1 workspace GitLab. Conflict / lỗi báo qua Zalo Bot.
/// </summary>
public sealed class ZaloChat
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>PRIVATE / GROUP.</summary>
    public string Type { get; set; } = "";
}

public sealed class AutoFlow
{
    public bool Enabled { get; set; }
    /// <summary>Chạy thử: chỉ ghi nhật ký những gì sẽ làm, không +2 / submit / merge.</summary>
    public bool DryRun { get; set; } = true;
    public int IntervalSec { get; set; } = 60;
    /// <summary>Chỉ change vào branch này (trống = mọi branch).</summary>
    public string? Branch { get; set; }
    /// <summary>Ai +1 (Code-Review) thì submit: username / email / tên.</summary>
    public List<string> Reviewers { get; set; } = new();
    /// <summary>Chỉ change do các account này tạo.</summary>
    public List<string> Owners { get; set; } = new();
    /// <summary>Workspace GitLab để tạo / merge MR sau khi submit (trống = chỉ submit Gerrit).</summary>
    public string? GitLabWs { get; set; }
    /// <summary>Branch nguồn bên GitLab, {branch} = branch của change trên Gerrit.</summary>
    public string GitLabSource { get; set; } = "{branch}";
    public string? GitLabTarget { get; set; }
    public bool GitLabMerge { get; set; } = true;
    /// <summary>Bot token Zalo đã bảo vệ + chat ID (người / nhóm) nhận thông báo.</summary>
    public string? ZaloToken { get; set; }
    /// <summary>Cũ (1 chat ID) – chuyển sang ZaloChats khi lưu.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ZaloChatId { get; set; }
    /// <summary>Người / nhóm nhận thông báo (tối đa 3).</summary>
    public List<ZaloChat> ZaloChats { get; set; } = new();
    [JsonIgnore] public List<ZaloChat> ChatList =>
        ZaloChats.Count > 0 || string.IsNullOrEmpty(ZaloChatId) ? ZaloChats : new() { new ZaloChat { Id = ZaloChatId } };
    public bool NotifySuccess { get; set; }
    /// <summary>Merge xong theo dõi pipeline trên branch đích, build xong (pass / fail) thì báo.</summary>
    public bool NotifyPipeline { get; set; } = true;
    /// <summary>Còn comment chưa resolve (viết trước lúc reviewer +1) thì tự trả lời "Done", resolve rồi submit.</summary>
    public bool AutoResolve { get; set; } = true;
    /// <summary>Conflict MR GitLab: tạo branch trung gian (từ branch nguồn, merge branch đích vào), nhờ Claude Code sửa, rồi merge branch đó.</summary>
    public bool ConflictFix { get; set; }
    /// <summary>Tên branch trung gian: {branch} = branch nguồn, {target} = branch đích.</summary>
    public string ConflictBranch { get; set; } = "{branch}_fix_conflict";
    /// <summary>Đường dẫn claude.exe (trống = tự tìm).</summary>
    public string? ClaudePath { get; set; }
    /// <summary>Model Claude (--model): opus, sonnet, haiku hoặc tên đầy đủ; trống = mặc định của Claude Code.</summary>
    public string? ClaudeModel { get; set; }
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
    /// <summary>Cache danh sách project/branch cũ hơn số giờ này thì mở app tự làm mới 1 lần (0 = không bao giờ, chỉ bấm ⟳).</summary>
    public int CacheMaxAgeHours { get; set; } = 12;

    /// <summary>Kích thước cửa sổ lần trước (0 = mặc định).</summary>
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

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

    private static readonly object SaveLock = new();

    public void Save()
    {
        lock (SaveLock) // auto flow chạy nền cũng lưu cấu hình
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
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
