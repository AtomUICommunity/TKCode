using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamMark.Avalonia.Transcript;
using TKCode.Arc;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 消息操作与长会话导航（docs/design/message-features-report.md R3 / R4）：
/// 用户消息悬停操作条、会话视图的 Arc 右键菜单、⌘F 查找（正文与提问）、⌘⌥↑↓ 在提问之间跳转。
/// </summary>
public sealed class MessageNavigationTests
{
    private const int Turns = 10;

    [Fact]
    public async Task UserMessage_ShowsTimeAndCopyOnHover()
    {
        await WithConversation(async (window, session, transcript) =>
        {
            transcript.ScrollToEnd();
            await Settle();
            var last = session.Items.OfType<UserMessageItemViewModel>().Last();
            var row = window.GetVisualDescendants().OfType<StackPanel>()
                .First(p => p.Classes.Contains("user-message") && ReferenceEquals(p.DataContext, last));
            var actions = row.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("message-actions"));
            Assert.Equal(0, actions.Opacity);
            var copy = actions.GetVisualDescendants().OfType<CopyButton>().Single();
            Assert.Equal(last.CopyText, copy.Value);

            var bubble = row.GetVisualDescendants().OfType<UserMessageBubble>().Single();
            var center = bubble.TranslatePoint(new Point(bubble.Bounds.Width / 2, bubble.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            await Settle();
            Assert.Equal(1, actions.Opacity);
            HeadlessApp.Save(window, "user-message-actions");
        });
    }

    [Fact]
    public async Task ContextMenu_OpensArcMenuOverTranscript()
    {
        await WithConversation(async (window, _, transcript) =>
        {
            transcript.Panel.RaiseEvent(new ContextRequestedEventArgs());
            await Settle();
            var surface = window.GetVisualDescendants().OfType<MenuSurface>().Single(s => s.IsOpen);
            var headers = surface.Items.OfType<MenuEntry>().Select(e => e.Header as string ?? "").ToArray();
            Assert.Equal(["复制", "复制为 Markdown", "全选", "查找…"], headers);
            HeadlessApp.Save(window, "transcript-context-menu");

            // "查找…" 打开查找条
            surface.Items.OfType<MenuEntry>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuEntry.ClickEvent));
            await Settle();
            Assert.True(window.FindControl<TranscriptFindBar>("FindBar")!.IsOpen);
        });
    }

    [Fact]
    public async Task Finder_CoversAnswersAndPrompts_InConversationOrder()
    {
        await WithConversation(async (window, session, transcript) =>
        {
            var finder = new TranscriptFinder(transcript);
            // 每轮的提问与回答都含「标记」：提问在前，回答在后
            finder.Find("标记");
            Assert.Equal(Turns * 2, finder.Count);

            // 跳到第一个结果（第一轮的提问）：气泡高亮并滚到视口上方
            while (finder.Current != 0)
            {
                finder.Next();
            }
            await Settle();
            var first = session.Items.OfType<UserMessageItemViewModel>().First(u => u.DisplayText.Contains("标记"));
            Assert.True(first.IsSearchHit);
            AssertNearTop(window, transcript, first);

            // 下一个是同一轮的回答（StreamMark 选区高亮），提问的高亮随之移走
            finder.Next();
            Assert.False(first.IsSearchHit);
            Assert.Equal(0, transcript.Search.CurrentIndex);

            finder.Clear();
            Assert.Equal(0, finder.Count);
            Assert.Empty(transcript.Search.Matches);
        });
    }

    [Fact]
    public async Task FindBar_OpensWithShortcut_AndClosesWithEscape()
    {
        await WithConversation(async (window, session, _) =>
        {
            var bar = window.FindControl<TranscriptFindBar>("FindBar")!;
            var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.F, command, PhysicalKey.F, "f");
            await Settle();
            Assert.True(bar.IsOpen);

            bar.GetVisualDescendants().OfType<TextBox>().First().Text = "问题 3";
            await Task.Delay(250);
            await Settle();
            var count = bar.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "CountText");
            Assert.Equal("1/1", count.Text);
            Assert.True(session.Items.OfType<UserMessageItemViewModel>().Single(u => u.DisplayText.Contains("问题 3")).IsSearchHit);
            HeadlessApp.Save(window, "transcript-find-bar");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Settle();
            Assert.False(bar.IsOpen);
            Assert.DoesNotContain(session.Items.OfType<UserMessageItemViewModel>(), u => u.IsSearchHit);
        });
    }

    [Fact]
    public async Task JumpToPrompt_StepsBetweenPrompts()
    {
        await WithConversation(async (window, session, transcript) =>
        {
            transcript.ScrollToEnd();
            await Settle();
            var prompts = session.Items.OfType<UserMessageItemViewModel>().ToList();

            // 从底部往上：最后一条提问已在屏幕上时直接到倒数第二条（否则先到最后一条），再往上一条
            var lastVisible = transcript.Panel.Children.Any(c => ReferenceEquals(c.DataContext, prompts[^1]) &&
                                                               c.Bounds.Top >= 0 && c.Bounds.Top < transcript.Panel.Viewport.Height);
            var expected = lastVisible ? prompts.Count - 2 : prompts.Count - 1;
            Assert.True(TranscriptNavigation.JumpToPrompt(transcript, -1, item => item is UserMessageItemViewModel));
            await Settle();
            AssertNearTop(window, transcript, prompts[expected]);
            Assert.True(TranscriptNavigation.JumpToPrompt(transcript, -1, item => item is UserMessageItemViewModel));
            await Settle();
            AssertNearTop(window, transcript, prompts[expected - 1]);

            // 远距离：直接到第二条提问（只靠估算 + 逐屏移动找到它）
            Assert.True(TranscriptNavigation.ScrollToItem(transcript, prompts[1]));
            await Settle();
            AssertNearTop(window, transcript, prompts[1]);

            // 往下一条
            Assert.True(TranscriptNavigation.JumpToPrompt(transcript, 1, item => item is UserMessageItemViewModel));
            await Settle();
            AssertNearTop(window, transcript, prompts[2]);
        });
    }

    private static void AssertNearTop(Window window, TranscriptView transcript, UserMessageItemViewModel prompt)
    {
        var row = transcript.Panel.Children.FirstOrDefault(c => ReferenceEquals(c.DataContext, prompt));
        Assert.NotNull(row);
        var top = row!.TranslatePoint(default, transcript.Panel)!.Value.Y;
        Assert.InRange(top, 0, 64);
    }

    /// <summary>主窗口 + 假模型，先跑通一轮，再注入 <see cref="Turns"/> 轮较长的问答。</summary>
    private static async Task WithConversation(Func<Window, SessionViewModel, TranscriptView, Task> body)
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        var state = new StateService(Path.Combine(home, "state.json"));
        state.Current.Workspaces.Add(project);
        state.Save();

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
            var answer = string.Join("\n\n", Enumerable.Range(1, 8).Select(p => $"第 {p} 段：这是一段较长的回答，用来把会话撑过好几屏。"));
            for (var i = 1; i <= Turns; i++)
            {
                session.Apply(new AgentStarted());
                session.Apply(new MessageStarted($"u{i}", "user", $"问题 {i}：请解释标记 {i}"));
                session.Apply(new MessageStarted($"a{i}", "assistant", null));
                session.Apply(new TextDelta($"a{i}", 0, $"回答 {i} 的标记。\n\n{answer}"));
                session.Apply(new TextEnded($"a{i}", 0));
                session.Apply(new MessageEnded($"a{i}", "assistant", "Stop", null, null));
                session.Apply(new AgentEnded());
            }
            var transcript = window.GetVisualDescendants().OfType<TranscriptView>().First(t => t.Name == "Transcript");
            // 流式正文经节奏控制器输出，等全部落到文档里
            await WaitUntil(() => transcript.Search.Find("标记") >= Turns, "answers rendered", 30_000);
            transcript.Search.Clear();
            await body(window, session, transcript);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
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

    private static async Task Settle()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
