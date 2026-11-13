using System.Net;

namespace GitLabMR;

public enum LogKind { Info, Success, Warn, Error }

public enum MrOutcome
{
    Ready, NoChanges, MissingBranch, AlreadyOpen, Created, Merged, AutoMerge, NotMerged, BranchCreated, BranchExists, Failed,
    /// <summary>Gerrit: đã +2 và submit.</summary>
    Submitted,
    /// <summary>Gerrit: đã +2 nhưng chưa submit được (thiếu Verified, conflict…).</summary>
    Approved,
}

public sealed record MrResult(MrOutcome Outcome, string Message, string? Url = null)
{
    public bool IsGood => Outcome is MrOutcome.Ready or MrOutcome.Created or MrOutcome.Merged or MrOutcome.AutoMerge or MrOutcome.BranchCreated or MrOutcome.Submitted;
}

/// <summary>Tạo branch hàng loạt: branch mới từ branch gốc, bỏ qua nếu đã có.</summary>
public static class BranchWorkflow
{
    /// <summary>Kiểm tra (không tạo gì). Dùng được cho cả GitLab và Gerrit.</summary>
    public static async Task<MrResult> CheckAsync(IBranchApi api, GlProject p, string baseBranch, string newBranch, CancellationToken ct)
    {
        var baseTask = api.BranchExistsAsync(p, baseBranch, ct);
        var newTask = api.BranchExistsAsync(p, newBranch, ct);
        if (!await baseTask) return new(MrOutcome.MissingBranch, $"Thiếu branch gốc '{baseBranch}'");
        if (await newTask) return new(MrOutcome.BranchExists, $"Đã có '{newBranch}'", api.BranchUrl(p, newBranch));
        return new(MrOutcome.Ready, $"Sẵn sàng tạo từ '{baseBranch}'");
    }

    public static async Task<MrResult> CreateAsync(IBranchApi api, GlProject p, string baseBranch, string newBranch,
        Action<string, LogKind> log, CancellationToken ct)
    {
        var check = await CheckAsync(api, p, baseBranch, newBranch, ct);
        if (check.Outcome != MrOutcome.Ready)
        {
            log(check.Message + ", bỏ qua.", LogKind.Warn);
            return check;
        }
        var b = await api.CreateBranchAsync(p, newBranch, baseBranch, ct);
        var url = b.WebUrl ?? api.BranchUrl(p, newBranch);
        log($"Đã tạo branch '{newBranch}' từ '{baseBranch}': {url}", LogKind.Success);
        return new(MrOutcome.BranchCreated, $"Đã tạo '{newBranch}'", url);
    }

    /// <summary>Kiểm tra nhanh tên branch theo quy tắc git (đủ để chặn lỗi gõ phổ biến).</summary>
    public static string? ValidateName(string name)
    {
        if (name.Length == 0) return "Nhập tên branch mới.";
        if (name.Any(char.IsWhiteSpace)) return "Tên branch không được có khoảng trắng.";
        if (name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.') || name.Contains("..") || name.Contains("//")
            || name.EndsWith(".lock") || name.IndexOfAny(new[] { '~', '^', ':', '?', '*', '[', '\\' }) >= 0 || name.Contains("@{"))
            return $"Tên branch '{name}' không hợp lệ.";
        return null;
    }
}

public sealed record MrOptions(
    string SourceBranch,
    string TargetBranch,
    string TitleTemplate,
    string Description,
    bool Merge,
    bool AutoMergeWhenPipeline,
    bool RemoveSource,
    bool Squash)
{
    public string TitleFor(GlProject p) => TitleTemplate
        .Replace("{source}", SourceBranch)
        .Replace("{target}", TargetBranch)
        .Replace("{project}", p.Name);
}

/// <summary>Luồng cho 1 project: check branch -> compare -> tìm MR đang mở / tạo MR -> (tuỳ chọn) chờ check mergeable -> merge.</summary>
public static class MrWorkflow
{
    // Trạng thái GitLab đang tính toán, cần chờ thêm.
    private static readonly HashSet<string> PendingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "unchecked", "checking", "preparing", "approvals_syncing", "cannot_be_merged_recheck",
    };

    /// <summary>Dry-run: không tạo gì, chỉ báo project có sẵn sàng tạo MR không.</summary>
    public static async Task<MrResult> CheckAsync(GitLabClient gl, GlProject p, string source, string target, CancellationToken ct)
    {
        var missing = await MissingBranchAsync(gl, p, source, target, ct);
        if (missing != null) return new(MrOutcome.MissingBranch, $"Thiếu branch '{missing}'");

        var cmpTask = gl.CompareAsync(p.Id, target, source, ct);
        var openTask = gl.FindOpenMrAsync(p.Id, source, target, ct);
        var cmp = await cmpTask;
        var open = await openTask;

        if (cmp.Commits.Count == 0) return new(MrOutcome.NoChanges, "Không có commit mới");
        if (open != null) return new(MrOutcome.AlreadyOpen, $"Đã có MR !{open.Iid} đang mở ({cmp.Commits.Count} commit)", open.WebUrl);
        return new(MrOutcome.Ready, $"Sẵn sàng — {cmp.Commits.Count} commit");
    }

    public static async Task<MrResult> RunAsync(GitLabClient gl, GlProject p, MrOptions o,
        Action<string, LogKind> log, CancellationToken ct)
    {
        // 1) Branch có tồn tại không, có gì để merge không?
        var missing = await MissingBranchAsync(gl, p, o.SourceBranch, o.TargetBranch, ct);
        if (missing != null)
        {
            log($"Thiếu branch '{missing}', bỏ qua.", LogKind.Warn);
            return new(MrOutcome.MissingBranch, $"Thiếu branch '{missing}'");
        }

        var cmp = await gl.CompareAsync(p.Id, o.TargetBranch, o.SourceBranch, ct);
        if (cmp.Commits.Count == 0)
        {
            log("Không có commit mới, không tạo MR.", LogKind.Warn);
            return new(MrOutcome.NoChanges, "Không có commit mới");
        }
        log($"{cmp.Commits.Count} commit:", LogKind.Info);
        foreach (var c in cmp.Commits.TakeLast(5))
            log($"   {c.ShortId}  {c.Title}  ({c.AuthorName})", LogKind.Info);
        if (cmp.Commits.Count > 5) log($"   ... và {cmp.Commits.Count - 5} commit khác", LogKind.Info);

        // 2) Dùng lại MR đang mở hoặc tạo mới
        var mr = await gl.FindOpenMrAsync(p.Id, o.SourceBranch, o.TargetBranch, ct);
        var reused = mr != null;
        if (mr != null)
        {
            log($"Đã có MR đang mở !{mr.Iid}, dùng lại: {mr.WebUrl}", LogKind.Warn);
        }
        else
        {
            mr = await gl.CreateMrAsync(p.Id, o.SourceBranch, o.TargetBranch, o.TitleFor(p), o.Description,
                o.RemoveSource, o.Squash, ct);
            log($"Đã tạo MR !{mr.Iid}: {mr.WebUrl}", LogKind.Success);
        }

        if (!o.Merge)
            return reused
                ? new(MrOutcome.AlreadyOpen, $"MR !{mr.Iid} đã có sẵn", mr.WebUrl)
                : new(MrOutcome.Created, $"Đã tạo MR !{mr.Iid} ({cmp.Commits.Count} commit)", mr.WebUrl);

        // 3) Chờ GitLab check mergeable
        mr = await WaitForMergeStatusAsync(gl, p.Id, mr, ct);
        var status = EffectiveStatus(mr);

        bool whenPipeline;
        switch (status)
        {
            case "mergeable":
                whenPipeline = false;
                break;
            case "ci_must_pass":
            case "ci_still_running":
                if (!o.AutoMergeWhenPipeline)
                {
                    log($"Chưa merge: {Explain(status)} {mr.WebUrl}", LogKind.Warn);
                    return new(MrOutcome.NotMerged, $"!{mr.Iid} chưa merge: {Explain(status)}", mr.WebUrl);
                }
                whenPipeline = true;
                break;
            default:
                log($"Không merge được: {Explain(status)} {mr.WebUrl}", LogKind.Warn);
                return new(MrOutcome.NotMerged, $"!{mr.Iid} không merge được: {Explain(status)}", mr.WebUrl);
        }

        // 4) Merge
        try
        {
            var merged = await gl.MergeAsync(p.Id, mr.Iid, mr.Sha, o.RemoveSource, o.Squash, whenPipeline, ct);
            if (string.Equals(merged.State, "merged", StringComparison.OrdinalIgnoreCase))
            {
                log($"MERGE THÀNH CÔNG !{merged.Iid}: {merged.WebUrl}", LogKind.Success);
                return new(MrOutcome.Merged, $"Đã merge !{merged.Iid} ({cmp.Commits.Count} commit)", merged.WebUrl);
            }
            if (whenPipeline)
            {
                log($"Đã bật auto-merge !{merged.Iid}, sẽ tự merge khi pipeline pass. {merged.WebUrl}", LogKind.Success);
                return new(MrOutcome.AutoMerge, $"!{merged.Iid} auto-merge khi pipeline pass", merged.WebUrl);
            }
            log($"GitLab nhận lệnh merge, trạng thái: {merged.State}. {merged.WebUrl}", LogKind.Warn);
            return new(MrOutcome.NotMerged, $"!{merged.Iid} trạng thái {merged.State}", merged.WebUrl);
        }
        catch (GitLabException ex) when (ex.Status is HttpStatusCode.MethodNotAllowed
                                             or HttpStatusCode.NotAcceptable
                                             or HttpStatusCode.Conflict
                                             or HttpStatusCode.UnprocessableEntity
                                             or HttpStatusCode.Unauthorized
                                             or HttpStatusCode.Forbidden)
        {
            var hint = ex.Status switch
            {
                HttpStatusCode.Conflict => "Source branch vừa có commit mới (SHA đổi), thử lại.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Không có quyền merge vào branch này (protected branch?).",
                _ => "MR chưa đủ điều kiện merge.",
            };
            log($"Merge thất bại: {hint} {ex.Message} {mr.WebUrl}", LogKind.Error);
            return new(MrOutcome.NotMerged, $"!{mr.Iid} merge lỗi: {hint}", mr.WebUrl);
        }
    }

    private static async Task<string?> MissingBranchAsync(GitLabClient gl, GlProject p, string source, string target, CancellationToken ct)
    {
        var s = gl.BranchExistsAsync(p.Id, source, ct);
        var t = gl.BranchExistsAsync(p.Id, target, ct);
        if (!await s) return source;
        if (!await t) return target;
        return null;
    }

    private static async Task<GlMergeRequest> WaitForMergeStatusAsync(GitLabClient gl, long projectId,
        GlMergeRequest mr, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            mr = await gl.GetMrAsync(projectId, mr.Iid, ct);
            if (!PendingStatuses.Contains(EffectiveStatus(mr)) || DateTime.UtcNow > deadline) return mr;
            await Task.Delay(2000, ct);
        }
    }

    /// <summary>detailed_merge_status (GitLab ≥ 15.6), fallback merge_status cho bản cũ.</summary>
    private static string EffectiveStatus(GlMergeRequest mr)
    {
        if (!string.Equals(mr.State, "opened", StringComparison.OrdinalIgnoreCase)) return "not_open";
        if (!string.IsNullOrEmpty(mr.DetailedMergeStatus)) return mr.DetailedMergeStatus!;
        if (mr.HasConflicts) return "conflict";
        return mr.MergeStatus switch
        {
            "can_be_merged" => "mergeable",
            "cannot_be_merged" => "conflict",
            null or "" => "unchecked",
            var s => s,
        };
    }

    private static string Explain(string status) => status switch
    {
        "conflict" => "Có conflict, cần resolve tay.",
        "need_rebase" => "Cần rebase source branch (project bật fast-forward merge).",
        "not_approved" => "Chưa đủ số approve.",
        "requested_changes" => "Reviewer đang yêu cầu sửa.",
        "draft_status" => "MR đang ở trạng thái Draft.",
        "discussions_not_resolved" => "Còn thread chưa resolve.",
        "ci_must_pass" => "Project yêu cầu pipeline pass trước khi merge.",
        "ci_still_running" => "Pipeline đang chạy.",
        "blocked_status" or "merge_request_blocked" => "Bị chặn bởi MR khác.",
        "not_open" => "MR không còn mở.",
        "broken_status" => "Source branch lỗi.",
        "external_status_checks" => "External status checks chưa pass.",
        "jira_association_missing" => "Thiếu Jira issue key.",
        "policies_denied" or "security_policy_violations" => "Vi phạm merge/security policy.",
        "locked_paths" or "locked_lfs_files" => "Có file/path đang bị lock.",
        "title_regex" => "Title không khớp regex của project.",
        "unchecked" or "checking" or "preparing" => "GitLab chưa kiểm tra xong (timeout).",
        _ => $"Trạng thái '{status}'.",
    };
}
