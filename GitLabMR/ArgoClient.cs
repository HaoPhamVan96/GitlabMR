using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GitLabMR;

/// <summary>Pod của 1 app Argo CD (lấy từ resource-tree).</summary>
public sealed record ArgoPod(string Name, string Namespace, string Uid, DateTime? Created, string Reason,
    int Ready, int Total, int Restarts, string Health, string HealthMsg, List<string> Images)
{
    public bool AllReady => Total > 0 && Ready == Total && Reason is "Running" or "Completed";
    /// <summary>Lỗi hẳn, không cần chờ thêm (CrashLoopBackOff, ImagePullBackOff, sai config…).</summary>
    public bool Broken =>
        Reason.Contains("BackOff", StringComparison.OrdinalIgnoreCase) || Reason.Contains("Err", StringComparison.OrdinalIgnoreCase)
        || Reason.Contains("Error", StringComparison.OrdinalIgnoreCase) || Reason is "OOMKilled" or "InvalidImageName" or "Evicted"
        || Restarts >= 3;
}

public sealed record ArgoApp(string Name, string Health, string HealthMsg, string Sync, string Revision, List<string> Images,
    string? OpPhase, string? OpMessage);

/// <summary>
/// Client chỉ đọc cho Argo CD REST API. Đăng nhập bằng account (POST /api/v1/session) hoặc token có sẵn.
/// Cần quyền applications/get (xem app, pod, event) và logs/get (xem log pod).
/// </summary>
public sealed class ArgoClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _user;
    private readonly string _secret;
    private string? _token;
    public string BaseUrl { get; }

    public ArgoClient(string url, string? user, string secret, bool ignoreSsl)
    {
        BaseUrl = NormalizeUrl(url);
        var handler = new HttpClientHandler();
        if (ignoreSsl) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(40) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitLabMR/1.0");
        _user = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        _secret = secret;
        if (_user == null) _token = secret.Trim(); // không có username: ô mật khẩu là token
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Dán cả link trang (…/applications?showFavorites=…) cũng được: chỉ giữ scheme + host (+ base path nếu Argo chạy dưới /argocd).</summary>
    public static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new GitLabException("URL Argo CD không hợp lệ (vd https://argocd.xxx.vn).");
        var path = u.AbsolutePath;
        foreach (var cut in new[] { "/applications", "/settings", "/user-info", "/login", "/api/" })
        {
            var i = path.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) { path = path[..i]; break; }
        }
        return $"{u.Scheme}://{u.Authority}{path.TrimEnd('/')}";
    }

    public string AppUrl(string app) => $"{BaseUrl}/applications/{Uri.EscapeDataString(app)}";

    private async Task LoginAsync(CancellationToken ct)
    {
        // Gửi kèm Content-Length (không chunked) cho chắc khi đi qua proxy / ingress
        using var body0 = new StringContent(JsonSerializer.Serialize(new { username = _user, password = _secret }), Encoding.UTF8);
        body0.Headers.ContentType = new MediaTypeHeaderValue("application/json"); // không kèm charset
        using var res = await _http.PostAsync("api/v1/session", body0, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new GitLabException("Argo: sai username / mật khẩu (account SSO thì nhờ admin tạo account local hoặc dùng token).");
        if (!res.IsSuccessStatusCode) throw new GitLabException("Argo đăng nhập lỗi: " + Err(res.StatusCode, body));
        _token = JsonDocument.Parse(body).RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(_token)) throw new GitLabException("Argo không trả token khi đăng nhập.");
    }

    private async Task<string> GetRawAsync(string path, CancellationToken ct, bool retry = true)
    {
        if (_token == null) await LoginAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized && retry && _user != null)
        {
            _token = null; // token hết hạn: đăng nhập lại 1 lần
            return await GetRawAsync(path, ct, false);
        }
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new GitLabException("Argo: token không hợp lệ / hết hạn.");
        if (res.StatusCode == HttpStatusCode.Forbidden) throw new GitLabException("Argo: account không có quyền – " + Err(res.StatusCode, body));
        if (res.StatusCode == HttpStatusCode.NotFound) throw new GitLabException("Argo: không thấy – " + Err(res.StatusCode, body));
        if (!res.IsSuccessStatusCode) throw new GitLabException("Argo: " + Err(res.StatusCode, body));
        return body;
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct) =>
        JsonDocument.Parse(await GetRawAsync(path, ct)).RootElement;

    private static string Err(HttpStatusCode code, string body)
    {
        try
        {
            var r = JsonDocument.Parse(body).RootElement;
            if (r.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } s) return $"{s} (HTTP {(int)code})";
            if (r.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } s2) return $"{s2} (HTTP {(int)code})";
        }
        catch (JsonException) { }
        return $"HTTP {(int)code}" + (body.Length is > 0 and < 200 && !body.TrimStart().StartsWith('<') ? ": " + body.Trim() : "");
    }

    private static string S(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return "";
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : e.ToString();
    }

    private static List<string> Strings(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new();

    /// <summary>Tên + health / sync của mọi app account xem được.</summary>
    public async Task<List<(string Name, string Health, string Sync)>> ListAppsAsync(CancellationToken ct)
    {
        var r = await GetAsync("api/v1/applications?fields=items.metadata.name,items.status.health.status,items.status.sync.status", ct);
        var list = new List<(string, string, string)>();
        if (r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
                list.Add((S(it, "metadata", "name"), S(it, "status", "health", "status"), S(it, "status", "sync", "status")));
        return list.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>refresh: bảo Argo so lại với git ngay (không chờ chu kỳ 3 phút). Chỉ cần quyền get.</summary>
    public async Task<ArgoApp> GetAppAsync(string app, bool refresh, CancellationToken ct)
    {
        var r = await GetAsync($"api/v1/applications/{Uri.EscapeDataString(app)}" + (refresh ? "?refresh=normal" : ""), ct);
        var st = r.TryGetProperty("status", out var s) ? s : default;
        var images = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("summary", out var sum) && sum.TryGetProperty("images", out var im)
            ? Strings(im) : new();
        return new ArgoApp(app, S(st, "health", "status"), S(st, "health", "message"), S(st, "sync", "status"), S(st, "sync", "revision"),
            images, S(st, "operationState", "phase") is { Length: > 0 } ph ? ph : null, S(st, "operationState", "message") is { Length: > 0 } om ? om : null);
    }

    public async Task<List<ArgoPod>> GetPodsAsync(string app, CancellationToken ct)
    {
        var r = await GetAsync($"api/v1/applications/{Uri.EscapeDataString(app)}/resource-tree", ct);
        var pods = new List<ArgoPod>();
        if (!r.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return pods;
        foreach (var n in nodes.EnumerateArray())
        {
            if (S(n, "kind") != "Pod") continue;
            string reason = "", containers = "";
            var restarts = 0;
            if (n.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
                foreach (var i in info.EnumerateArray())
                    switch (S(i, "name"))
                    {
                        case "Status Reason": reason = S(i, "value"); break;
                        case "Containers": containers = S(i, "value"); break;
                        case "Restart Count": int.TryParse(S(i, "value"), out restarts); break;
                    }
            var parts = containers.Split('/');
            int.TryParse(parts.ElementAtOrDefault(0), out var ready);
            int.TryParse(parts.ElementAtOrDefault(1), out var total);
            DateTime? created = DateTime.TryParse(S(n, "createdAt"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var c) ? c : null;
            pods.Add(new ArgoPod(S(n, "name"), S(n, "namespace"), S(n, "uid"), created, reason, ready, total, restarts,
                S(n, "health", "status"), S(n, "health", "message"), n.TryGetProperty("images", out var im) ? Strings(im) : new()));
        }
        return pods;
    }

    /// <summary>Event cảnh báo gần nhất của pod (vd Back-off restarting failed container, Readiness probe failed).</summary>
    public async Task<List<string>> GetPodEventsAsync(string app, ArgoPod pod, CancellationToken ct)
    {
        var r = await GetAsync($"api/v1/applications/{Uri.EscapeDataString(app)}/events?resourceNamespace={Uri.EscapeDataString(pod.Namespace)}" +
                               $"&resourceName={Uri.EscapeDataString(pod.Name)}&resourceUID={Uri.EscapeDataString(pod.Uid)}", ct);
        var list = new List<(DateTime, string)>();
        if (r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var e in items.EnumerateArray())
            {
                if (S(e, "type") != "Warning") continue;
                DateTime.TryParse(S(e, "lastTimestamp") is { Length: > 0 } lt ? lt : S(e, "eventTime"), out var t);
                var cnt = S(e, "count");
                list.Add((t, $"{S(e, "reason")}: {S(e, "message")}" + (cnt is { Length: > 0 } && cnt != "1" ? $" (x{cnt})" : "")));
            }
        return list.OrderByDescending(x => x.Item1).Select(x => x.Item2).Distinct().Take(3).ToList();
    }

    /// <summary>Log cuối của pod. previous: log của lần container chết trước (pod CrashLoopBackOff).</summary>
    public async Task<string> GetPodLogsAsync(string app, ArgoPod pod, bool previous, int tail, CancellationToken ct)
    {
        var path = $"api/v1/applications/{Uri.EscapeDataString(app)}/logs?namespace={Uri.EscapeDataString(pod.Namespace)}" +
                   $"&podName={Uri.EscapeDataString(pod.Name)}&tailLines={tail}&follow=false" + (previous ? "&previous=true" : "");
        string raw;
        try { raw = await GetRawAsync(path, ct); }
        catch (GitLabException) when (previous) { raw = await GetRawAsync(path.Replace("&previous=true", ""), ct); } // chưa có lần chết trước
        // Kết quả là nhiều dòng JSON: {"result":{"content":"...", ...}}
        var sb = new StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                var r = JsonDocument.Parse(line).RootElement;
                var content = S(r, "result", "content");
                if (S(r, "result", "last") == "true" && content.Length == 0) continue;
                sb.AppendLine(content);
            }
            catch (JsonException) { sb.AppendLine(line); }
        }
        return sb.ToString().TrimEnd();
    }
}
