using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Services;

/// <summary>历史的读取接口（VM 通过它取数据，测试可以替换）。</summary>
public interface IGitHistory
{
    Task<GitHistoryResult<IReadOnlyList<GitCommitInfo>>> LogAsync(string repository, int skip, int count, CancellationToken ct);
    Task<GitHistoryResult<GitCommitDetail>> ShowAsync(string repository, string sha, CancellationToken ct);
    Task<GitHistoryResult<GitFileDiff>> FileDiffAsync(string repository, string sha, GitFileChange file, CancellationToken ct);
}

/// <summary>提交列表中的一条（<c>git log</c> 的一行）。</summary>
public sealed record GitCommitInfo(string Sha, string ShortSha, string Author, DateTimeOffset AuthoredAt, IReadOnlyList<string> Parents, string Subject);

/// <summary>一条提交的正文与改动文件（<c>git show --numstat</c>）。</summary>
public sealed record GitCommitDetail(string Sha, string Message, IReadOnlyList<GitFileChange> Files);

/// <param name="Path">新路径（删除时为原路径）；相对于仓库根，'/' 分隔。</param>
/// <param name="OldPath">重命名前的路径；不是重命名时为 null。</param>
/// <param name="Added">新增行数；二进制 / 子模块为 null。</param>
/// <param name="Removed">删除行数；二进制 / 子模块为 null。</param>
/// <param name="IsRename">numstat 报告为重命名（old → new）。</param>
public sealed record GitFileChange(string Path, string? OldPath, int? Added, int? Removed, bool IsBinary, bool IsRename);

/// <summary>
/// 一个文件的 patch。<see cref="IsRenameOnly"/> 表示只是在重命名、没有内容改动（<see cref="Lines"/> 为空）；
/// <see cref="Truncated"/> 表示超过了解析器的行数上限。
/// </summary>
public sealed record GitFileDiff(IReadOnlyList<DiffLineViewModel> Lines, bool Truncated, bool IsRenameOnly);

/// <summary>预期内的失败（不是仓库、git 未安装、非零退出、超时）不抛异常，放在 <paramref name="Error"/> 里。</summary>
public sealed record GitHistoryResult<T>(T? Value, string? Error) where T : class;

/// <summary>
/// 历史面板的数据来源：调用本机 git（参数逐个传入，不经过 shell），只读。
/// 列表、提交详情、单文件 patch 分三步取，避免为一次展开加载整条提交的 diff。
/// </summary>
public sealed class GitHistoryService : IGitHistory
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public GitHistoryService()
    {
    }

    public async Task<GitHistoryResult<IReadOnlyList<GitCommitInfo>>> LogAsync(string repository, int skip, int count, CancellationToken ct)
    {
        // 非仓库不启动进程：与 GitRepository.Repositories 用同一套发现规则
        if (GitLayout.Find(repository) is null)
        {
            return new GitHistoryResult<IReadOnlyList<GitCommitInfo>>(null, NotARepository(repository));
        }
        var run = await RunAsync(repository, [
            "-c", "core.quotePath=false",
            "--no-pager", "log", "--no-color", "--no-show-signature", "--date=iso-strict",
            $"--max-count={count.ToString(CultureInfo.InvariantCulture)}",
            $"--skip={skip.ToString(CultureInfo.InvariantCulture)}",
            "--format=%H\u001f%h\u001f%an\u001f%aI\u001f%P\u001f%s",
            "HEAD",
        ], ct);
        if (run.Error is { } failure)
        {
            return new GitHistoryResult<IReadOnlyList<GitCommitInfo>>(null, failure);
        }
        if (run.ExitCode != 0)
        {
            // 空仓库（unborn branch）与错误都让 log 失败：HEAD 也不存在才是「还没有提交」
            var probe = await RunAsync(repository, ["rev-parse", "--verify", "-q", "HEAD"], ct);
            return probe.Error is null && probe.ExitCode == 0
                ? new GitHistoryResult<IReadOnlyList<GitCommitInfo>>(null, Describe(run))
                : new GitHistoryResult<IReadOnlyList<GitCommitInfo>>([], null);
        }
        return new GitHistoryResult<IReadOnlyList<GitCommitInfo>>(ParseLog(run.StdOut), null);
    }

    public async Task<GitHistoryResult<GitCommitDetail>> ShowAsync(string repository, string sha, CancellationToken ct)
    {
        var run = await RunAsync(repository, [
            "-c", "core.quotePath=false",
            "--no-pager", "show", "--no-color", "--no-ext-diff", "--first-parent",
            // 参数里不能带真正的 NUL（Windows 会截断命令行），用 git 的 %x00 转义
            "--format=%B%x00", "--numstat", "-M", "-z", sha,
        ], ct);
        if (run.Error is { } failure)
        {
            return new GitHistoryResult<GitCommitDetail>(null, failure);
        }
        return run.ExitCode == 0
            ? new GitHistoryResult<GitCommitDetail>(ParseDetail(sha, run.StdOut), null)
            : new GitHistoryResult<GitCommitDetail>(null, Describe(run));
    }

    public async Task<GitHistoryResult<GitFileDiff>> FileDiffAsync(string repository, string sha, GitFileChange file, CancellationToken ct)
    {
        var arguments = new List<string>
        {
            "-c", "core.quotePath=false",
            "--no-pager", "show", "--no-color", "--no-ext-diff", "--no-textconv",
            "--src-prefix=a/", "--dst-prefix=b/", "--first-parent", "--format=", "--patch", "-M", sha, "--",
        };
        // 只给新路径时 git 会把重命名当成新文件：两个路径都传才能得到 rename 头
        if (file.IsRename && file.OldPath is { Length: > 0 } oldPath && !string.Equals(oldPath, file.Path, StringComparison.Ordinal))
        {
            arguments.Add(oldPath);
        }
        arguments.Add(file.Path);
        var run = await RunAsync(repository, arguments, ct);
        if (run.Error is { } failure)
        {
            return new GitHistoryResult<GitFileDiff>(null, failure);
        }
        if (run.ExitCode != 0)
        {
            return new GitHistoryResult<GitFileDiff>(null, Describe(run));
        }
        if (IsBinaryPatch(run.StdOut))
        {
            return new GitHistoryResult<GitFileDiff>(new GitFileDiff([], false, false), null);
        }
        var patch = run.StdOut;
        // git 的 patch 以 "diff --git" / "index" / "similarity index" 等元信息开头；DiffView 只显示文件内容，
        // 所以从第一个文件头（---）或块头（@@）开始解析。纯重命名 / 只改模式时没有这一段。
        var body = PatchBody(patch);
        // 纯重命名没有 hunk（numstat 也会给出 0/0），界面上只说「仅重命名」
        var renameOnly = body.Length == 0
            && patch.Contains("rename from ", StringComparison.Ordinal)
            && patch.Contains("rename to ", StringComparison.Ordinal);
        var parsed = UnifiedDiffParser.Parse(body);
        return new GitHistoryResult<GitFileDiff>(new GitFileDiff(renameOnly ? [] : parsed.Lines, parsed.Truncated, renameOnly), null);
    }

    private static IReadOnlyList<GitCommitInfo> ParseLog(string output)
    {
        var commits = new List<GitCommitInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            var fields = line.Split('\u001f', 6);
            if (fields.Length < 6)
            {
                continue;
            }
            var authoredAt = DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
                ? date
                : default;
            var parents = fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            commits.Add(new GitCommitInfo(fields[0], fields[1], fields[2], authoredAt, parents, fields[5]));
        }
        return commits;
    }

    /// <summary>
    /// 输出是 <c>&lt;正文&gt;\0</c> 后接 <c>-z</c> 的 numstat 记录：普通项 <c>added\t removed\t path\0</c>，
    /// 重命名是 <c>added\tremoved\t\0old\0new\0</c>；<c>-\t-</c> 表示二进制 / 子模块。
    /// </summary>
    private static GitCommitDetail ParseDetail(string sha, string output)
    {
        var nul = output.IndexOf('\0');
        var message = (nul < 0 ? output : output[..nul]).TrimEnd('\n', '\r');
        var files = new List<GitFileChange>();
        if (nul >= 0)
        {
            var rest = output[(nul + 1)..];
            // git 在 --format 之后还会输出一个 NUL 与换行，跳过它们再开始读 numstat
            var start = 0;
            while (start < rest.Length && rest[start] is '\0' or '\n' or '\r')
            {
                start++;
            }
            var tokens = rest[start..].Split('\0');
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token.Length == 0)
                {
                    continue;
                }
                var parts = token.Split('\t', 3);
                if (parts.Length < 3)
                {
                    continue;
                }
                var added = ParseStat(parts[0]);
                var removed = ParseStat(parts[1]);
                var binary = added is null || removed is null;
                if (parts[2].Length > 0)
                {
                    files.Add(new GitFileChange(parts[2], null, added, removed, binary, false));
                }
                else if (i + 2 < tokens.Length)
                {
                    var oldPath = tokens[i + 1];
                    var newPath = tokens[i + 2];
                    i += 2;
                    if (oldPath.Length > 0 && newPath.Length > 0)
                    {
                        files.Add(new GitFileChange(newPath, oldPath, added, removed, binary, true));
                    }
                }
            }
        }
        return new GitCommitDetail(sha, message, files);
    }

    private static int? ParseStat(string text) =>
        text == "-" ? null : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool IsBinaryPatch(string patch) =>
        patch.Contains("GIT binary patch", StringComparison.Ordinal)
        || (patch.Contains("Binary files ", StringComparison.Ordinal) && patch.Contains(" differ", StringComparison.Ordinal));

    /// <summary>patch 里属于文件内容的部分：从第一个 <c>--- </c> 或 <c>@@</c> 行开始；没有则返回空串。</summary>
    private static string PatchBody(string patch)
    {
        var index = 0;
        while (index < patch.Length)
        {
            var end = patch.IndexOf('\n', index);
            var line = end < 0 ? patch[index..] : patch[index..end];
            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal))
            {
                return patch[index..];
            }
            if (end < 0)
            {
                break;
            }
            index = end + 1;
        }
        return "";
    }

    private static string NotARepository(string repository) => $"not a git repository: {repository}";

    private static string Describe(GitRun run)
    {
        var error = run.StdErr.Trim();
        return error.Length > 0 ? error : $"git exited with code {run.ExitCode.ToString(CultureInfo.InvariantCulture)}";
    }

    private sealed record GitRun(int ExitCode, string StdOut, string StdErr, string? Error);

    /// <summary>
    /// 运行 git 并读取完整输出。超时 / 取消时杀掉整个进程树并返回错误（不抛异常），git 不在 PATH 时同样如此。
    /// </summary>
    private static async Task<GitRun> RunAsync(string repository, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        // 不弹出凭据或编辑器提示；不写 index.lock（历史面板只读）
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
            {
                return new GitRun(-1, "", "", "git not started");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DefaultTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                try
                {
                    await Task.WhenAll(stdout, stderr);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // 进程被杀后读取会失败；此时也没有可用的输出
                }
                return new GitRun(-1, "", "", ct.IsCancellationRequested ? "canceled" : "git timed out");
            }
            string outText, errorText;
            try
            {
                outText = await stdout;
                errorText = await stderr;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // 刚退出时取消 / 流被关闭：视为超时，不把异常抛给调用方
                Kill(process);
                return new GitRun(-1, "", "", ct.IsCancellationRequested ? "canceled" : "git timed out");
            }
            return new GitRun(process.ExitCode, outText, errorText, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new GitRun(-1, "", "", ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 已经退出或无权结束：忽略
        }
    }
}
