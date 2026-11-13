using System.Collections.Concurrent;

namespace GitLabMR;

/// <summary>Gerrit, chế độ MR: tải change đang mở của các repo đã tick, chọn change rồi tự +2 và submit.</summary>
public sealed partial class AppHost
{
    private static string ChangeKey(string project, long number) => $"{project}~{number}";

    /// <summary>Giá trị cao nhất đang có của 1 nhãn: "+2", "+1", "0", "-1", "-2"; null nếu change không có nhãn đó.</summary>
    private static string? LabelValue(GerritClient.LabelInfo? l, int max)
    {
        if (l == null) return null;
        if (l.Rejected != null) return "-" + max;
        if (l.Approved != null) return "+" + max;
        if (l.Disliked != null) return "-1";
        if (l.Recommended != null) return "+1";
        return "0";
    }

    /// <summary>Trạng thái tóm tắt của change: (nhãn, màu, có chọn sẵn để submit không).</summary>
    private static (string Text, string Cls, bool Pick) ChangeStatus(GerritClient.ChangeInfo c)
    {
        if (c.WorkInProgress) return ("WIP", "mute", false);
        if (c.Mergeable == false) return ("Conflict", "err", false);
        var cr = c.Labels?.GetValueOrDefault("Code-Review");
        if (cr?.Rejected != null) return ("Bị CR −2", "err", false);   // phủ quyết: +2 của mình không gỡ được
        if (c.Submittable) return ("Submit được", "ok", true);
        if (cr?.Disliked != null) return ("Có CR −1", "warn", false);  // có người chưa đồng ý: không chọn sẵn, tự tick nếu chắc
        var missing = (c.SubmitRequirements ?? new())
            .Where(r => r.Status == "UNSATISFIED" && r.Name != null && r.Name != "Code-Review")
            .Select(r => r.Name!).ToList();
        // Gerrit cũ không có submit_requirements: xem nhãn nào đang chặn
        if (c.SubmitRequirements == null && c.Labels != null)
            missing.AddRange(c.Labels.Where(kv => kv.Key != "Code-Review" && (kv.Value.Rejected != null || kv.Value.Blocking)).Select(kv => kv.Key));
        return missing.Count == 0 ? ("Cần +2", "info", true) : ("Thiếu " + string.Join(", ", missing.Distinct()), "warn", false);
    }

    private static object ChangeView(GerritClient gc, GlProject p, GerritClient.ChangeInfo c)
    {
        var (text, cls, pick) = ChangeStatus(c);
        return new
        {
            key = ChangeKey(c.Project, c.Number),
            projectId = p.Id,
            number = c.Number,
            subject = c.Subject,
            branch = c.Branch,
            owner = c.Owner?.Name ?? c.Owner?.Username ?? "",
            updated = c.Updated,
            cr = LabelValue(c.Labels?.GetValueOrDefault("Code-Review"), 2),
            v = LabelValue(c.Labels?.GetValueOrDefault("Verified"), 1),
            size = $"+{c.Insertions} −{c.Deletions}",
            status = text,
            cls,
            pick,
            url = gc.ChangeUrl(c.Project, c.Number),
        };
    }

    private GerritClient GerritApi()
    {
        var st = St;
        if (!st.IsConnected) throw new GitLabException("Kết nối trước đã.");
        return Api(_ws, st) as GerritClient ?? throw new GitLabException("Chỉ workspace Gerrit mới duyệt change được.");
    }

    /// <summary>Tải change đang mở của các repo đã tick (lọc theo branch đích nếu có).</summary>
    private async Task LoadChangesAsync(List<long> ids, string branch, CancellationToken ct)
    {
        var gc = GerritApi();
        var st = St;
        var projects = ids.Where(st.Projects.ContainsKey).Select(id => st.Projects[id]).ToList();
        if (projects.Count == 0) throw new GitLabException("Tick repo cần xem change trước.");
        _ws.Mode = "mr";
        _ws.TargetBranch = branch;
        if (branch.Length > 0) _cfg.RememberBranch(branch);
        TrySave();

        Log($"── [{_ws.Name}] Tải change đang mở{(branch.Length > 0 ? $" → {branch}" : "")} · {projects.Count} repo", LogKind.Info);
        var byPath = projects.ToDictionary(p => p.PathWithNamespace, StringComparer.Ordinal);
        var changes = await gc.GetOpenChangesAsync(byPath.Keys.ToList(), branch, ct);

        st.Changes.Clear();
        var items = new List<object>();
        foreach (var c in changes.OrderBy(c => c.Project, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Number))
        {
            if (!byPath.TryGetValue(c.Project, out var p)) continue;
            if (!st.Changes.TryAdd(ChangeKey(c.Project, c.Number), (p, c))) continue;
            items.Add(ChangeView(gc, p, c));
        }
        Send(new { type = "changes", branch, projectIds = projects.Select(p => p.Id), items });
        var repos = st.Changes.Values.Select(x => x.Project.Id).Distinct().Count();
        Log($"Có {items.Count} change đang mở ở {repos}/{projects.Count} repo", LogKind.Success);
    }

    /// <summary>
    /// +2 rồi submit từng change. Khác repo chạy song song; cùng repo chạy lần lượt theo số change tăng dần
    /// (submit change con sẽ kéo change cha theo — tới lượt cha thì báo "đã merge").
    /// </summary>
    private async Task SubmitChangesAsync(List<string> keys, CancellationToken ct)
    {
        var gc = GerritApi();
        var st = St;
        var list = keys.Where(st.Changes.ContainsKey).Select(k => st.Changes[k]).ToList();
        if (list.Count == 0) throw new GitLabException("Chưa chọn change nào (bấm Tải change trước).");

        Log($"── [{_ws.Name}] +2 & Submit {list.Count} change", LogKind.Info);
        foreach (var (_, c) in list) Send(new { type = "crow", key = ChangeKey(c.Project, c.Number), state = "wait" });

        var results = new ConcurrentBag<MrOutcome>();
        using var gate = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(list.GroupBy(x => x.Change.Project).Select(async g =>
        {
            try { await gate.WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                foreach (var (_, c) in g) SendChangeDone(gc, c, new MrResult(MrOutcome.Failed, "Đã dừng"), results);
                return;
            }
            try
            {
                foreach (var (_, c) in g.OrderBy(x => x.Change.Number))
                {
                    if (ct.IsCancellationRequested) { SendChangeDone(gc, c, new MrResult(MrOutcome.Failed, "Đã dừng"), results); continue; }
                    Send(new { type = "crow", key = ChangeKey(c.Project, c.Number), state = "run" });
                    void CLog(string msg, LogKind k) => Log($"[{c.Project} #{c.Number}] {msg}", k);
                    var res = await SubmitOneAsync(gc, c, CLog, ct);
                    CLog(res.Message, res.Outcome switch
                    {
                        MrOutcome.Submitted => LogKind.Success,
                        MrOutcome.Approved => LogKind.Warn,
                        _ => LogKind.Error,
                    });
                    SendChangeDone(gc, c, res, results);
                }
            }
            finally { gate.Release(); }
        }));

        var counts = results.GroupBy(x => x).ToDictionary(x => x.Key.ToString(), x => x.Count());
        Send(new { type = "done", dryRun = false, counts });
        Log("── Xong: " + string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}")), LogKind.Success);
    }

    private void SendChangeDone(GerritClient gc, GerritClient.ChangeInfo c, MrResult res, ConcurrentBag<MrOutcome> results)
    {
        results.Add(res.Outcome);
        Send(new
        {
            type = "crow", key = ChangeKey(c.Project, c.Number), state = "done",
            outcome = res.Outcome.ToString(), text = res.Message, url = gc.ChangeUrl(c.Project, c.Number),
        });
    }

    private static async Task<MrResult> SubmitOneAsync(GerritClient gc, GerritClient.ChangeInfo c, Action<string, LogKind> log, CancellationToken ct)
    {
        const string mergedByOther = "Đã merge (được submit cùng change khác)";
        var approved = false;
        try
        {
            // Đọc lại trạng thái mới nhất: có thể đã merge / abandon / có người +2 sau lúc tải
            var cur = await gc.GetChangeAsync(c.Project, c.Number, ct);
            if (cur.Status == "MERGED") return new MrResult(MrOutcome.Submitted, mergedByOther);
            if (cur.Status == "ABANDONED") return new MrResult(MrOutcome.Failed, "Change đã bị abandon");
            if (cur.WorkInProgress) return new MrResult(MrOutcome.Failed, "Change đang WIP – bỏ WIP trên Gerrit rồi submit");

            if (cur.Labels?.GetValueOrDefault("Code-Review")?.Approved == null)
            {
                await gc.ApproveAsync(c.Project, c.Number, ct);
                log("Đã +2", LogKind.Info);
            }
            approved = true;

            var done = await gc.SubmitAsync(c.Project, c.Number, ct);
            return done.Status == "MERGED"
                ? new MrResult(MrOutcome.Submitted, "Đã +2 & submit")
                : new MrResult(MrOutcome.Approved, $"Đã +2, đã gửi submit nhưng trạng thái là {done.Status}");
        }
        catch (OperationCanceledException)
        {
            return new MrResult(approved ? MrOutcome.Approved : MrOutcome.Failed, approved ? "Đã +2, dừng trước khi submit" : "Đã dừng");
        }
        catch (Exception ex) when (approved)
        {
            // Submit lỗi: có thể change vừa được merge cùng change khác
            try
            {
                var cur = await gc.GetChangeAsync(c.Project, c.Number, CancellationToken.None);
                if (cur.Status == "MERGED") return new MrResult(MrOutcome.Submitted, mergedByOther);
            }
            catch { /* giữ lỗi gốc */ }
            return new MrResult(MrOutcome.Approved, "Đã +2, chưa submit được: " + Describe(ex));
        }
        catch (Exception ex)
        {
            return new MrResult(MrOutcome.Failed, Describe(ex));
        }
    }
}
