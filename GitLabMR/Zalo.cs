using System.Net.Http.Json;
using System.Text.Json;

namespace GitLabMR;

/// <summary>
/// Zalo Bot Platform (https://bot.zaloplatforms.com/docs): gửi tin bằng bot token.
/// Lấy chat ID: người nhận nhắn 1 tin cho bot (hoặc trong nhóm có bot) rồi gọi getUpdates.
/// </summary>
public static class Zalo
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private const int MaxText = 2000;

    public sealed record Chat(string Id, string Type, string Name, string Text);

    private static async Task<JsonElement> CallAsync(string token, string method, object body, CancellationToken ct)
    {
        token = token.Trim();
        if (token.Length == 0 || token.Contains('/')) throw new GitLabException("Bot token Zalo không hợp lệ.");
        using var res = await Http.PostAsJsonAsync($"https://bot-api.zaloplatforms.com/bot{token}/{method}", body, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement; }
        catch (JsonException) { throw new GitLabException($"Zalo trả về HTTP {(int)res.StatusCode}, không phải JSON."); }
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return root.TryGetProperty("result", out var r) ? r : default;
        var desc = root.TryGetProperty("description", out var d) ? d.ToString() : $"HTTP {(int)res.StatusCode}";
        var code = root.TryGetProperty("error_code", out var c) ? $" (mã {c})" : "";
        var hint = (int)res.StatusCode is 401 or 404 ? " – kiểm tra lại bot token." : "";
        throw new GitLabException($"Zalo: {desc}{code}{hint}");
    }

    public static Task SendAsync(string token, string chatId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chatId)) throw new GitLabException("Chưa có chat ID Zalo – bấm \"Lấy chat ID\".");
        if (text.Length > MaxText) text = text[..(MaxText - 1)] + "…";
        return CallAsync(token, "sendMessage", new { chat_id = chatId.Trim(), text }, ct);
    }

    /// <summary>1 tin nhắn tới bot.</summary>
    public sealed record Update(string ChatId, string ChatType, string ChatName, string FromName, string Text);

    /// <summary>Tin nhắn mới tới bot (cũ trước). Chờ tối đa ~20 giây; trống = không có tin nào. Mỗi tin chỉ đọc được 1 lần.</summary>
    public static async Task<List<Update>> GetUpdatesAsync(string token, CancellationToken ct)
    {
        JsonElement result;
        try { result = await CallAsync(token, "getUpdates", new { timeout = "20" }, ct); }
        catch (GitLabException ex) when (ex.Message.Contains("mã 408")) { return new(); } // hết thời gian chờ mà chưa có tin nào
        var updates = result.ValueKind == JsonValueKind.Array ? result.EnumerateArray().ToList()
            : result.ValueKind == JsonValueKind.Object ? new List<JsonElement> { result } : new();
        var list = new List<Update>();
        foreach (var u in updates)
        {
            if (!u.TryGetProperty("message", out var m) || !m.TryGetProperty("chat", out var chat)) continue;
            var id = chat.TryGetProperty("id", out var i) ? i.ToString() : "";
            if (id.Length == 0) continue;
            var type = chat.TryGetProperty("chat_type", out var t) ? t.ToString() : "";
            var sender = m.TryGetProperty("from", out var f) && f.TryGetProperty("display_name", out var n) ? n.ToString() : "";
            // Tên nhóm nếu Zalo có gửi kèm, không thì lấy tên người nhắn
            var title = new[] { "display_name", "title", "name", "group_name" }
                .Select(k => chat.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var name = title ?? (type.Equals("GROUP", StringComparison.OrdinalIgnoreCase) ? (sender.Length > 0 ? $"Nhóm ({sender} nhắn)" : "Nhóm") : sender);
            var msg = m.TryGetProperty("text", out var tx) ? tx.ToString() : "";
            list.Add(new Update(id, type, name, sender, msg));
        }
        return list;
    }

    /// <summary>Các cuộc trò chuyện vừa nhắn cho bot (mới nhất trước). Trống = chưa ai nhắn trong lúc chờ.</summary>
    public static async Task<List<Chat>> GetRecentChatsAsync(string token, CancellationToken ct)
    {
        var chats = new List<Chat>();
        foreach (var u in (await GetUpdatesAsync(token, ct)).AsEnumerable().Reverse())
            if (!chats.Any(x => x.Id == u.ChatId)) chats.Add(new Chat(u.ChatId, u.ChatType, u.ChatName, u.Text));
        return chats;
    }
}
