using TKCode.Desktop.Icons;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 历史面板的 view model：仓库选择、分页、手风琴展开、懒加载的提交详情与逐文件 diff、错误与竞态。
/// 数据来源是假的 <see cref="IGitHistory"/>（不启动 git 进程），watcher 回调直接执行（不经过 Dispatcher）。
/// </summary>
public sealed class HistoryTabTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoRepositories_ShowsEmptyState_WithoutStartingGit()
    {
        var history = new FakeHistory();
        var tab = Create(history);
        try
        {
            tab.SetRepositories([]);

            await tab.LoadAsync();

            Assert.True(tab.HasNoRepository);
            Assert.False(tab.HasRepository);
            Assert.Empty(tab.Repositories);
            Assert.Empty(tab.Commits);
            Assert.Empty(history.LogCalls);
            Assert.False(tab.IsLoading);
            Assert.False(tab.IsEmpty);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task Loads_FirstPage_AndBranchText()
    {
        var repository = NewRepository("main");
        var history = new FakeHistory
        {
            Log = (_, _, _, _) => Ok([Commit("aaaa1111"), Commit("bbbb2222")]),
        };
        var tab = Create(history);
        try
        {
            tab.SetRepositories([(repository, "repo")]);

            Assert.Equal("main", tab.BranchText);
            Assert.False(tab.HasMultipleRepositories);

            await tab.LoadAsync();

            Assert.Equal(["aaaa1111", "bbbb2222"], tab.Commits.Select(c => c.Sha));
            Assert.Equal((repository, 0, 51), history.LogCalls[0]);
            Assert.False(tab.HasMore);
            Assert.False(tab.IsEmpty);
            Assert.Null(tab.Error);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task Pages_CommitList_FiftyAtATime()
    {
        var repository = NewRepository();
        var history = new FakeHistory
        {
            Log = (_, skip, _, _) => skip == 0
                ? Ok(Enumerable.Range(0, 51).Select(i => Commit($"c{i:D3}")).ToArray())
                : Ok(Enumerable.Range(51, 10).Select(i => Commit($"c{i:D3}")).ToArray()),
        };
        var tab = Create(history);
        try
        {
            tab.SetRepositories([(repository, "repo")]);
            await tab.LoadAsync();

            Assert.Equal(50, tab.Commits.Count);
            Assert.True(tab.HasMore);
            Assert.Equal("c000", tab.Commits[0].Sha);

            await tab.LoadMoreCommand.ExecuteAsync(null);

            Assert.Equal(60, tab.Commits.Count);
            Assert.False(tab.HasMore);
            Assert.False(tab.IsLoadingMore);
            Assert.Equal([(repository, 0, 51), (repository, 50, 51)], history.LogCalls);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task ExpandingCommit_LoadsDetailOnce_AndCollapsesThePrevious()
    {
        var repository = NewRepository();
        var history = new FakeHistory
        {
            Log = (_, _, _, _) => Ok([Commit("one"), Commit("two")]),
            Show = (_, sha, _) => Task.FromResult(new GitHistoryResult<GitCommitDetail>(
                new GitCommitDetail(sha, $"body of {sha}", [new GitFileChange("a.txt", null, 1, 0, false, false)]), null)),
        };
        var tab = Create(history);
        try
        {
            tab.SetRepositories([(repository, "repo")]);
            await tab.LoadAsync();
            var first = tab.Commits[0];
            var second = tab.Commits[1];

            first.ToggleExpandedCommand.Execute(null);

            Assert.True(first.IsExpanded);
            Assert.True(first.IsDetailLoaded);
            Assert.Equal("body of one", first.Message);
            Assert.True(first.HasFiles);
            var file = Assert.Single(first.Files);
            Assert.Equal("a.txt", file.Name);
            Assert.Equal("+1", file.AddedText);
            Assert.Equal("−0", file.RemovedText);
            Assert.Single(history.ShowCalls);

            second.ToggleExpandedCommand.Execute(null);

            Assert.False(first.IsExpanded);
            Assert.True(second.IsExpanded);
            Assert.Equal(2, history.ShowCalls.Count);

            // 已加载过的提交再展开：折叠第二条，不重复请求详情
            first.ToggleExpandedCommand.Execute(null);

            Assert.True(first.IsExpanded);
            Assert.False(second.IsExpanded);
            Assert.Equal(2, history.ShowCalls.Count);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task ExpandingFile_LoadsDiffOnce_AndPropagatesFlags()
    {
        var repository = NewRepository();
        var history = new FakeHistory
        {
            Log = (_, _, _, _) => Ok([Commit("one")]),
            Show = (_, sha, _) => Task.FromResult(new GitHistoryResult<GitCommitDetail>(
                new GitCommitDetail(sha, "body", [new GitFileChange("src/renamed.cs", "src/old.cs", 0, 0, false, true)]), null)),
            Diff = (_, _, _, _) => Task.FromResult(new GitHistoryResult<GitFileDiff>(
                new GitFileDiff([new DiffLineViewModel("x", DiffLineKind.Added)], Truncated: true, IsRenameOnly: true), null)),
        };
        var tab = Create(history);
        try
        {
            tab.SetRepositories([(repository, "repo")]);
            await tab.LoadAsync();
            tab.Commits[0].ToggleExpandedCommand.Execute(null);
            var file = Assert.Single(tab.Commits[0].Files);

            Assert.True(file.IsRename);
            Assert.Equal("src/old.cs", file.OldPath);
            Assert.Equal("renamed.cs", file.Name);
            Assert.Equal("src", file.Directory);
            Assert.Equal(LucideKind.FileCode, file.Icon);
            Assert.Equal("TKCode.FilePurple", file.IconColor);

            await file.ToggleExpandedCommand.ExecuteAsync(null);

            Assert.True(file.IsExpanded);
            Assert.True(file.IsRenameOnly);
            Assert.True(file.IsDiffTruncated);
            Assert.True(file.HasLines);
            Assert.Single(file.Lines);
            Assert.Single(history.DiffCalls);

            await file.ToggleExpandedCommand.ExecuteAsync(null); // 折叠
            Assert.False(file.IsExpanded);

            await file.ToggleExpandedCommand.ExecuteAsync(null); // 再展开：用缓存

            Assert.True(file.IsExpanded);
            Assert.Single(history.DiffCalls);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task BinaryFile_ShowsWithoutRequestingDiff()
    {
        var repository = NewRepository();
        var history = new FakeHistory
        {
            Log = (_, _, _, _) => Ok([Commit("one")]),
            Show = (_, sha, _) => Task.FromResult(new GitHistoryResult<GitCommitDetail>(
                new GitCommitDetail(sha, "body", [new GitFileChange("logo.png", null, null, null, true, false)]), null)),
        };
        var tab = Create(history);
        try
        {
            tab.SetRepositories([(repository, "repo")]);
            await tab.LoadAsync();
            tab.Commits[0].ToggleExpandedCommand.Execute(null);
            var file = Assert.Single(tab.Commits[0].Files);

            Assert.True(file.IsBinary);
            Assert.Equal("", file.AddedText);
            Assert.Equal("", file.RemovedText);

            await file.ToggleExpandedCommand.ExecuteAsync(null);

            Assert.True(file.IsExpanded);
            Assert.Empty(history.DiffCalls);
            Assert.False(file.HasLines);
            Assert.False(file.HasError);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task ServiceError_IsShownWithoutThrowing()
    {
        var repository = NewRepository();
        var history = new FakeHistory
        {
            Log = (_, _, _, _) => Task.FromResult(new GitHistoryResult<IReadOnlyList<GitCommitInfo>>(null, "git not found")),
        };
        var tab = Create(history);
        try
        {
            tab.SetRepositories([(repository, "repo")]);

            await tab.LoadAsync();

            Assert.Equal("git not found", tab.Error);
            Assert.True(tab.HasError);
            Assert.Empty(tab.Commits);
            Assert.False(tab.IsLoading);
            Assert.False(tab.IsEmpty);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task CommitDetailError_IsShownOnTheRow_AndCanBeRetried()
    {
        var repository = NewRepository();
        var history = new FakeHistory
        {
            Log = (_, _, _, _) => Ok([Commit("one")]),
            Show = (_, _, _) => Task.FromResult(new GitHistoryResult<GitCommitDetail>(null, "bad object")),
        };
        var tab = Create(history);
        try
        {
            tab.SetRepositories([(repository, "repo")]);
            await tab.LoadAsync();
            var commit = tab.Commits[0];

            commit.ToggleExpandedCommand.Execute(null);

            Assert.True(commit.IsExpanded);
            Assert.Equal("bad object", commit.Error);
            Assert.False(commit.IsLoading);
            Assert.False(commit.IsDetailLoaded);
            Assert.False(commit.HasFiles);
            Assert.Single(history.ShowCalls);

            // 失败没有缓存：折叠后再展开会重试，且不抛异常
            commit.ToggleExpandedCommand.Execute(null);
            commit.ToggleExpandedCommand.Execute(null);

            Assert.Equal(2, history.ShowCalls.Count);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task SetRepositories_KeepsSelection_AndProjectSwitchReloads()
    {
        var first = NewRepository("main");
        var second = NewRepository("dev");
        var history = new FakeHistory
        {
            Log = (repo, _, _, _) => Ok([Commit(repo == first ? "a" : repo == second ? "b" : "c")]),
        };
        var tab = Create(history);
        try
        {
            tab.SetRepositories([(first, "first"), (second, "second")]);

            Assert.True(tab.HasMultipleRepositories);
            Assert.Equal(first, tab.SelectedRepository!.Path);
            Assert.Equal("main", tab.BranchText);

            tab.SelectedRepository = tab.Repositories[1];

            Assert.Equal(second, tab.SelectedRepository!.Path);
            Assert.Equal("dev", tab.BranchText);

            // 顺序变化但选中的仓库仍在：保持选中
            tab.SetRepositories([(second, "second"), (first, "first")]);
            Assert.Equal(second, tab.SelectedRepository!.Path);

            tab.IsActive = true; // 激活时加载当前仓库
            var calls = history.LogCalls.Count;

            var third = NewRepository("topic");
            tab.SetRepositories([(third, "third")]);

            Assert.Single(tab.Repositories);
            Assert.Equal(third, tab.SelectedRepository!.Path);
            Assert.Equal("topic", tab.BranchText);
            Assert.True(history.LogCalls.Count > calls);
            Assert.Equal(third, history.LogCalls[^1].Repository);
            Assert.Equal("c", Assert.Single(tab.Commits).Sha);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task StaleRepositoryLoad_IsDiscarded()
    {
        var first = NewRepository("main");
        var second = NewRepository("dev");
        var gate = new TaskCompletionSource<GitHistoryResult<IReadOnlyList<GitCommitInfo>>>();
        var history = new FakeHistory
        {
            Log = (repo, _, _, _) => repo == first
                ? gate.Task
                : Task.FromResult(new GitHistoryResult<IReadOnlyList<GitCommitInfo>>([Commit("current")], null)),
        };
        var tab = Create(history);
        try
        {
            tab.SetRepositories([(first, "first")]);
            var slow = tab.LoadAsync();
            Assert.True(tab.IsLoading);

            tab.SetRepositories([(second, "second")]);
            var fast = tab.LoadAsync();
            await fast;

            Assert.Equal("current", Assert.Single(tab.Commits).Sha);

            // 旧仓库的请求这时才结束：结果必须被丢弃
            gate.SetResult(new GitHistoryResult<IReadOnlyList<GitCommitInfo>>([Commit("stale")], null));
            await slow;

            Assert.Equal("current", Assert.Single(tab.Commits).Sha);
            Assert.False(tab.IsLoading);
        }
        finally
        {
            tab.OnClosed();
        }
    }

    [Fact]
    public async Task OnClosed_DisposesWatcher_AndActivationLoadsAgain()
    {
        var repository = NewRepository("main");
        var history = new FakeHistory
        {
            Log = (_, _, _, _) => Ok([Commit("one")]),
        };
        var tab = Create(history);
        tab.SetRepositories([(repository, "repo")]);
        await tab.LoadAsync();
        tab.IsActive = true;
        Assert.False(tab.IsStale);

        tab.OnClosed();

        Assert.False(tab.IsActive);
        Assert.True(tab.IsStale);
        var calls = history.LogCalls.Count;

        // watcher 已释放：改动 HEAD 并等过去抖时间，不应再请求（也不应抛异常）
        File.WriteAllText(Path.Combine(repository, ".git", "HEAD"), "ref: refs/heads/other\n");
        await Task.Delay(600, Ct);
        Assert.Equal(calls, history.LogCalls.Count);

        // 再次打开（工作台把 IsActive 置回 true）：分支文字与列表重新读取
        tab.IsActive = true;

        Assert.True(history.LogCalls.Count > calls);
        Assert.Equal("other", tab.BranchText);
        Assert.Equal("one", Assert.Single(tab.Commits).Sha);
        tab.OnClosed();
    }

    [Fact]
    public async Task CopyHash_UsesClipboardCallback_AndSwallowsFailures()
    {
        var tab = Create(new FakeHistory());
        string? copied = null;
        tab.CopyTextAsync = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await tab.CopyHashCommand.ExecuteAsync("abc123");
        Assert.Equal("abc123", copied);

        tab.CopyTextAsync = _ => Task.FromException(new InvalidOperationException("clipboard busy"));
        await tab.CopyHashCommand.ExecuteAsync("def456"); // 剪贴板失败不抛
        tab.OnClosed();
    }

    [Fact]
    public void DateText_UsesLocalTime_AndShortensOlderYears()
    {
        var now = DateTimeOffset.Now;

        var current = new GitCommitViewModel(Commit("now", parents: 2, authoredAt: now), _ => { });
        var older = new GitCommitViewModel(Commit("old", authoredAt: now.AddYears(-2)), _ => { });

        Assert.Equal(now.LocalDateTime.ToString("MM-dd HH:mm"), current.DateText);
        Assert.Equal(now.AddYears(-2).LocalDateTime.ToString("yyyy-MM-dd"), older.DateText);
        Assert.True(current.IsMerge);
        Assert.False(older.IsMerge);
    }

    private static HistoryTabViewModel Create(FakeHistory history) => new(history, action => action());

    /// <summary>只放一个 .git 目录与 HEAD 的临时仓库：GitRepository.ReadBranch / GitLayout 能读，不需要真的跑 git。</summary>
    private static string NewRepository(string branch = "main")
    {
        var folder = Directory.CreateTempSubdirectory("tkcode-history-vm-").FullName;
        var git = Path.Combine(folder, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), $"ref: refs/heads/{branch}\n");
        return folder;
    }

    private static GitCommitInfo Commit(string sha, int parents = 1, DateTimeOffset? authoredAt = null) =>
        new(sha, sha[..Math.Min(7, sha.Length)], "tester", authoredAt ?? DateTimeOffset.Now,
            Enumerable.Range(0, parents).Select(i => $"parent{i}").ToArray(), $"subject {sha}");

    private static Task<GitHistoryResult<IReadOnlyList<GitCommitInfo>>> Ok(IReadOnlyList<GitCommitInfo> commits) =>
        Task.FromResult(new GitHistoryResult<IReadOnlyList<GitCommitInfo>>(commits, null));

    private sealed class FakeHistory : IGitHistory
    {
        public Func<string, int, int, CancellationToken, Task<GitHistoryResult<IReadOnlyList<GitCommitInfo>>>> Log { get; set; } =
            (_, _, _, _) => Task.FromResult(new GitHistoryResult<IReadOnlyList<GitCommitInfo>>([], null));

        public Func<string, string, CancellationToken, Task<GitHistoryResult<GitCommitDetail>>> Show { get; set; } =
            (_, sha, _) => Task.FromResult(new GitHistoryResult<GitCommitDetail>(new GitCommitDetail(sha, "", []), null));

        public Func<string, string, GitFileChange, CancellationToken, Task<GitHistoryResult<GitFileDiff>>> Diff { get; set; } =
            (_, _, _, _) => Task.FromResult(new GitHistoryResult<GitFileDiff>(new GitFileDiff([], false, false), null));

        public List<(string Repository, int Skip, int Count)> LogCalls { get; } = [];
        public List<(string Repository, string Sha)> ShowCalls { get; } = [];
        public List<(string Repository, string Sha, GitFileChange File)> DiffCalls { get; } = [];

        public Task<GitHistoryResult<IReadOnlyList<GitCommitInfo>>> LogAsync(string repository, int skip, int count, CancellationToken ct)
        {
            LogCalls.Add((repository, skip, count));
            return Log(repository, skip, count, ct);
        }

        public Task<GitHistoryResult<GitCommitDetail>> ShowAsync(string repository, string sha, CancellationToken ct)
        {
            ShowCalls.Add((repository, sha));
            return Show(repository, sha, ct);
        }

        public Task<GitHistoryResult<GitFileDiff>> FileDiffAsync(string repository, string sha, GitFileChange file, CancellationToken ct)
        {
            DiffCalls.Add((repository, sha, file));
            return Diff(repository, sha, file, ct);
        }
    }
}
