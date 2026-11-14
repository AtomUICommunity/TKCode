using System.Diagnostics;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 历史面板的数据层：在临时仓库里真的跑 git，覆盖列表分页、提交详情（增删改 / 重命名 / 二进制 / 空提交）、
/// 合并提交的 --first-parent 差异，以及统一 diff 解析（与原 DiffLinesOf 行为一致、行数上限、无换行标记）。
/// </summary>
public sealed class GitHistoryServiceTests
{
    private readonly GitHistoryService _history = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Log_Lists_Commits_Newest_First_With_Fields()
    {
        var repo = NewRepository();
        Write(repo, "a.txt", "one\n");
        var first = Commit(repo, "first");
        Write(repo, "a.txt", "two\n");
        var second = Commit(repo, "second");

        var result = await _history.LogAsync(repo, 0, 10, Ct);

        Assert.Null(result.Error);
        var commits = Assert.IsAssignableFrom<IReadOnlyList<GitCommitInfo>>(result.Value);
        Assert.Equal(2, commits.Count);
        Assert.Equal("second", commits[0].Subject);
        Assert.Equal(second, commits[0].Sha);
        Assert.True(commits[0].ShortSha.Length is >= 7 and < 40);
        Assert.StartsWith(commits[0].ShortSha, commits[0].Sha);
        Assert.Equal("t", commits[0].Author);
        var parent = Assert.Single(commits[0].Parents);
        Assert.Equal(first, parent);
        Assert.Empty(commits[1].Parents);
        Assert.Equal("first", commits[1].Subject);
        // %aI 的时区偏移被保留；时间应在刚刚
        Assert.True((DateTimeOffset.Now - commits[0].AuthoredAt).Duration() < TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Log_Pages_With_Skip_And_Max_Count()
    {
        var repo = NewRepository();
        for (var i = 0; i < 60; i++)
        {
            CommitEmpty(repo, $"c{i:D2}");
        }

        var page = await _history.LogAsync(repo, 0, 51, Ct);
        Assert.Null(page.Error);
        Assert.Equal(51, page.Value!.Count);
        Assert.Equal("c59", page.Value[0].Subject);
        Assert.Equal("c09", page.Value[^1].Subject);

        var rest = await _history.LogAsync(repo, 51, 51, Ct);
        Assert.Null(rest.Error);
        Assert.Equal(9, rest.Value!.Count);
        Assert.Equal("c08", rest.Value[0].Subject);
        Assert.Equal("c00", rest.Value[^1].Subject);

        var beyond = await _history.LogAsync(repo, 60, 51, Ct);
        Assert.Null(beyond.Error);
        Assert.Empty(beyond.Value!);
    }

    [Fact]
    public async Task Show_Returns_Message_And_Files_With_Stats()
    {
        var repo = NewRepository();
        Write(repo, "gone.txt", "bye\n");
        Write(repo, "edit.txt", "a\n");
        Write(repo, "keep.txt", new string('x', 200) + "\n");
        Commit(repo, "base");

        File.Delete(Path.Combine(repo, "gone.txt"));
        Write(repo, "edit.txt", "a\nb\n");
        Write(repo, "new.txt", "n1\nn2\n");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "mv", "keep.txt", "moved.txt");
        var sha = Commit(repo, "multi\n\nbody line");

        var result = await _history.ShowAsync(repo, sha, Ct);

        Assert.Null(result.Error);
        var detail = result.Value!;
        Assert.Equal(sha, detail.Sha);
        Assert.Equal("multi\n\nbody line", detail.Message);
        var files = detail.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        Assert.Equal(4, files.Count);

        Assert.False(files["new.txt"].IsRename);
        Assert.Null(files["new.txt"].OldPath);
        Assert.Equal(2, files["new.txt"].Added);
        Assert.Equal(0, files["new.txt"].Removed);
        Assert.False(files["new.txt"].IsBinary);

        Assert.Equal(1, files["edit.txt"].Added);
        Assert.Equal(0, files["edit.txt"].Removed);

        Assert.Equal(0, files["gone.txt"].Added);
        Assert.Equal(1, files["gone.txt"].Removed);

        Assert.True(files["moved.txt"].IsRename);
        Assert.Equal("keep.txt", files["moved.txt"].OldPath);
        Assert.Equal(0, files["moved.txt"].Added ?? -1);
        Assert.Equal(0, files["moved.txt"].Removed ?? -1);
        Assert.DoesNotContain("keep.txt", files.Keys);
    }

    [Fact]
    public async Task Show_Reports_Binary_File()
    {
        var repo = NewRepository();
        Write(repo, "text.txt", "t\n");
        Commit(repo, "base");
        File.WriteAllBytes(Path.Combine(repo, "bin.dat"), [0, 1, 2, 3, 0, 255]);
        var sha = Commit(repo, "binary");

        var result = await _history.ShowAsync(repo, sha, Ct);

        var file = Assert.Single(result.Value!.Files);
        Assert.Equal("bin.dat", file.Path);
        Assert.True(file.IsBinary);
        Assert.Null(file.Added);
        Assert.Null(file.Removed);
    }

    [Fact]
    public async Task Show_Returns_No_Files_For_Empty_Commit()
    {
        var repo = NewRepository();
        CommitEmpty(repo, "base");
        var sha = CommitEmpty(repo, "empty");

        var result = await _history.ShowAsync(repo, sha, Ct);

        Assert.Null(result.Error);
        Assert.Equal("empty", result.Value!.Message);
        Assert.Empty(result.Value.Files);
    }

    [Fact]
    public async Task Show_Uses_First_Parent_Diff_For_Merge_Commit()
    {
        var repo = NewRepository();
        Write(repo, "c.txt", "1\n");
        Commit(repo, "base");
        Git(repo, "switch", "-q", "-c", "side");
        Write(repo, "b.txt", "side\n");
        Commit(repo, "side adds b");
        Git(repo, "switch", "-q", "main");
        Write(repo, "c.txt", "2\n");
        Commit(repo, "main edits c");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "merge", "-q", "--no-ff", "-m", "merge side", "side");
        var merge = Rev(repo, "HEAD");

        var result = await _history.ShowAsync(repo, merge, Ct);

        Assert.Null(result.Error);
        var paths = result.Value!.Files.Select(f => f.Path).ToArray();
        Assert.Contains("b.txt", paths);
        // c.txt 只在第一个父提交里被改过，相对第一个父没有差异
        Assert.DoesNotContain("c.txt", paths);
    }

    [Fact]
    public async Task Show_Reports_Both_Parents_For_Merge_Commit()
    {
        var repo = NewRepository();
        Write(repo, "c.txt", "1\n");
        Commit(repo, "base");
        Git(repo, "switch", "-q", "-c", "side");
        Write(repo, "b.txt", "side\n");
        Commit(repo, "side adds b");
        Git(repo, "switch", "-q", "main");
        Write(repo, "c.txt", "2\n");
        Commit(repo, "main edits c");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "merge", "-q", "--no-ff", "-m", "merge side", "side");

        var result = await _history.LogAsync(repo, 0, 1, Ct);

        Assert.Equal(2, Assert.Single(result.Value!).Parents.Count);
    }

    [Fact]
    public async Task FileDiff_For_Add_Modify_Delete_And_Rename()
    {
        var repo = NewRepository();
        Write(repo, "gone.txt", "bye\n");
        Write(repo, "edit.txt", "a\n");
        Write(repo, "keep.txt", new string('x', 200) + "\n");
        Commit(repo, "base");
        File.Delete(Path.Combine(repo, "gone.txt"));
        Write(repo, "edit.txt", "a\nb\n");
        Write(repo, "new.txt", "n1\nn2\n");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "mv", "keep.txt", "moved.txt");
        var sha = Commit(repo, "changes");

        var detail = (await _history.ShowAsync(repo, sha, Ct)).Value!;
        var files = detail.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);

        var added = await _history.FileDiffAsync(repo, sha, files["new.txt"], Ct);
        Assert.Null(added.Error);
        Assert.False(added.Value!.Truncated);
        Assert.False(added.Value.IsRenameOnly);
        Assert.Equal(["n1", "n2"], added.Value.Lines.Where(l => l.IsAdded).Select(l => l.Text));
        Assert.Equal([1, 2], added.Value.Lines.Where(l => l.IsAdded).Select(l => l.NewNumber ?? -1));

        var modified = await _history.FileDiffAsync(repo, sha, files["edit.txt"], Ct);
        Assert.Null(modified.Error);
        Assert.Contains(modified.Value!.Lines, l => l.IsAdded && l.Text == "b");
        Assert.DoesNotContain(modified.Value.Lines, l => l.IsRemoved);

        var deleted = await _history.FileDiffAsync(repo, sha, files["gone.txt"], Ct);
        Assert.Null(deleted.Error);
        Assert.Contains(deleted.Value!.Lines, l => l.IsRemoved && l.Text == "bye");
        Assert.DoesNotContain(deleted.Value.Lines, l => l.IsAdded);

        var renamed = await _history.FileDiffAsync(repo, sha, files["moved.txt"], Ct);
        Assert.Null(renamed.Error);
        Assert.True(renamed.Value!.IsRenameOnly);
        Assert.Empty(renamed.Value.Lines);
        Assert.False(renamed.Value.Truncated);
    }

    [Fact]
    public async Task FileDiff_Handles_NonAscii_Path()
    {
        var repo = NewRepository();
        Write(repo, "base.txt", "b\n");
        Commit(repo, "base");
        Write(repo, "中文.txt", "cn\n");
        var sha = Commit(repo, "add chinese name");

        var detail = (await _history.ShowAsync(repo, sha, Ct)).Value!;
        var file = Assert.Single(detail.Files);
        Assert.Equal("中文.txt", file.Path);

        var diff = await _history.FileDiffAsync(repo, sha, file, Ct);

        Assert.Null(diff.Error);
        Assert.Contains(diff.Value!.Lines, l => l.IsAdded && l.Text == "cn");
    }

    [Fact]
    public async Task FileDiff_Ignores_No_Newline_Marker()
    {
        var repo = NewRepository();
        Write(repo, "noeol.txt", "a\nb"); // 结尾没有换行
        Commit(repo, "without newline");
        Write(repo, "noeol.txt", "a\nb\n");
        var sha = Commit(repo, "with newline");

        var detail = (await _history.ShowAsync(repo, sha, Ct)).Value!;
        var diff = await _history.FileDiffAsync(repo, sha, Assert.Single(detail.Files), Ct);

        Assert.Null(diff.Error);
        Assert.DoesNotContain(diff.Value!.Lines, l => l.Text.Contains("No newline", StringComparison.Ordinal));
        Assert.Contains(diff.Value.Lines, l => l.IsRemoved && l.Text == "b");
        Assert.Contains(diff.Value.Lines, l => l.IsAdded && l.Text == "b");
    }

    [Fact]
    public async Task Log_Returns_Empty_List_For_Unborn_Branch()
    {
        var repo = Directory.CreateTempSubdirectory("tkcode-history-").FullName;
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "config", "core.autocrlf", "false");

        var result = await _history.LogAsync(repo, 0, 50, Ct);

        Assert.Null(result.Error);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task Log_Returns_Error_For_Non_Repository()
    {
        var folder = Directory.CreateTempSubdirectory("tkcode-history-norepo-").FullName;

        var result = await _history.LogAsync(folder, 0, 50, Ct);

        Assert.NotNull(result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Repositories_Dedupes_Same_Git_Dir_And_Skips_Others()
    {
        // 两个工作目录共享同一个 git 目录（--separate-git-dir + .git 文件），只算一个仓库
        var root = Directory.CreateTempSubdirectory("tkcode-history-gitdir-").FullName;
        var gitDir = Path.Combine(root, "shared.git");
        var first = Path.Combine(root, "one");
        Git(root, "init", "-q", "-b", "main", $"--separate-git-dir={gitDir}", first);
        Git(first, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "init");
        var second = Path.Combine(root, "two");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, ".git"), $"gitdir: {gitDir}\n");
        var plain = Directory.CreateTempSubdirectory("tkcode-history-plain-").FullName;

        var repositories = GitRepository.Repositories(first, [second, plain, first]);

        Assert.Equal(first, Assert.Single(repositories));
        Assert.Empty(GitRepository.Repositories(null, []));
        Assert.Empty(GitRepository.Repositories(plain, []));
    }

    [Fact]
    public void UnifiedDiffParser_Keeps_DiffLinesOf_Output()
    {
        // 与 ChangeViewSnapshotTests 的快照同一份 fixture（那边不修改）；行号与行的种类逐行固定下来
        const string Diff = """
            --- a/src/App.cs
            +++ b/src/App.cs
            @@ -12,7 +12,8 @@
             public sealed class App
             {
            -    private readonly Clock _clock;
            +    private readonly Clock _clock = new();
            +    private readonly Logger _log;
                 public App(Clock clock)
                 {
                     _clock = clock;
            @@ -40,3 +41,3 @@
             void Run()
            -    => Start();
            +    => StartAsync();
             }
            """;

        var result = UnifiedDiffParser.Parse(Diff);

        Assert.False(result.Truncated);
        (string Text, DiffLineKind Kind, int? Old, int? New)[] expected =
        [
            ("@@ -12,7 +12,8 @@", DiffLineKind.Hunk, null, null),
            ("public sealed class App", DiffLineKind.Context, 12, 12),
            ("{", DiffLineKind.Context, 13, 13),
            ("    private readonly Clock _clock;", DiffLineKind.Removed, 14, null),
            ("    private readonly Clock _clock = new();", DiffLineKind.Added, null, 14),
            ("    private readonly Logger _log;", DiffLineKind.Added, null, 15),
            ("    public App(Clock clock)", DiffLineKind.Context, 15, 16),
            ("    {", DiffLineKind.Context, 16, 17),
            ("        _clock = clock;", DiffLineKind.Context, 17, 18),
            ("@@ -40,3 +41,3 @@", DiffLineKind.Hunk, null, null),
            ("void Run()", DiffLineKind.Context, 40, 41),
            ("    => Start();", DiffLineKind.Removed, 41, null),
            ("    => StartAsync();", DiffLineKind.Added, null, 42),
            ("}", DiffLineKind.Context, 42, 43),
        ];
        Assert.Equal(expected, result.Lines.Select(l => (l.Text, l.Kind, l.OldNumber, l.NewNumber)));
        // 工具详情继续走同一个解析器
        Assert.Equal(result.Lines.Select(l => l.Text), ParsedToolDetails.DiffLinesOf(Diff).Select(l => l.Text));
    }

    [Fact]
    public void UnifiedDiffParser_Truncates_At_Limit()
    {
        var diff = "@@ -0,0 +1,410 @@\n" + string.Concat(Enumerable.Repeat("+line\n", 410));

        var result = UnifiedDiffParser.Parse(diff, maxLines: 400);
        var small = UnifiedDiffParser.Parse("@@ -1 +1 @@\n-a\n+b\n");

        Assert.True(result.Truncated);
        Assert.Equal(401, result.Lines.Count);
        Assert.Equal("…", result.Lines[^1].Text);
        Assert.Equal(DiffLineKind.Hunk, result.Lines[^1].Kind);
        Assert.False(small.Truncated);
        Assert.Equal(3, small.Lines.Count);
        // 默认上限与工具详情的 400 行一致
        Assert.True(UnifiedDiffParser.Parse(diff).Truncated);
    }

    [Fact]
    public void UnifiedDiffParser_Skips_No_Newline_Marker()
    {
        var result = UnifiedDiffParser.Parse("@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+b\n");

        Assert.False(result.Truncated);
        Assert.Equal(["@@ -1 +1 @@", "a", "b"], result.Lines.Select(l => l.Text));
        Assert.Equal([DiffLineKind.Hunk, DiffLineKind.Removed, DiffLineKind.Added], result.Lines.Select(l => l.Kind));
    }

    private static string NewRepository()
    {
        var project = Directory.CreateTempSubdirectory("tkcode-history-").FullName;
        Git(project, "init", "-q", "-b", "main");
        // 让 diff 与工作区文本保持 LF（测试里直接写文件）
        Git(project, "config", "core.autocrlf", "false");
        return project;
    }

    private static void Write(string repository, string name, string content) =>
        File.WriteAllText(Path.Combine(repository, name), content);

    private static string Commit(string repository, string message)
    {
        Git(repository, "-c", "user.name=t", "-c", "user.email=t@example.com", "add", "-A");
        Git(repository, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--no-gpg-sign", "-m", message);
        return Rev(repository, "HEAD");
    }

    private static string CommitEmpty(string repository, string message)
    {
        Git(repository, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "--no-gpg-sign", "-m", message);
        return Rev(repository, "HEAD");
    }

    private static string Rev(string repository, string reference) => Git(repository, "rev-parse", reference).Trim();

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr.Result}{stdout.Result}");
        return stdout.Result;
    }
}
