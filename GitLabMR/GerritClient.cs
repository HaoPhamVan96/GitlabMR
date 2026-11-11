using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLabMR;

/// <summary>Những thao tác branch dùng chung cho GitLab và Gerrit (tạo branch hàng loạt).</summary>
public interface IBranchApi : IDisposable
{
    Task<bool> BranchExistsAsync(GlProject p, string branch, CancellationToken ct);
    Task<GlBranch> CreateBranchAsync(GlProject p, string name, string fromRef, CancellationToken ct);
    Task<List<GlBranch>> GetBranchesAsync(GlProject p, CancellationToken ct);
    string BranchUrl(GlProject p, string branch);
}

/// <summary>
/// Link Gerrit người dùng dán vào. Chấp nhận:
///   https://review.company.com/peoplex/          -> mọi project bắt đầu bằng "peoplex/"
///   https://review.company.com/peoplex/api/core-pr  (hoặc link clone /a/..., /admin/repos/..., ssh://...:29418/...)
/// </summary>
public sealed record GerritRef(string BaseUrl, string Host, string Name, bool IsPrefix, string? User)
{
    public static bool TryParse(string? input, out GerritRef? result, out string error)
    {
        result = null;
        error = "";
        input = (input ?? "").Trim();
        if (input.Length == 0) { error = "Chưa nhập link Gerrit."; return false; }
        var isPrefix = input.EndsWith('/') || input.EndsWith('*');

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            error = "Link không hợp lệ. Ví dụ: https://review.company.com/peoplex/";
            return false;
        }

        var scheme = uri.Scheme == "ssh" ? "https" : uri.Scheme;
        if (scheme != "http" && scheme != "https") { error = "Link phải bắt đầu bằng https://"; return false; }
        var authority = uri.Scheme == "ssh" ? uri.Host : uri.Authority;
        var user = string.IsNullOrEmpty(uri.UserInfo) ? null : Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);

        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        foreach (var prefix in new[] { "a/", "admin/repos/", "plugins/gitiles/" })
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) path = path[prefix.Length..];
        var comma = path.IndexOf(',');                  // /admin/repos/x,branches
        if (comma >= 0) path = path[..comma];
        var gitiles = path.IndexOf("/+", StringComparison.Ordinal); // gitiles /+/refs/...
        if (gitiles >= 0) path = path[..gitiles];
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        path = path.Trim('/', '*');

        result = new GerritRef($"{scheme}://{authority}", authority.ToLowerInvariant(), path, isPrefix || path.Length == 0, user);
        return true;
    }
}

/// <summary>Client tối giản cho Gerrit REST API (đăng nhập username + HTTP password).</summary>
public sealed class GerritClient : IBranchApi
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public GerritClient(string baseUrl, string username, string httpPassword, bool ignoreSsl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        var handler = new HttpClientHandler();
        if (ignoreSsl) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(handler) { BaseAddress = new Uri(_baseUrl + "/a/"), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{httpPassword}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitLabMR/1.0");
    }

    public void Dispose() => _http.Dispose();

    private static string Esc(string s) => Uri.EscapeDataString(s);
    private static string ShortRef(string r) => r.StartsWith("refs/heads/") ? r[11..] : r;

    public sealed class Account
    {
        public string? Username { get; set; }
        public string? Name { get; set; }
    }

    public sealed class ProjectInfo
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? State { get; set; }
    }

    public sealed class BranchInfo
    {
        public string Ref { get; set; } = "";
        public string? Revision { get; set; }
    }

    public async Task<string> GetSelfAsync(CancellationToken ct)
    {
        var a = await SendAsync<Account>(HttpMethod.Get, "accounts/self", null, ct);
        return a.Username ?? a.Name ?? "?";
    }

    /// <summary>Project đúng tên; null nếu không có.</summary>
    public async Task<string?> GetProjectAsync(string name, CancellationToken ct)
    {
        if (name.Length == 0) return null;
        try
        {
            var p = await SendAsync<ProjectInfo>(HttpMethod.Get, $"projects/{Esc(name)}", null, ct);
            return Uri.UnescapeDataString(p.Name ?? p.Id ?? name);
        }
        catch (GitLabException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Mọi project ACTIVE có tên bắt đầu bằng <paramref name="prefix"/> (rỗng = tất cả).</summary>
    public async Task<List<string>> ListProjectsAsync(string prefix, CancellationToken ct)
    {
        var names = new List<string>();
        for (var skip = 0; skip < 20000; skip += 500)
        {
            var q = prefix.Length > 0 ? $"&p={Esc(prefix)}" : "";
            var page = await SendAsync<Dictionary<string, ProjectInfo>>(HttpMethod.Get, $"projects/?n=500&S={skip}{q}", null, ct);
            names.AddRange(page.Where(kv => kv.Value.State is null or "ACTIVE").Select(kv => kv.Key));
            if (page.Count < 500) break;
        }
        return names.Where(n => n != "All-Projects" && n != "All-Users").ToList();
    }

    public async Task<bool> BranchExistsAsync(GlProject p, string branch, CancellationToken ct)
    {
        try
        {
            await SendAsync<BranchInfo>(HttpMethod.Get, $"projects/{Esc(p.PathWithNamespace)}/branches/{Esc(branch)}", null, ct);
            return true;
        }
        catch (GitLabException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<GlBranch> CreateBranchAsync(GlProject p, string name, string fromRef, CancellationToken ct)
    {
        var b = await SendAsync<BranchInfo>(HttpMethod.Put, $"projects/{Esc(p.PathWithNamespace)}/branches/{Esc(name)}",
            new Dictionary<string, string> { ["revision"] = fromRef }, ct);
        return new GlBranch { Name = ShortRef(b.Ref), WebUrl = BranchUrl(p, name) };
    }

    public async Task<List<GlBranch>> GetBranchesAsync(GlProject p, CancellationToken ct)
    {
        var list = await SendAsync<List<BranchInfo>>(HttpMethod.Get, $"projects/{Esc(p.PathWithNamespace)}/branches/?n=2000", null, ct);
        return list.Where(b => b.Ref.StartsWith("refs/heads/")).Select(b => new GlBranch { Name = ShortRef(b.Ref) }).ToList();
    }

    public string BranchUrl(GlProject p, string branch) =>
        $"{_baseUrl}/admin/repos/{Esc(p.PathWithNamespace)},branches/q/filter:{Esc(branch)}";

    public string ProjectUrl(string name) => $"{_baseUrl}/admin/repos/{Esc(name)}";

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = JsonContent.Create(body);
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var detail = text.Trim();
            if (detail.Length > 200) detail = detail[..200] + "…";
            var hint = res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Sai username hoặc HTTP password (Gerrit → Settings → HTTP Credentials).",
                HttpStatusCode.Forbidden => "Không có quyền (cần quyền Create Reference trên project).",
                HttpStatusCode.NotFound => "Không tìm thấy (sai tên project/branch hoặc không có quyền xem).",
                HttpStatusCode.Conflict => "Branch đã tồn tại.",
                _ => $"HTTP {(int)res.StatusCode}.",
            };
            throw new GitLabException(detail.Length > 0 ? $"{hint} Gerrit: {detail}" : hint, res.StatusCode);
        }
        // Gerrit chèn ")]}'" ở đầu mọi JSON để chống XSSI
        if (text.StartsWith(")]}'")) text = text[4..];
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json) ?? throw new GitLabException("Gerrit trả về dữ liệu rỗng.");
        }
        catch (JsonException)
        {
            throw new GitLabException("Phản hồi không phải JSON. Link có đúng là Gerrit không?");
        }
    }
}
