using System.Net.Http.Json;

namespace GitLabMR;

/// <summary>
/// Gửi thông báo vào kênh Teams qua webhook. Dùng Adaptive Card: chạy được với cả
/// Workflows ("Post to a channel when a webhook request is received") lẫn Incoming Webhook kiểu cũ.
/// </summary>
public static class Teams
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task SendAsync(string webhook, string title, string text, string? link, bool error, CancellationToken ct)
    {
        var body = new List<object>
        {
            new { type = "TextBlock", text = title, weight = "Bolder", size = "Medium", wrap = true, color = error ? "Attention" : "Good" },
        };
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            body.Add(new { type = "TextBlock", text = line, wrap = true, spacing = "Small" });

        var card = new Dictionary<string, object>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };
        if (!string.IsNullOrEmpty(link))
            card["actions"] = new object[] { new { type = "Action.OpenUrl", title = "Mở", url = link } };

        var payload = new
        {
            type = "message",
            attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive", content = card } },
        };
        using var res = await Http.PostAsJsonAsync(webhook, payload, ct);
        if (!res.IsSuccessStatusCode)
        {
            var detail = (await res.Content.ReadAsStringAsync(ct)).Trim();
            if (detail.Length > 160) detail = detail[..160] + "…";
            throw new GitLabException($"Teams webhook trả HTTP {(int)res.StatusCode}. {detail}");
        }
    }
}
