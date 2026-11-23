using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GitLabMR;

/// <summary>
/// Lắng nghe tin nhắn tới Zalo Bot (getUpdates, không tốn token AI):
///  - Lệnh "&lt;branch&gt; &lt;OTP&gt;": member đã fix conflict trên 1 branch riêng → app merge branch đó vào môi trường.
///    OTP 6 ký tự được gửi kèm tin báo conflict, dùng 1 lần, hết hạn sau 3 ngày.
///  - "Lấy chat ID" dùng chung luồng này (2 nơi cùng gọi getUpdates sẽ giành tin của nhau).
/// </summary>
public sealed partial class AppHost
{
    /// <summary>1 conflict đang chờ member fix tay.</summary>
    private sealed class OtpFix
    {
        public required string Otp;
        public required GlProject Project;
        public required string Source, Target, Label, Changes;
        public string? Suggest;                    // branch gợi ý (vd branch fix app đã tạo)
        public DateTime Created = DateTime.Now;
    }

    private sealed record ZaloSeen(DateTime At, string ChatId, string ChatType, string ChatName, string Text);

    private static readonly TimeSpan OtpLife = TimeSpan.FromDays(3);
    private const string OtpChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // bỏ 0/O, 1/I dễ nhầm

    /// <summary>Tạo OTP cho 1 conflict, trả về dòng hướng dẫn để gắn vào tin báo (trống nếu chưa cấu hình Zalo).</summary>
    private string OtpHint(Workspace ws, AutoState a, GlProject p, string source, string target, string label, string changes, string? suggest)
    {
        var f = ws.Auto;
        if (f?.ZaloToken == null || f.ChatList.Count == 0) return "";
        string otp;
        lock (a.Jobs)
        {
            a.Otps.RemoveAll(o => DateTime.Now - o.Created > OtpLife || (o.Project.Id == p.Id && o.Source == source && o.Target == target));
            do otp = new string(Enumerable.Range(0, 6).Select(_ => OtpChars[RandomNumberGenerator.GetInt32(OtpChars.Length)]).ToArray());
            while (a.Otps.Any(o => o.Otp == otp));
            a.Otps.Add(new OtpFix { Otp = otp, Project = p, Source = source, Target = target, Label = label, Changes = changes, Suggest = suggest });
        }
        var sug = suggest ?? f.ConflictBranch.Replace("{branch}", source).Replace("{target}", target);
        return $"\nMã fix: {otp}\nFix conflict trên 1 branch riêng (đừng merge {target} vào {source}), push xong reply bot: <branch> {otp}\nVD: {sug} {otp}";
    }

    // ---------------------------------------------------------------- luồng nghe
    private void StartZaloListener(Workspace ws, AutoState a, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var f = ws.Auto;
                var token = f?.ZaloToken == null ? null : Platform.UnprotectToken(ws.ZaloKey, f.ZaloToken);
                if (string.IsNullOrEmpty(token)) { a.Listening = false; try { await Task.Delay(60_000, ct); } catch { break; } continue; }
                a.Listening = true;
                try
                {
                    foreach (var u in await Zalo.GetUpdatesAsync(token, ct))
                    {
                        lock (a.Seen) { a.Seen.Add(new ZaloSeen(DateTime.Now, u.ChatId, u.ChatType, u.ChatName, u.Text)); if (a.Seen.Count > 50) a.Seen.RemoveAt(0); }
                        if (f!.ChatList.Any(c => c.Id == u.ChatId)) _ = HandleZaloAsync(ws, a, token, u, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Once(a, "zl|" + Describe(ex), $"[Auto {ws.Name}] Zalo: không đọc được tin nhắn – {Describe(ex)}", LogKind.Warn);
                    try { await Task.Delay(15_000, ct); } catch { break; }
                }
            }
            a.Listening = false;
        });
    }

    private async Task HandleZaloAsync(Workspace ws, AutoState a, string token, Zalo.Update u, CancellationToken ct)
    {
        var tag = $"[Auto {ws.Name}]";
        Task Reply(string text) => Zalo.SendAsync(token, u.ChatId, text, ct);
        OtpFix? o = null; string? branch = null;
        lock (a.Jobs)
        {
            a.Otps.RemoveAll(x => DateTime.Now - x.Created > OtpLife);
            // Reply tin bot hoặc @tag bot đều được; mã và branch đứng trước / sau đều được
            var words = u.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('`', '"', '\'', '<', '>', ',', ';')).Where(w => w.Length > 0).ToList();
            for (var i = 0; i < words.Count && o == null; i++)
            {
                var hit = a.Otps.FirstOrDefault(x => x.Otp.Equals(words[i], StringComparison.OrdinalIgnoreCase));
                if (hit == null) continue;
                var rest = words.Where((w, j) => j != i && !w.StartsWith('@')).ToList();
                branch = rest.FirstOrDefault(w => w.Contains('/'))                 // branch thường có dấu /
                         ?? (i > 0 && !words[i - 1].StartsWith('@') ? words[i - 1] : null)
                         ?? (i + 1 < words.Count ? words[i + 1] : null);
                if (branch != null) o = hit;
            }
            if (o == null)
            {
                // Có mã OTP nhưng thiếu branch, hoặc mã sai / hết hạn
                var codes = Regex.Matches(u.Text, @"\b[A-Za-z0-9]{6}\b").Select(m => m.Value).ToList();
                var only = codes.Select(cd => a.Otps.FirstOrDefault(x => x.Otp.Equals(cd, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(x => x != null);
                if (only != null) { _ = Reply($"Thiếu branch. Cú pháp: <branch> {only.Otp}"); return; }
                // Tag / reply bot mà sai cú pháp: bot đanh đá đáp lại người đó (bật / tắt trong popup Zalo), mỗi nơi tối đa 1 câu / 2 phút
                if (ws.Auto?.SassyBot == true && (!Sass.TryGetValue(u.ChatId, out var last) || DateTime.Now - last > SassGap))
                {
                    Sass[u.ChatId] = DateTime.Now;
                    var looksLikeCode = codes.Any(cd => cd.Any(char.IsDigit) && cd.Any(char.IsLetter));
                    _ = Reply(SassLine(u.ChatId, u.FromName, looksLikeCode, a.Otps.Count > 0));
                }
                return;
            }
        }
        var who = u.FromName.Length > 0 ? u.FromName : "member";
        var key = "otp|" + o.Otp;
        lock (a.Fixing) if (!a.Fixing.Add(key)) { _ = Reply($"Mã {o.Otp} đang được xử lý, chờ chút."); return; }
        try
        {
            if (ws.Auto?.DryRun == true) { await Reply("Auto đang ở chế độ Chạy thử: không merge."); return; }
            if (branch!.Equals(o.Target, StringComparison.OrdinalIgnoreCase))
            { await Reply($"Branch phải là branch đã fix, không phải {o.Target}."); return; }
            if (branch.Equals(o.Source, StringComparison.OrdinalIgnoreCase))
            { await Reply($"Không merge thẳng {o.Source}: branch feature phải giữ sạch. Tạo branch riêng (vd {o.Source}_fix_conflict từ {o.Target}, merge {o.Source} vào, fix) rồi reply lại."); return; }

            var g = AutoGitLab(ws, a) ?? throw new GitLabException("Auto chưa chọn workspace GitLab.");
            var gl = g.Gl; var p = o.Project;
            Log($"{tag} {who} gửi {branch} {o.Otp} → merge vào {o.Target} ({p.PathWithNamespace})", LogKind.Info);
            if (!await gl.BranchExistsAsync(p.Id, branch, ct)) { await Reply($"Không thấy branch {branch} trên {p.PathWithNamespace}. Push lên GitLab rồi reply lại."); return; }
            var markers = await gl.ConflictMarkersAsync(p.Id, o.Target, branch, ct);
            if (markers.Count > 0) { await Reply($"{branch} còn dấu conflict (<<<<<<< / >>>>>>>) ở: {string.Join(", ", markers.Take(5))}. Sửa xong reply lại."); return; }

            await Reply($"Đã nhận {branch} {o.Otp} từ {who} · đang merge vào {o.Target}…");
            var opts = new MrOptions(branch, o.Target, $"merge branch {o.Source} to {o.Target}",
                $"Fix conflict {o.Source} → {o.Target} (Gerrit {o.Changes}) bởi {who}, merge qua Zalo Bot.", true, _cfg.AutoMerge, false, false);
            var res = await MrWorkflow.RunAsync(gl, p, opts, (msg, k) => Log($"{tag} [{p.PathWithNamespace}] {msg}", k), ct);
            var label = $"{p.PathWithNamespace} {branch} → {o.Target}";
            if (res.Outcome is MrOutcome.Merged or MrOutcome.AutoMerge)
            {
                lock (a.Jobs) a.Otps.Remove(o);
                if (ws.Auto!.NotifyPipeline && MrIid(res.Url) is { } iid)
                {
                    a.NewPipes.Enqueue(new PipeWatch { Project = p, Iid = iid, Label = label, MrUrl = res.Url!, Changes = o.Changes });
                    WakeGitLab(a);
                }
                if (res.Outcome == MrOutcome.Merged)
                    await NotifyAsync(ws, "Đã merge branch fix conflict", $"{label}\nGerrit {o.Changes} · fix bởi {who}", res.Url);
                else await NotifyAsync(ws, "Branch fix: pipeline đang chạy, chưa merge", $"{label}\nSẽ tự merge khi pipeline pass.", res.Url, pending: true);
            }
            else
                await AlertAsync(ws, "Branch fix chưa merge được", $"{label}\n{res.Message}\nSửa xong reply lại: {branch} {o.Otp}", res.Url);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { try { await Reply($"Lỗi khi merge {branch}: {Describe(ex)}"); } catch { } }
        finally { lock (a.Fixing) a.Fixing.Remove(key); }
    }

    // ---------------------------------------------------------------- bot đanh đá (cho vui)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Sass = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> SassLast = new();
    private static readonly TimeSpan SassGap = TimeSpan.FromMinutes(2);

    private static readonly string[] SassWrongCode =
    {
        "Mã gì đây {name}? Bổn bot không nhận hàng giả nha 😤",
        "{name} gõ mã kiểu gì vậy? Sai, hết hạn, hoặc dùng rồi. Đọc lại tin báo giùm cái 🙄",
        "Mã này bổn bot không quen. {name} bịa ra đó hả? 😒",
        "Sai mã rồi {name}. Không phải bổn bot khó tính, là {name} gõ ẩu thôi 💅",
        "Mã đó hết đát rồi {name} ơi, như lời hứa fix bug của ai đó vậy 🫠",
    };
    private static readonly string[] SassNoConflict =
    {
        "Có conflict nào đâu mà {name} gọi? Rảnh quá thì đi viết unit test đi 🙄",
        "{name} tag bổn bot làm gì? Đang yên đang lành, không có gì để merge cả 💅",
        "Không có conflict, không có việc, không có chuyện để nói với {name} 😌",
        "Gọi hoài vậy {name}? Conflict còn chưa có mà đã đòi merge, mơ đi 😏",
    };
    private static readonly string[] SassGeneric =
    {
        "Tag bổn bot mà không kèm mã fix, tưởng bổn bot rảnh lắm à {name}? 🙄 Cú pháp: <branch> <mã>",
        "{name} ơi, cú pháp là <branch> <mã>. Nói mãi không thuộc hả? 😤",
        "Bổn bot chỉ merge, không tám chuyện. Biết điều thì gửi <branch> <mã> nha {name} 💅",
        "Đọc lại tin báo conflict giùm cái {name}, mã nằm sờ sờ ra đó 😒",
        "Lại là {name}... Lần thứ mấy rồi? Gửi đúng <branch> <mã> giùm 🫠",
        "Nói chuyện với bổn bot thì phải có mã. Không mã, không merge, không tiếp {name} 😌",
    };

    /// <summary>Câu đanh đá ngẫu nhiên (không lặp lại câu vừa nói ở cùng nhóm), mở đầu bằng tên người tag.</summary>
    private static string SassLine(string chatId, string name, bool wrongCode, bool hasPending)
    {
        var pool = wrongCode && hasPending ? SassWrongCode : !hasPending ? SassNoConflict : SassGeneric;
        var prev = SassLast.GetValueOrDefault(chatId);
        var pick = pool.Where(x => x != prev).ElementAt(RandomNumberGenerator.GetInt32(pool.Count(x => x != prev)));
        SassLast[chatId] = pick;
        var who = string.IsNullOrWhiteSpace(name) ? "bạn" : name.Trim();
        return $"↩ {who}: " + pick.Replace("{name}", who);
    }

    /// <summary>"Lấy chat ID" khi luồng nghe đang chạy: chờ tin mới mà luồng nghe thấy.</summary>
    private async Task<ZaloSeen?> WaitSeenAsync(AutoState a, TimeSpan wait)
    {
        var since = DateTime.Now;
        for (var end = since + wait; DateTime.Now < end; await Task.Delay(500))
            lock (a.Seen) { var s = a.Seen.LastOrDefault(x => x.At >= since); if (s != null) return s; }
        return null;
    }
}
