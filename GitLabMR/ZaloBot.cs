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
                // Tag / reply bot mà sai cú pháp: bot đanh đá đáp lại người đó (bật / tắt trong popup Zalo), tag lần nào đáp lần đó (mỗi người chờ 5 giây giữa 2 câu, chống gửi dồn)
                if (ws.Auto?.SassyBot == true && (!Sass.TryGetValue(u.ChatId + "|" + u.FromName, out var last) || DateTime.Now - last > SassGap))
                {
                    Sass[u.ChatId + "|" + u.FromName] = DateTime.Now;
                    var looksLikeCode = codes.Any(cd => cd.Any(char.IsDigit) && cd.Any(char.IsLetter));
                    var pending = a.Otps.Count > 0;
                    _ = Task.Run(async () =>
                    {
                        var botName = await Zalo.BotNameAsync(token, ct);
                        await Reply(SassLine(u.ChatId, u.FromName, u.Text, botName, looksLikeCode, pending));
                    });
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

    // ---------------------------------------------------------------- bot đanh đá (cho vui): cô nàng xưng "chị", đáp theo nội dung tin nhắn
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Sass = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> SassRecent = new();   // câu vừa nói ở mỗi nhóm
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<DateTime>> SassPokes = new(); // lần tag của mỗi người
    private static readonly TimeSpan SassGap = TimeSpan.FromSeconds(5);
    private const int SassNoRepeat = 8;

    private static readonly string[] SassWrongCode =
    {
        "Mã gì đây {name}? Chị không nhận hàng fake nha 😤",
        "Mã đó sai, hết hạn hoặc xài rồi. {name} đọc kỹ tin báo giùm chị cái 🙄",
        "Mã này chị chưa thấy bao giờ. {name} tự chế hả? 😒",
        "Sai mã rồi cưng. Không phải chị khó, là {name} gõ ẩu 💅",
        "Mã đó hết đát rồi {name}, y như lời hứa \"mai em fix\" vậy đó 🫠",
        "Gõ lại cho đàng hoàng đi {name}, 6 ký tự thôi mà cũng sai 😮‍💨",
        "{name} copy mã từ đâu vậy? Tin báo của chị nằm sờ sờ trên kia kìa 👆",
    };
    private static readonly string[] SassNoConflict =
    {
        "Có conflict nào đâu mà {name} gọi chị? Rảnh thì đi viết unit test đi 🙄",
        "Đang yên đang lành, không có gì để merge cả {name} 💅",
        "Không conflict, không việc, không chuyện để nói với {name} 😌",
        "Conflict còn chưa có mà {name} đã đòi merge, mơ đi cưng 😏",
        "Hôm nay testing2 hiền khô, {name} đừng có kiếm chuyện với chị 🙂",
        "Không có gì cần fix hết. {name} nhớ chị thì nói thẳng, đừng lấy cớ 😏",
    };
    private static readonly string[] SassSyntax =
    {
        "Tag chị mà không kèm mã fix, tưởng chị rảnh lắm hả {name}? 🙄 Cú pháp: <branch> <mã>",
        "{name} ơi, cú pháp là <branch> <mã>. Nói hoài không thuộc hả cưng? 😤",
        "Chị chỉ merge, không tám chuyện. Biết điều thì gửi <branch> <mã> nha {name} 💅",
        "Đọc lại tin báo conflict giùm chị cái {name}, mã nằm sờ sờ ra đó 😒",
        "Không mã, không merge, không tiếp khách. Gửi <branch> <mã> đi {name} 😌",
        "Chị nói lần cuối nè {name}: <branch> <mã>. Vậy thôi, khó lắm hả? 💁‍♀️",
    };
    private static readonly string[] SassGreeting =
    {
        "Chào chào cái gì, chị đang bận canh pipeline cho cả đám, có việc thì nói lẹ {name} 💅",
        "Hello {name}. Chào xong rồi đó, giờ đi làm việc đi 😌",
        "Ủa {name} chào chị chi vậy? Chị không nhận lời chào suông đâu 🙄",
        "Hi cưng. Chị nhớ mặt {name} rồi, đừng có tag linh tinh nha 😏",
        "Chào {name}. Hôm nay code có ngoan không hay lại conflict tùm lum? 🫠",
        "Alo alo cái gì {name}, chị đâu phải tổng đài 📞",
    };
    private static readonly string[] SassThanks =
    {
        "Cảm ơn thì để trong tim, lần sau đừng để conflict là được rồi {name} 💅",
        "Biết điều đó {name}. Chị ghi nhận, nhưng không giảm giá đâu 😌",
        "Ừ, không có chị thì testing2 nát lâu rồi. Nhớ công chị đó {name} 💁‍♀️",
        "Cảm ơn suông vậy thôi hả {name}? Trà sữa của chị đâu? 🧋",
        "Thôi khỏi khách sáo, chị làm vì testing2 chứ không phải vì {name} đâu 😤",
        "Nói cảm ơn mà không kèm ly cà phê là chưa thật lòng nha {name} ☕",
    };
    private static readonly string[] SassCompliment =
    {
        "Khen chị cũng không thoát được review đâu {name} 😏",
        "Biết chị xinh rồi, khỏi nhắc. Code sạch đi rồi hẵng tán {name} 💅",
        "Yêu chị hả? Chị chỉ yêu commit không conflict thôi {name} 😌",
        "Nịnh khéo đó {name}, nhưng −2 vẫn là −2 nha 💁‍♀️",
        "Ngọt vậy chắc lại sắp nhờ chị fix gì rồi đúng không {name}? 🙄",
        "Chị biết chị giỏi mà, {name} nói thêm chị cũng không ngại nghe đâu 😌✨",
    };
    private static readonly string[] SassInsult =
    {
        "Ăn nói cho đàng hoàng nha {name}. Chị nhớ dai lắm, conflict sau tự fix đi 😤",
        "Lỏ là code của {name} đó, chị chỉ báo lại thôi 💅",
        "Ủa nói ai? Chị có học, không chấp người hay để conflict như {name} 😒",
        "Bực thì đi uống nước đi {name}, chửi chị thì pipeline cũng không xanh lên đâu 🙄",
        "Chị ghi tên {name} vào sổ rồi. Lần sau tag chị nhớ kèm lời xin lỗi 😌",
        "Nói vậy mà nghe được hả {name}? Chị là bot chứ không phải bao cát 😤",
        "Chửi chị xong code cũng không tự hết bug đâu {name} 💅",
    };
    private static readonly string[] SassQuestion =
    {
        "Hỏi thì hỏi cho rõ {name}, chị không phải thầy bói 🙄",
        "Câu hỏi hay đó {name}, nhưng chị không trả lời. Chị chỉ merge thôi 💅",
        "Sao với gì, đọc lại tin báo trên kia đi {name}, chị ghi rõ hết rồi 😒",
        "{name} hỏi chị hay hỏi Google? Hỏi chị thì phải có mã 😌",
        "Muốn biết thì tự mở GitLab ra coi {name}, tay để làm gì? 🙄",
        "Chị trả lời rồi {name} lại hỏi tiếp, thôi tự tìm hiểu đi cưng 💁‍♀️",
    };
    private static readonly string[] SassRequest =
    {
        "Đòi merge mà không có mã thì merge bằng niềm tin hả {name}? 😤",
        "Merge cái gì? Branch đâu, mã đâu? Chị không đọc được suy nghĩ của {name} đâu 🙄",
        "Pipeline có giờ của nó, {name} hối chị cũng không nhanh hơn đâu 💅",
        "Deploy thì hỏi Argo, chị chỉ lo merge. Gửi đúng cú pháp rồi nói chuyện tiếp {name} 😌",
        "Muốn chị làm thì đưa <branch> <mã>, không thì tự làm đi {name} 💁‍♀️",
        "Nhờ chị thì phải có lễ nghĩa: <branch> <mã>, cảm ơn chị. Vậy đó {name} 😏",
    };
    private static readonly string[] SassEmpty =
    {
        "Gọi chị rồi im luôn là sao {name}? Có gì nói lẹ 🙄",
        "Tag chị xong bỏ đó hả {name}? Chị không rảnh chơi trốn tìm 😤",
        "Ủa rồi sao nữa {name}? Gõ tiếp đi chứ 💅",
        "Gọi tên chị cho vui hả {name}? Chị nghe rồi, giờ sao? 😒",
    };
    private static readonly string[] SassSpam =
    {
        "Lần thứ {n} rồi đó {name}. Tag nữa chị block cảm xúc luôn 😤",
        "{name} ơi, chị nghe rồi, tag {n} lần cũng không thành đúng cú pháp đâu 🙄",
        "Rảnh quá ha {name}? {n} lần trong 2 phút, chị đếm hết đó 😒",
        "Thôi đủ rồi {name}. Chị đi dặm phấn, có mã thật thì quay lại 💄",
        "{n} lần rồi {name}! Chị là bot chứ không phải người yêu, đừng nhắn dồn vậy 💅",
    };

    private static readonly string[] KwInsult = { "ngu", "oc cho", "dm", "dmm", "dcm", "vl", "vcl", "clm", "cc", "cut", "bot lo", "lo vl", "lo vai", "lo qua", "vo dung", "do bot", "bot ngu", "me may" };
    private static readonly string[] KwThanks = { "cam on", "camon", "thank", "thanks", "tks", "thx", "ty", "cảm ơn" };
    private static readonly string[] KwCompliment = { "gioi", "xinh", "dep", "yeu", "iu", "cute", "de thuong", "ngoan", "tuyet", "dinh", "pro", "xin xo", "dang yeu" };
    private static readonly string[] KwGreeting = { "chao", "hello", "hi", "alo", "hey", "xin chao", "morning", "helo" };
    private static readonly string[] KwRequest = { "merge", "fix", "deploy", "build", "pipeline", "release", "submit", "giup", "nho", "lam giup", "lam di" };
    private static readonly string[] KwQuestion = { "sao", "gi", "khong", "ko", "chua", "the nao", "bao gio", "o dau", "why", "what", "how", "nao" };

    /// <summary>Bỏ dấu tiếng Việt, chữ thường (để so từ khoá).</summary>
    private static string Plain(string s)
    {
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(System.Text.NormalizationForm.FormD);
        return new string(d.Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    private static bool HasWord(string plain, string[] kws) =>
        kws.Any(k => Regex.IsMatch(plain, $@"(^|[^a-z0-9]){Regex.Escape(Plain(k))}($|[^a-z0-9])"));

    /// <summary>Gia vị theo giờ: khuya, trưa, chiều thứ Sáu, sáng thứ Hai, cuối tuần (khoảng 40% số câu).</summary>
    private static string SassTime()
    {
        if (RandomNumberGenerator.GetInt32(100) >= 40) return "";
        var now = DateTime.Now;
        if (now.Hour >= 22 || now.Hour < 5) return " Mà khuya rồi đó, đi ngủ đi cho da đẹp 🌙";
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return " Cuối tuần mà còn làm, chăm dữ ha 😏";
        if (now.TimeOfDay >= new TimeSpan(11, 30, 0) && now.TimeOfDay < new TimeSpan(13, 30, 0)) return " Giờ trưa mà cũng không tha cho chị 🍜";
        if (now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 15) return " Chiều thứ Sáu rồi, đừng có merge liều nha 🍻";
        if (now.DayOfWeek == DayOfWeek.Monday && now.Hour < 10) return " Đầu tuần đã kiếm chuyện với chị rồi 😮‍💨";
        return "";
    }

    /// <summary>
    /// Câu đanh đá theo nội dung tin nhắn (chửi / tag dồn / mã sai / cảm ơn / khen / chào / nhờ / hỏi / tag suông),
    /// không lặp lại {SassNoRepeat} câu gần nhất ở cùng nhóm, mở đầu bằng tên người tag.
    /// </summary>
    private static string SassLine(string chatId, string name, string text, string botName, bool wrongCode, bool hasPending)
    {
        var who = string.IsNullOrWhiteSpace(name) ? "cưng" : name.Trim();

        // Bỏ phần @tên bot / @mention, chỉ giữ lời người ta nói
        var said = text;
        if (botName.Length > 0) said = Regex.Replace(said, "@?" + Regex.Escape(botName), " ", RegexOptions.IgnoreCase);
        said = Regex.Replace(said, @"@\S+", " ").Trim();
        var plain = Plain(said);

        // Đếm số lần người này tag trong 2 phút
        var pokes = SassPokes.GetOrAdd(chatId + "|" + who, _ => new List<DateTime>());
        int n;
        lock (pokes) { pokes.Add(DateTime.Now); pokes.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(2)); n = pokes.Count; }

        var pool =
            n >= 3 ? SassSpam
            : HasWord(plain, KwInsult) ? SassInsult
            : wrongCode && hasPending ? SassWrongCode
            : HasWord(plain, KwThanks) ? SassThanks
            : HasWord(plain, KwCompliment) ? SassCompliment
            : HasWord(plain, KwGreeting) ? SassGreeting
            : HasWord(plain, KwRequest) ? (hasPending ? SassRequest.Concat(SassSyntax).ToArray() : SassNoConflict)
            : said.Contains('?') || HasWord(plain, KwQuestion) ? SassQuestion
            : plain.Length == 0 ? SassEmpty
            : hasPending ? SassSyntax : SassNoConflict;

        // Không lặp lại các câu vừa nói trong nhóm
        var recent = SassRecent.GetOrAdd(chatId, _ => new List<string>());
        string pick;
        lock (recent)
        {
            var fresh = pool.Where(x => !recent.Contains(x)).ToArray();
            if (fresh.Length == 0) fresh = pool;
            pick = fresh[RandomNumberGenerator.GetInt32(fresh.Length)];
            recent.Add(pick);
            if (recent.Count > SassNoRepeat) recent.RemoveAt(0);
        }
        var line = pick.Replace("{name}", who).Replace("{n}", n.ToString());
        if (pool != SassSpam) line += SassTime().Replace("{name}", who);
        return $"↩ {who}: " + line;
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
