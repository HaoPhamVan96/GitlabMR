using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GitLabMR;

public sealed class GitLabException : Exception
{
    public HttpStatusCode? Status { get; }
    public GitLabException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
}

/// <summary>Project tham chiếu từ URL người dùng dán vào.</summary>
public sealed record ProjectRef(string BaseUrl, string Host, string Path)
{
    /// <summary>
    /// Chấp nhận: https://host/group/sub/project, .../project.git, .../project/-/tree/main,
    /// git@host:group/project.git, ssh://git@host:2222/group/project.git
    /// </summary>
    public static bool TryParse(string? input, out ProjectRef? result, out string error)
    {
        result = null;
        error = "";
        input = (input ?? "").Trim();
        if (input.Length == 0) { error = "Chưa nhập URL project."; return false; }

        // git@host:group/project.git -> https://host/group/project.git
        if (input.StartsWith("git@", StringComparison.OrdinalIgnoreCase) && input.Contains(':'))
        {
            var rest = input[4..];
            var colon = rest.IndexOf(':');
            input = $"https://{rest[..colon]}/{rest[(colon + 1)..]}";
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            error = "URL không hợp lệ. Ví dụ: https://gitlab.company.com/group/project";
            return false;
        }

        var scheme = uri.Scheme;
        var authority = uri.Authority;
        if (scheme == "ssh")
        {
            scheme = "https";
            authority = uri.Host; // bỏ port SSH
        }
        else if (scheme != "http" && scheme != "https")
        {
            error = "URL phải bắt đầu bằng http:// hoặc https://";
            return false;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        var dash = path.IndexOf("/-/", StringComparison.Ordinal);
        if (dash >= 0) path = path[..dash];
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        path = path.Trim('/');

        if (path.Length == 0)
        {
            error = "URL thiếu group/project. Ví dụ: https://gitlab.company.com/my-group";
            return false;
        }
        if (path.StartsWith("groups/", StringComparison.OrdinalIgnoreCase)) path = path[7..]; // link kiểu /groups/xxx cũ

        result = new ProjectRef($"{scheme}://{authority}", authority.ToLowerInvariant(), path);
        return true;
    }
}

public sealed class GlUser
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class GlProject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string PathWithNamespace { get; set; } = "";
    public string? DefaultBranch { get; set; }
    public string WebUrl { get; set; } = "";
}

public sealed class GlGroup
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string WebUrl { get; set; } = "";
}

public sealed class GlBranch
{
    public string Name { get; set; } = "";
    public bool Protected { get; set; }
    public bool Default { get; set; }
}

public sealed class GlCommit
{
    public string Id { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string Title { get; set; } = "";
    public string AuthorName { get; set; } = "";
}

public sealed class GlCompare
{
    public List<GlCommit> Commits { get; set; } = new();
}

public sealed class GlMergeRequest
{
    public long Id { get; set; }
    public long Iid { get; set; }
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
    public string WebUrl { get; set; } = "";
    public string SourceBranch { get; set; } = "";
    public string TargetBranch { get; set; } = "";
    public string? MergeStatus { get; set; }
    public string? DetailedMergeStatus { get; set; }
    public bool HasConflicts { get; set; }
    public string? Sha { get; set; }
    public bool? MergeWhenPipelineSucceeds { get; set; }
    public string? MergeError { get; set; }
    public bool Draft { get; set; }
}

/// <summary>Client tối giản cho GitLab REST API v4, xác thực bằng Personal/Project Access Token.</summary>
public sealed class GitLabClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public GitLabClient(string baseUrl, string token, bool ignoreSsl)
    {
        var handler = new HttpClientHandler();
        if (ignoreSsl)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/v4/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitLabMR/1.0");
    }

    public void Dispose() => _http.Dispose();

    private static string Esc(string s) => Uri.EscapeDataString(s);

    public Task<GlUser> GetCurrentUserAsync(CancellationToken ct) =>
        SendAsync<GlUser>(HttpMethod.Get, "user", null, ct);

    public Task<GlProject> GetProjectAsync(string path, CancellationToken ct) =>
        SendAsync<GlProject>(HttpMethod.Get, $"projects/{Esc(path)}", null, ct);

    public Task<GlGroup> GetGroupAsync(string path, CancellationToken ct) =>
        SendAsync<GlGroup>(HttpMethod.Get, $"groups/{Esc(path)}", null, ct);

    /// <summary>Tất cả project (gồm subgroup, bỏ archived và project được share từ group khác).</summary>
    public async Task<List<GlProject>> GetGroupProjectsAsync(long groupId, CancellationToken ct)
    {
        var all = new List<GlProject>();
        for (var page = 1; page <= 100; page++)
        {
            var batch = await SendAsync<List<GlProject>>(HttpMethod.Get,
                $"groups/{groupId}/projects?include_subgroups=true&archived=false&with_shared=false&simple=true" +
                $"&order_by=path&sort=asc&per_page=100&page={page}", null, ct);
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }
        return all;
    }

    /// <summary>
    /// Fallback khi không đọc được group (chỉ là member của subgroup/project con → API groups trả 404):
    /// lấy các project mình có quyền Developer+ rồi lọc theo prefix đường dẫn.
    /// </summary>
    public async Task<List<GlProject>> GetProjectsUnderPathAsync(string namespacePath, CancellationToken ct)
    {
        var prefix = namespacePath.Trim('/') + "/";
        var lastSegment = namespacePath.Trim('/').Split('/')[^1];
        var search = lastSegment.Length >= 3 ? $"&search_namespaces=true&search={Esc(lastSegment)}" : "";
        var all = new List<GlProject>();
        for (var page = 1; page <= 100; page++)
        {
            var batch = await SendAsync<List<GlProject>>(HttpMethod.Get,
                $"projects?min_access_level=30&simple=true&archived=false&order_by=path&sort=asc{search}&per_page=100&page={page}",
                null, ct);
            all.AddRange(batch.Where(p => p.PathWithNamespace.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            if (batch.Count < 100) break;
        }
        return all;
    }

    public async Task<bool> BranchExistsAsync(long projectId, string branch, CancellationToken ct)
    {
        try
        {
            await SendAsync<GlBranch>(HttpMethod.Get, $"projects/{projectId}/repository/branches/{Esc(branch)}", null, ct);
            return true;
        }
        catch (GitLabException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<List<GlBranch>> GetBranchesAsync(long projectId, CancellationToken ct)
    {
        var all = new List<GlBranch>();
        for (var page = 1; page <= 50; page++)
        {
            var batch = await SendAsync<List<GlBranch>>(HttpMethod.Get,
                $"projects/{projectId}/repository/branches?per_page=100&page={page}", null, ct);
            all.AddRange(batch);
            if (batch.Count < 100) break;
        }
        return all;
    }

    /// <summary>Các commit có trong <paramref name="to"/> mà chưa có trong <paramref name="from"/> (tính từ merge-base).</summary>
    public Task<GlCompare> CompareAsync(long projectId, string from, string to, CancellationToken ct) =>
        SendAsync<GlCompare>(HttpMethod.Get,
            $"projects/{projectId}/repository/compare?from={Esc(from)}&to={Esc(to)}", null, ct);

    public async Task<GlMergeRequest?> FindOpenMrAsync(long projectId, string source, string target, CancellationToken ct)
    {
        var list = await SendAsync<List<GlMergeRequest>>(HttpMethod.Get,
            $"projects/{projectId}/merge_requests?state=opened&source_branch={Esc(source)}&target_branch={Esc(target)}",
            null, ct);
        return list.FirstOrDefault();
    }

    public Task<GlMergeRequest> CreateMrAsync(long projectId, string source, string target, string title,
        string description, bool removeSource, bool squash, CancellationToken ct) =>
        SendAsync<GlMergeRequest>(HttpMethod.Post, $"projects/{projectId}/merge_requests",
            new Dictionary<string, object>
            {
                ["source_branch"] = source,
                ["target_branch"] = target,
                ["title"] = title,
                ["description"] = description,
                ["remove_source_branch"] = removeSource,
                ["squash"] = squash,
            }, ct);

    public Task<GlMergeRequest> GetMrAsync(long projectId, long iid, CancellationToken ct) =>
        SendAsync<GlMergeRequest>(HttpMethod.Get, $"projects/{projectId}/merge_requests/{iid}", null, ct);

    public Task<GlMergeRequest> MergeAsync(long projectId, long iid, string? sha, bool removeSource, bool squash,
        bool whenPipelineSucceeds, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["should_remove_source_branch"] = removeSource,
            ["squash"] = squash,
        };
        if (!string.IsNullOrEmpty(sha)) body["sha"] = sha;
        if (whenPipelineSucceeds)
        {
            body["merge_when_pipeline_succeeds"] = true; // GitLab < 17
            body["auto_merge"] = true;                   // GitLab >= 17
        }
        return SendAsync<GlMergeRequest>(HttpMethod.Put, $"projects/{projectId}/merge_requests/{iid}/merge", body, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = JsonContent.Create(body);

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new GitLabException(DescribeError(res.StatusCode, text), res.StatusCode);

        try
        {
            return JsonSerializer.Deserialize<T>(text, Json)
                   ?? throw new GitLabException("GitLab trả về dữ liệu rỗng.");
        }
        catch (JsonException)
        {
            throw new GitLabException("Phản hồi không phải JSON. URL có đúng là GitLab không? " + Truncate(text, 200));
        }
    }

    private static string DescribeError(HttpStatusCode code, string body)
    {
        var detail = ExtractMessage(body);
        var hint = code switch
        {
            HttpStatusCode.Unauthorized => "Token không hợp lệ hoặc đã hết hạn.",
            HttpStatusCode.Forbidden => "Token không đủ quyền (cần scope 'api', role Developer trở lên).",
            HttpStatusCode.NotFound => "Không tìm thấy. Sai URL project/branch, hoặc token không có quyền xem project.",
            HttpStatusCode.Conflict => "Xung đột.",
            _ => $"HTTP {(int)code}.",
        };
        return string.IsNullOrWhiteSpace(detail) ? hint : $"{hint} GitLab: {detail}";
    }

    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var m)) return Flatten(m);
                if (root.TryGetProperty("error", out var e)) return Flatten(e);
            }
            return Flatten(root);
        }
        catch (JsonException)
        {
            return Truncate(body, 200);
        }
    }

    private static string Flatten(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString() ?? "";
            case JsonValueKind.Array:
                return string.Join("; ", e.EnumerateArray().Select(Flatten));
            case JsonValueKind.Object:
                var sb = new StringBuilder();
                foreach (var p in e.EnumerateObject())
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(p.Name == "base" ? Flatten(p.Value) : $"{p.Name}: {Flatten(p.Value)}");
                }
                return sb.ToString();
            default:
                return e.ToString();
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
