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
    private static readonly Regex CmdRx = new(@"(?<br>[^\s@]+)\s+(?<otp>[A-Za-z0-9]{6})(?=\s|$)", RegexOptions.Compiled);

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
            foreach (Match m in CmdRx.Matches(u.Text))
            {
                var hit = a.Otps.FirstOrDefault(x => x.Otp.Equals(m.Groups["otp"].Value, StringComparison.OrdinalIgnoreCase));
                if (hit != null) { o = hit; branch = m.Groups["br"].Value.Trim('`', '"', '\'', '<', '>'); break; }
            }
            if (o == null)
            {
                // Có mã OTP nhưng thiếu branch, hoặc mã sai / hết hạn
                var codes = Regex.Matches(u.Text, @"\b[A-Za-z0-9]{6}\b").Select(m => m.Value).ToList();
                var only = codes.Select(cd => a.Otps.FirstOrDefault(x => x.Otp.Equals(cd, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(x => x != null);
                if (only != null) { _ = Reply($"Thiếu branch. Cú pháp: <branch> {only.Otp}"); return; }
                return; // tin nhắn thường: bỏ qua
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

    /// <summary>"Lấy chat ID" khi luồng nghe đang chạy: chờ tin mới mà luồng nghe thấy.</summary>
    private async Task<ZaloSeen?> WaitSeenAsync(AutoState a, TimeSpan wait)
    {
        var since = DateTime.Now;
        for (var end = since + wait; DateTime.Now < end; await Task.Delay(500))
            lock (a.Seen) { var s = a.Seen.LastOrDefault(x => x.At >= since); if (s != null) return s; }
        return null;
    }
}
