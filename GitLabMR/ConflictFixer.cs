using System.Diagnostics;
using System.Text;

namespace GitLabMR;

/// <summary>
/// Auto-fix conflict MR GitLab theo quy trình ppx-merge-testing (branch feature giữ sạch, không merge môi trường vào):
///   1. Tạo branch {A}_fix_conflict từ origin/testing2, merge --no-ff --no-commit origin/A vào.
///   2. Conflict thì gọi Claude Code CLI (claude -p) sửa đúng các file conflict, đánh giá độ nặng / độ tự tin, build hẹp.
///   3. Kiểm tra hết dấu conflict, commit "merge branch A to testing2" (author = git user của máy), push branch fix.
///      App tạo MR fix → testing2; chỉ merge khi không có conflict Nặng, độ tự tin đủ và build không fail.
/// Repo clone 1 lần tại %LOCALAPPDATA%\GitLabMR\work\{project id}, các lần sau chỉ fetch.
/// </summary>
public static class ConflictFixer
{
    /// <summary>Ok = đã push branch fix; Safe = được phép merge tự động (không Nặng, tự tin đủ, build không fail).</summary>
    public sealed record Result(bool Ok, string Message, List<string> Files, string? Summary = null, bool Safe = true, bool BranchExists = false);

    public sealed record FileFix(string File, string Resolution, string Severity, int Confidence);

    /// <summary>Độ tự tin tối thiểu (%) để tự merge.</summary>
    public const int MinConfidence = 80;

    private static readonly TimeSpan ClaudeWait = TimeSpan.FromMinutes(30);

    public static string WorkRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitLabMR", "work");

    /// <summary>Tìm Claude Code CLI: đường dẫn cấu hình → PATH → ~/.local/bin → npm global.</summary>
    public static string? FindClaude(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        dirs.Add("/opt/homebrew/bin"); dirs.Add("/usr/local/bin");
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var p = Path.Combine(d.Trim('"'), n); if (File.Exists(p)) return p; } catch { }
            }
        return null;
    }

    /// <summary>Gọi thử Claude: trả về null nếu chạy được, ngược lại là lý do.</summary>
    public static async Task<string?> CheckClaudeAsync(string? configured, CancellationToken ct)
    {
        var exe = FindClaude(configured);
        if (exe == null) return "Không tìm thấy Claude Code CLI (claude). Cài: npm i -g @anthropic-ai/claude-code, hoặc nhập đường dẫn claude.exe.";
        var dir = Directory.CreateDirectory(Path.Combine(WorkRoot, "_check")).FullName;
        var r = await RunAsync(exe, new[] { "-p", "--output-format", "text" }, dir, null, "Reply with exactly: OK", TimeSpan.FromMinutes(2), ct);
        return r.Code == 0 && r.Out.Contains("OK") ? null : ClaudeError(r);
    }

    private static string ClaudeError((int Code, string Out, string Err) r)
    {
        var all = (r.Out + "\n" + r.Err).Trim();
        if (all.Contains("authenticat", StringComparison.OrdinalIgnoreCase) || all.Contains("login", StringComparison.OrdinalIgnoreCase))
            return "Claude Code chưa đăng nhập: mở terminal chạy `claude` rồi gõ /login.";
        return $"Claude lỗi (exit {r.Code}): {Tail(all, 400)}";
    }

    public static async Task<Result> FixAsync(
        string baseUrl, string token, bool ignoreSsl, GlProject p, string source, string target, string fixBranch,
        string? claudePath, Func<List<string>, Task> onConflict, Action<string, LogKind> log, CancellationToken ct)
    {
        var exe = FindClaude(claudePath);
        var dir = Path.Combine(WorkRoot, p.Id.ToString());
        var env = GitEnv(baseUrl, token, ignoreSsl);
        var remote = $"{baseUrl.TrimEnd('/')}/{p.PathWithNamespace}.git";

        async Task<(int Code, string Out, string Err)> Git(params string[] args) =>
            await RunAsync("git", args, dir, env, null, TimeSpan.FromMinutes(10), ct);
        async Task<string> GitOk(params string[] args)
        {
            var r = await Git(args);
            if (r.Code != 0) throw new GitLabException($"git {args[0]} lỗi: {Tail((r.Err + " " + r.Out).Trim(), 300)}");
            return r.Out;
        }

        // 1. Repo làm việc: clone 1 lần, sau đó chỉ fetch 2 branch cần dùng
        Directory.CreateDirectory(dir);
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            log("Tạo repo làm việc " + dir, LogKind.Info);
            await GitOk("init", "-q");
            await GitOk("remote", "add", "origin", remote);
        }
        else await Git("remote", "set-url", "origin", remote);
        await Git("merge", "--abort");
        await Git("reset", "-q", "--hard");
        await Git("clean", "-qfd");
        log($"Fetch {source}, {target}…", LogKind.Info);
        await GitOk("fetch", "-q", "--no-tags", "origin",
            $"+refs/heads/{source}:refs/remotes/origin/{source}", $"+refs/heads/{target}:refs/remotes/origin/{target}");

        // Branch fix đã có trên server (lần fix trước chưa merge, có thể đang được sửa tay) → không ghi đè
        if ((await Git("ls-remote", "--exit-code", "--heads", "origin", fixBranch)).Code == 0)
            return new(false, $"Branch '{fixBranch}' đã có trên GitLab (lần fix trước chưa merge?) – app không ghi đè. Xem / merge / xoá branch đó, lần submit sau app chạy lại.", new(), BranchExists: true);

        // 2. Branch fix từ branch đích (testing2), merge branch nguồn vào – branch nguồn không bị đụng tới
        await GitOk("checkout", "-q", "-f", "--no-track", "-B", fixBranch, $"origin/{target}");
        var merge = await Git("-c", "merge.conflictstyle=diff3", "merge", "--no-ff", "--no-commit", $"origin/{source}");
        var files = new List<string>();
        var fixes = new List<FileFix>();
        string? summary = null, build = null;
        var message = $"merge branch {source} to {target}";
        var hasMergeHead = File.Exists(Path.Combine(dir, ".git", "MERGE_HEAD"));
        if (merge.Code != 0)
        {
            var status = (await GitOk("status", "--porcelain")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var unmerged = status.Where(l => l.Length > 3 && (l[0] == 'U' || l[1] == 'U' || l[..2] is "AA" or "DD")).ToList();
            if (unmerged.Count == 0) throw new GitLabException("git merge lỗi: " + Tail((merge.Err + " " + merge.Out).Trim(), 300));
            files = unmerged.Select(l => l[3..].Trim().Trim('"')).ToList();
            await onConflict(files);

            var structural = unmerged.Where(l => l[..2] is not ("UU" or "AA")).Select(l => $"{l[3..].Trim()} ({l[..2]})").ToList();
            if (structural.Count > 0)
                return new(false, "Conflict xoá / đổi tên file, cần fix tay: " + string.Join(", ", structural), files);
            if (exe == null)
                return new(false, "Không tìm thấy Claude Code CLI (claude) để sửa conflict.", files);

            // 3. Claude sửa conflict trong đúng các file đó
            log($"Gọi Claude sửa {files.Count} file conflict…", LogKind.Info);
            var prompt = Prompt(p, source, target, files);
            var r = await RunAsync(exe, new[]
            {
                "-p", "--output-format", "text", "--permission-mode", "acceptEdits",
                "--allowedTools", "Read,Edit,Write,Glob,Grep,Bash(git diff:*),Bash(git log:*),Bash(git show:*),Bash(git status:*),"
                                  + "Bash(git merge-base:*),Bash(dotnet build:*),Bash(dotnet restore:*)",
            }, dir, null, prompt, ClaudeWait, ct);
            if (r.Code != 0) return new(false, ClaudeError(r), files);
            (fixes, build) = ParseReport(r.Out);
            summary = fixes.Count > 0
                ? string.Join("\n", fixes.Select(x => $"- {x.File}: {x.Resolution} [{x.Severity}, {x.Confidence}%]")) + (build != null ? $"\nBuild: {build}" : "")
                : Tail(r.Out.Trim(), 900);
            message += "\n\nConflict resolution:\n" + (fixes.Count > 0
                ? string.Join("\n", fixes.Select(x => $"- {x.File}: {x.Resolution}"))
                : string.Join("\n", files.Select(f => $"- {f}: resolved")));

            // Chỉ giữ thay đổi trong file conflict, file khác Claude lỡ sửa thì trả lại
            foreach (var l in (await GitOk("status", "--porcelain")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = l.Length > 3 ? l[3..].Trim().Trim('"') : "";
                if (files.Contains(f)) continue;
                if (l.StartsWith("??")) { try { File.Delete(Path.Combine(dir, f)); } catch { } }
                else if (l[1] != ' ') await Git("checkout", "--", f);
            }
            var left = files.Where(f => HasMarkers(Path.Combine(dir, f))).ToList();
            if (left.Count > 0) return new(false, "Claude chưa sửa hết conflict: " + string.Join(", ", left), files, summary);

            await GitOk(new[] { "add", "--" }.Concat(files).ToArray());
            if ((await GitOk("diff", "--name-only", "--diff-filter=U")).Trim().Length > 0)
                return new(false, "Vẫn còn file chưa resolve.", files, summary);
            await GitOk("commit", "-q", "-m", message);
        }
        else if (hasMergeHead)
        {
            log("Merge không còn conflict (branch đích có thể đã đổi).", LogKind.Info);
            await GitOk("commit", "-q", "-m", message);
        }
        else return new(false, "Branch nguồn không có commit mới so với branch đích (Already up to date).", files);

        // 4. Push branch fix (branch của app, ghi đè được)
        await GitOk("push", "-q", "--force", "origin", $"HEAD:refs/heads/{fixBranch}");
        log($"Đã push {fixBranch}", LogKind.Success);
        // Chỉ tự merge khi chắc: đủ đánh giá cho mọi file, không Nặng, tự tin đủ, build không fail
        var why = new List<string>();
        if (files.Count > 0)
        {
            if (fixes.Count < files.Count) why.Add("Claude không đánh giá đủ từng file");
            if (fixes.Any(x => x.Severity.Contains("heavy", StringComparison.OrdinalIgnoreCase) || x.Severity.Contains("N\u1eb7ng", StringComparison.OrdinalIgnoreCase)))
                why.Add("có conflict Nặng");
            if (fixes.Count > 0 && fixes.Min(x => x.Confidence) < MinConfidence) why.Add($"độ tự tin < {MinConfidence}%");
            if (build?.StartsWith("fail", StringComparison.OrdinalIgnoreCase) == true) why.Add("build fail");
        }
        var safe = why.Count == 0;
        return new(true, files.Count > 0 ? $"Đã fix {files.Count} file conflict" + (safe ? "" : $" – cần duyệt ({string.Join(", ", why)})") : "Không còn conflict",
            files, summary, safe);
    }

    /// <summary>Đọc khối ```json cuối cùng Claude trả về: {"files":[{file,resolution,severity,confidence}], "build": "..."}.</summary>
    private static (List<FileFix>, string?) ParseReport(string output)
    {
        var list = new List<FileFix>();
        string? build = null;
        var i = output.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        var json = i >= 0 ? output[(i + 7)..] : output[Math.Max(0, output.LastIndexOf('{'))..];
        var end = json.IndexOf("```", StringComparison.Ordinal);
        if (end >= 0) json = json[..end];
        try
        {
            var start = json.IndexOf('{');
            if (start < 0) return (list, null);
            using var doc = System.Text.Json.JsonDocument.Parse(json[start..]);
            var root = doc.RootElement;
            if (root.TryGetProperty("build", out var b)) build = b.ToString();
            if (root.TryGetProperty("files", out var fs) && fs.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var f in fs.EnumerateArray())
                {
                    string S(string k) => f.TryGetProperty(k, out var v) ? v.ToString() : "";
                    var conf = f.TryGetProperty("confidence", out var c) && c.TryGetInt32(out var n) ? n : int.TryParse(S("confidence").TrimEnd('%'), out var m) ? m : 0;
                    list.Add(new(S("file"), S("resolution"), S("severity"), conf));
                }
        }
        catch { }
        return (list, build);
    }

    private static string Prompt(GlProject p, string source, string target, List<string> files) => $$"""
        You are resolving git merge conflicts in repository {{p.PathWithNamespace}} (working directory = repo root).
        State: branch '{{source}}_fix_conflict' was created from origin/{{target}} (shared test environment) and
        `git merge --no-ff --no-commit origin/{{source}}` (feature branch) stopped on conflicts.
        Conflict markers are diff3 style: <<<<<<< ours = {{target}}, ||||||| base, =======, >>>>>>> theirs = {{source}}.
        Files with conflicts:
        {{string.Join("\n", files.Select(f => "- " + f))}}

        Rules (follow strictly):
        - Edit ONLY these files. Remove every conflict marker so each file is valid code.
        - For each hunk read the real blocks around the markers (ours = {{target}}, theirs = {{source}}); do not invent code.
        - Keep the intent of BOTH sides: the feature change and what others merged into {{target}}. Combine additions
          (methods, usings/imports, config keys, DI registrations). Never use a whole-file "take ours/theirs".
        - If you decide to keep only one side of a hunk, first Grep to prove the dropped code is not referenced anywhere
          and that equivalent logic exists on the kept side; mention that evidence in the resolution.
        - Sensitive files are Heavy by default: *.csproj / Directory.Packages.props (package versions), *ModelSnapshot.cs and
          Migrations/* (do not hand-merge – mark Heavy, confidence <= 50), *.resx / *.Designer.cs, appsettings*.json, .gitlab-ci*.yml.
        - Severity: Light = only usings/imports, line order, comments, whitespace; Medium = one side only adds logic,
          the other edits elsewhere in the same file; Heavy = both sides changed the same logic, a side deleted/renamed
          a member, or a sensitive file.
        - Narrow build: if a .csproj contains the changed files, run `dotnet build <that.csproj> --disable-build-servers -v q -nologo`
          (only that project, not the solution). Report "pass", "fail: <first errors>" or "skipped: <reason>"
          (e.g. restore failed because of network/feeds). Lower confidence if the build was skipped.
        - Do NOT run git add, commit, checkout, reset, merge, push. Do not touch any other file. Do not mention Claude anywhere.
        - Write resolutions in English, one short line each (they go into the commit message).

        Finish your reply with exactly one JSON block and nothing after it:
        ```json
        {"files":[{"file":"path","resolution":"how it was resolved and why","severity":"Light|Medium|Heavy","confidence":90}],"build":"pass"}
        ```
        """;

    private static bool HasMarkers(string path)
    {
        if (!File.Exists(path)) return false;
        foreach (var line in File.ReadLines(path))
            if (line.StartsWith("<<<<<<< ") || line.StartsWith(">>>>>>> ") || line.StartsWith("||||||| ")) return true;
        return false;
    }

    /// <summary>Token đưa qua biến môi trường (không nằm trên dòng lệnh / trong .git/config).</summary>
    private static Dictionary<string, string> GitEnv(string baseUrl, string token, bool ignoreSsl)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("oauth2:" + token));
        var cfg = new List<(string, string)>
        {
            ($"http.{baseUrl.TrimEnd('/')}/.extraHeader", "Authorization: Basic " + basic),
            ("credential.helper", ""),
            ("core.autocrlf", "false"),
            ("core.longpaths", "true"),
        };
        if (ignoreSsl) cfg.Add(("http.sslVerify", "false"));
        var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never", ["GIT_CONFIG_COUNT"] = cfg.Count.ToString() };
        for (var i = 0; i < cfg.Count; i++) { env[$"GIT_CONFIG_KEY_{i}"] = cfg[i].Item1; env[$"GIT_CONFIG_VALUE_{i}"] = cfg[i].Item2; }
        return env;
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(
        string exe, IEnumerable<string> args, string dir, Dictionary<string, string>? env, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var proc = Process.Start(psi) ?? throw new GitLabException("Không chạy được " + exe);
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (stdin != null) await proc.StandardInput.WriteAsync(stdin);
        proc.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new GitLabException($"{Path.GetFileNameWithoutExtension(exe)} quá {timeout.TotalMinutes:0} phút chưa xong.");
        }
        return (proc.ExitCode, await outTask, await errTask);
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];
}
