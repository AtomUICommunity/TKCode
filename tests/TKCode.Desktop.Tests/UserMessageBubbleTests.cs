using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>用户消息气泡：太长的消息默认收起到 8 行并显示「展开」（Arc ExpandableContent），展开 / 收起不能让布局抖动或留白。</summary>
public sealed class UserMessageBubbleTests
{
    private static string Handoff() =>
        "# Astra Pet 交接文档\n\n日期：2026-10-20。这是一个用 Avalonia 重做桌面宠物显示层的验证项目，目的是确认「用 zip 里那套带透明通道的 WebM 角色，在 Avalonia 里做成透明置顶窗口」这条路走得通。结论是走得通，但 macOS 上要绕过 Avalonia 12.1.4 的一个 bug。\n\n## 1. 背景\n\n"
        + string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"第 {i} 段：这里是一些足够长的说明文字，用来把整条消息撑到远远超过八行。"));

    [Fact]
    public async Task LongMessage_CollapsesToEightLines_WithToggle()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var message = new UserMessageItemViewModel(Handoff());
            var bubble = new UserMessageBubble { DataContext = message };
            var host = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 560, Children = { bubble } };
            var window = Show(new Window { Width = 800, Height = 900, Content = host });
            await Settle();

            var collapsedHeight = bubble.Bounds.Height;
            Assert.True(Clamp(bubble).IsOverflowing, "long message should show the toggle");
            // 收起时只排到第 9 行（够判断还有没有更多），长消息不用整段排版
            Assert.Equal(CollapsedLayoutLines, bubble.FindDescendantOfType<SelectableTextBlock>()!.MaxLines);
            HeadlessApp.Save(window, "user-bubble-collapsed");

            // 窗口宽度变化后仍然收起，高度不变
            window.Width = 700;
            await Settle();
            window.Width = 800;
            await Settle();
            Assert.True(Clamp(bubble).IsOverflowing);
            Assert.Equal(CollapsedLayoutLines, bubble.FindDescendantOfType<SelectableTextBlock>()!.MaxLines);
            Assert.Equal(collapsedHeight, bubble.Bounds.Height, 0.5);

            message.IsExpanded = true;
            await Settle();
            Assert.True(Clamp(bubble).IsExpanded);
            Assert.True(Clamp(bubble).IsOverflowing);
            Assert.Equal(0, bubble.FindDescendantOfType<SelectableTextBlock>()!.MaxLines);
            Assert.True(bubble.Bounds.Height > collapsedHeight * 2);
            HeadlessApp.Save(window, "user-bubble-expanded");

            message.IsExpanded = false;
            await Settle();
            Assert.Equal(CollapsedLayoutLines, bubble.FindDescendantOfType<SelectableTextBlock>()!.MaxLines);
            Assert.Equal(collapsedHeight, bubble.Bounds.Height, 0.5);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>真实转录里：长消息之后紧跟流式的思考 / 正文，收起、展开、再收起都不能留白或与后面的内容重叠。</summary>
    [Fact]
    public async Task InTranscript_LongMessageDoesNotOverlapFollowingItems()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        var settings = new StateService(Path.Combine(home, "state.json"));
        settings.Current.Workspaces.Add(project);
        settings.Save();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            vm.Conversation.ComposerText = "你好";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "first turn finished");
            var session = vm.Conversation.Active!;

            session.Apply(new AgentStarted());
            session.Apply(new MessageStarted("u-long", "user", Handoff()));
            session.Apply(new MessageStarted("a-long", "assistant", null));
            session.Apply(new ThinkingDelta("a-long", 0, "先看一下仓库，再决定怎么改。"));
            session.Apply(new TextDelta("a-long", 1, "I'll start by checking the repo before changing anything."));
            await Settle();

            var bubble = window.GetVisualDescendants().OfType<UserMessageBubble>().Single(b => b.DataContext is UserMessageItemViewModel { Text.Length: > 500 });
            var message = (UserMessageItemViewModel)bubble.DataContext!;
            Assert.True(Clamp(bubble).IsOverflowing);
            var collapsedHeight = bubble.Bounds.Height;
            Assert.True(collapsedHeight < 260, $"collapsed bubble is {collapsedHeight}px tall");
            HeadlessApp.Save(window, "user-bubble-transcript-collapsed");

            message.IsExpanded = true;
            await Settle();
            Assert.True(Clamp(bubble).IsOverflowing, "toggle must stay available after expanding");
            Assert.True(bubble.Bounds.Height > collapsedHeight * 2);
            HeadlessApp.Save(window, "user-bubble-transcript-expanded");

            message.IsExpanded = false;
            await Settle();
            Assert.True(Clamp(bubble).IsOverflowing);
            Assert.Equal(collapsedHeight, bubble.Bounds.Height, 0.5);
            HeadlessApp.Save(window, "user-bubble-transcript-recollapsed");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShortMessage_HasNoToggle()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var bubble = new UserMessageBubble { DataContext = new UserMessageItemViewModel("你好\n再见") };
            var window = Show(new Window { Width = 800, Height = 400, Content = new StackPanel { Children = { bubble } } });
            await Settle();
            Assert.False(Clamp(bubble).IsOverflowing);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    public async Task ToggleAppearsOnlyBeyondTheLineLimit(int lines, bool toggle)
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var text = string.Join("\n", Enumerable.Range(1, lines).Select(i => $"line {i}"));
            var message = new UserMessageItemViewModel(text);
            var bubble = new UserMessageBubble { DataContext = message };
            var window = Show(new Window { Width = 800, Height = 400, Content = new StackPanel { Children = { bubble } } });
            await Settle();
            Assert.Equal(toggle, Clamp(bubble).IsOverflowing);
            Assert.Equal(toggle, Clamp(bubble).GetVisualDescendants().OfType<Avalonia.Controls.Button>().Any(b => b.IsEffectivelyVisible));
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>收起时纯文本排到的行数：折叠行数 + 1。</summary>
    private const int CollapsedLayoutLines = UserMessageItemViewModel.CollapsedLineLimit + 1;

    private static ExpandableContent Clamp(UserMessageBubble bubble) => bubble.FindDescendantOfType<ExpandableContent>()!;

    /// <summary>单独的测试窗口没有 ZoomHost：和主窗口一样关掉 Arc 动效，断言不追动画。</summary>
    private static Window Show(Window window)
    {
        ArcMotion.SetLevel(window, ArcMotionLevel.Off);
        window.Show();
        return window;
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        await Settle();
    }
}
