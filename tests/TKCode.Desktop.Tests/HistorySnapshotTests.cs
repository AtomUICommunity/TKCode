using Avalonia.Controls;
using Avalonia.Threading;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 历史面板的界面：一条展开的提交（正文、文件、一个文件展开 diff）与「不是仓库」空状态，
/// 在 520px 与 320px 两个宽度下渲染，检查窄宽度不溢出。数据来自同步的假 <see cref="IGitHistory"/>。
/// </summary>
public sealed class HistorySnapshotTests
{
    private const string Diff = """
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

        """;

    [Fact]
    public async Task HistoryTab_RendersExpandedCommitDiff_AtWideAndNarrowWidths()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var repository = NewRepository();
            var history = new FakeHistory
            {
                Commit = new GitCommitInfo("3f2a1b4c5d6e7f8091a2b3c4d5e6f708192a3b4c", "3f2a1b4", "Ada Lovelace",
                    DateTimeOffset.Now.AddHours(-3), ["parent"], "Fix the startup race"),
                Detail = new GitCommitDetail("3f2a1b4c5d6e7f8091a2b3c4d5e6f708192a3b4c",
                    "Fix the startup race\n\nWait for the window before reading the layout so the first frame is not clipped.",
                    [
                        new GitFileChange("src/App.cs", null, 2, 1, false, false),
                        new GitFileChange("src/renamed/NewName.cs", "src/old-name.cs", 0, 0, false, true),
                    ]),
                Diff = new GitFileDiff(ParsedToolDetails.DiffLinesOf(Diff), Truncated: false, IsRenameOnly: false),
            };
            var tab = new HistoryTabViewModel(history, action => action());
            try
            {
                tab.SetRepositories([(repository, "repo")]);
                await tab.LoadAsync();

                tab.Commits[0].ToggleExpandedCommand.Execute(null);
                await tab.Commits[0].Files[0].ToggleExpandedCommand.ExecuteAsync(null);

                Render(tab, 520, 640, "history-tab");
                Render(tab, 320, 640, "history-tab-narrow");
            }
            finally
            {
                tab.OnClosed();
                Directory.Delete(repository, recursive: true);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HistoryTab_ShowsNoRepositoryState()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var tab = new HistoryTabViewModel(new FakeHistory(), action => action());
            try
            {
                tab.SetRepositories([]);
                await tab.LoadAsync();

                Render(tab, 520, 640, "history-empty");
            }
            finally
            {
                tab.OnClosed();
            }
        }, TestContext.Current.CancellationToken);
    }

    private static void Render(HistoryTabViewModel tab, double width, double height, string name)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Background = Avalonia.Media.Brushes.Black,
            Content = new HistoryTabView { DataContext = tab },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        HeadlessApp.Save(window, name);
        window.Close();
    }

    /// <summary>只放一个 .git 目录与 HEAD 的临时仓库：GitRepository.ReadBranch 能读，不需要真的跑 git。</summary>
    private static string NewRepository()
    {
        var folder = Directory.CreateTempSubdirectory("tkcode-history-view-").FullName;
        var git = Path.Combine(folder, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        return folder;
    }

    /// <summary>同步返回（Task.FromResult）：展开提交后详情与文件状态在渲染前就已就绪。</summary>
    private sealed class FakeHistory : IGitHistory
    {
        public GitCommitInfo? Commit { get; init; }

        public GitCommitDetail? Detail { get; init; }

        public GitFileDiff? Diff { get; init; }

        public Task<GitHistoryResult<IReadOnlyList<GitCommitInfo>>> LogAsync(string repository, int skip, int count, CancellationToken ct) =>
            Task.FromResult(new GitHistoryResult<IReadOnlyList<GitCommitInfo>>(
                Commit is null ? [] : [Commit], null));

        public Task<GitHistoryResult<GitCommitDetail>> ShowAsync(string repository, string sha, CancellationToken ct) =>
            Task.FromResult(new GitHistoryResult<GitCommitDetail>(
                Detail ?? new GitCommitDetail(sha, "", []), null));

        public Task<GitHistoryResult<GitFileDiff>> FileDiffAsync(string repository, string sha, GitFileChange file, CancellationToken ct) =>
            Task.FromResult(new GitHistoryResult<GitFileDiff>(
                Diff ?? new GitFileDiff([], false, false), null));
    }
}
