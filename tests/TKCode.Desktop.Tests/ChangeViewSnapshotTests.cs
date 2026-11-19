using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Desktop.Controls;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>改动的界面：统一 diff（行号、整行着色）与一轮结束的改动卡片，渲染后保存快照供检查。</summary>
public sealed class ChangeViewSnapshotTests
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
        @@ -40,3 +41,3 @@
         void Run()
        -    => Start();
        +    => StartAsync();
         }

        """;

    [Fact]
    public async Task ChangesTab_ShowsFileTypeIconsBesideEachFile()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var tab = new ChangesTabViewModel();
            var files = new[] { "/p/src/App.cs", "/p/web/index.html", "/p/docs/notes.md", "/p/data/config.json" }
                .Select(path => FileChangeSet.Group([new ParsedToolDetails { Kind = "diff", Path = path, Added = 1, DiffLines = DiffLinesOf() }],
                    _ => Task.FromResult(new RevertFileResult(null)))[0]).ToArray();
            foreach (var f in files)
            {
                tab.Files.Add(f);
            }
            files[0].IsExpanded = true;
            var view = new ChangesTabView { DataContext = tab, Width = 720, Height = 320 };
            var window = new Window { Width = 740, Height = 340, Background = Avalonia.Media.Brushes.Black, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            HeadlessApp.Save(window, "changes-tab-icons");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    private static IReadOnlyList<DiffLineViewModel> DiffLinesOf() => ParsedToolDetails.DiffLinesOf("@@ -1 +1 @@\n+x\n");

    [Fact]
    public async Task DiffAndTurnCard_RenderWithLineNumbersAndHunks()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var lines = ParsedToolDetails.DiffLinesOf(Diff);
            var diffView = new DiffView { Lines = lines, MaxContentHeight = 0, Width = 560, Height = 260 };

            var edit = new ParsedToolDetails
            {
                Kind = "diff",
                Path = "/Users/dev/project/src/App.cs",
                Added = 3,
                Removed = 2,
                AfterSha256 = "h",
                Before = "x",
                DiffLines = lines,
            };
            var second = edit with { Path = "/Users/dev/project/docs/notes.md", Added = 12, Removed = 0, DiffLines = [] };
            var turn = new TurnChangesItemViewModel(
                FileChangeSet.Group([edit, second], _ => Task.FromResult(new RevertFileResult(null))),
                _ => { }, () => { });
            var card = new TurnChangesView { DataContext = turn, Width = 560 };

            var window = new Window
            {
                Width = 600,
                Height = 560,
                Background = Avalonia.Media.Brushes.Black,
                Content = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 16, Children = { card, diffView } },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            HeadlessApp.Save(window, "change-view-diff-and-turn");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TurnCard_HoveringAFile_ShowsItsDiffInACard()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var lines = ParsedToolDetails.DiffLinesOf(Diff);
            var edit = new ParsedToolDetails
            {
                Kind = "diff",
                Path = "/Users/dev/project/src/App.cs",
                Added = 3,
                Removed = 2,
                AfterSha256 = "h",
                Before = "x",
                DiffLines = lines,
            };
            var turn = new TurnChangesItemViewModel(
                FileChangeSet.Group([edit], _ => Task.FromResult(new RevertFileResult(null)), "/Users/dev/project"),
                _ => { }, () => { });
            var card = new TurnChangesView { DataContext = turn, Width = 560 };
            var window = new Window
            {
                Width = 1000,
                Height = 700,
                Background = Avalonia.Media.Brushes.Black,
                Content = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 16, Children = { card } },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 断言文件行显示相对工作区的路径
            Assert.Contains("src/App.cs", card.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

            // 悬停弹出 diff 卡片（顶部对齐会超出窗口，截图时改为向下）。模板里的弹层走 ZoomHost。
            var hover = card.GetVisualDescendants().OfType<HoverCard>().Single();
            hover.Placement = PlacementMode.BottomEdgeAlignedLeft;
            hover.Popup!.ShouldUseOverlayLayer = true;
            hover.IsOpen = true;
            Dispatcher.UIThread.RunJobs();
            HeadlessApp.Save(window, "change-view-file-hover");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
