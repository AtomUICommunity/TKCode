using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 上下文容量卡片（docs/design/context-usage.md）：悬停卡片的打开 / 保持 / 关闭，图例与分段条的数据，
/// 以及真实 Host（Fake 模型）下从占用环悬停到卡片显示分类的完整流程（截图 context-card-*）。
/// </summary>
public sealed class ContextUsageCardTests
{
    [Fact]
    public void Compact_UsesWanAndYiInChinese_KAndMInEnglish()
    {
        var zh = CultureInfo.GetCultureInfo("zh-CN");
        var en = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal(["9,999", "2.6万", "100万", "1.5亿"], new long[] { 9_999, 26_000, 1_000_000, 150_000_000 }.Select(v => ContextUsageViewModel.Compact(v, zh)));
        Assert.Equal(["999", "26K", "272K", "18.7K", "1M"], new long[] { 999, 26_000, 272_000, 18_714, 1_000_000 }.Select(v => ContextUsageViewModel.Compact(v, en)));
    }

    [Fact]
    public async Task ViewModel_LoadsWhenOpened_SortsTheLegend_AndStacksSegmentsInContextOrder()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var requests = new List<string>();
            ContextUsageDto? next = null;
            var vm = new ContextUsageViewModel((id, _) =>
            {
                requests.Add(id);
                return Task.FromResult(next!);
            }, NullLogger.Instance);

            vm.Update("s1", 26_000, 1_000_000, nearlyFull: false);
            Assert.True(vm.HasUsage);
            Assert.Equal("3%", vm.PercentText);
            Assert.Empty(requests); // 卡片打开前不请求

            next = new ContextUsageDto(26_000, new ContextBreakdownDto(1_248, 20_514, 546, 910, 1_534, 1_248), 0.852);
            vm.IsCardOpen = true;
            await Settle();
            Assert.Equal(["s1"], requests);
            Assert.Equal(["系统工具", "消息", "系统提示词", "其他", "技能", "MCP 工具"], vm.Categories.Select(c => c.Label));
            Assert.Equal("78.9%", vm.Categories[0].Share);
            Assert.Equal("85.2%", vm.CacheHitRate);
            // 分段条按上下文的实际顺序叠放（颜色跟着类别），总长等于占用比例
            Assert.Equal(["TKCode.ContextSystemPrompt", "TKCode.ContextSystemTools", "TKCode.ContextMcpTools", "TKCode.ContextSkills",
                "TKCode.ContextMessages", "TKCode.ContextOther"], vm.Segments.Select(s => s.BrushKey));
            Assert.Equal(vm.Value, vm.Segments.Sum(s => s.Value), 6);

            // 打开期间用量变化：重新请求
            next = next with { CacheHitRate = null };
            vm.Update("s1", 30_000, 1_000_000, nearlyFull: false);
            await Settle();
            Assert.Equal(2, requests.Count);
            Assert.Equal("—", vm.CacheHitRate);

            // 外部运行时：没有分类，整段一个颜色
            next = new ContextUsageDto(30_000, null, 0.5);
            vm.IsCardOpen = false;
            vm.Update("s2", 30_000, 1_000_000, nearlyFull: true);
            Assert.False(vm.HasCategories);
            vm.IsCardOpen = true;
            await Settle();
            Assert.True(vm.IsBreakdownUnavailable);
            Assert.Equal("50.0%", vm.CacheHitRate);
            Assert.Equal(["TKCode.FileOrange"], vm.Segments.Select(s => s.BrushKey));

            // 没有用量：环隐藏，卡片关闭
            vm.Update("s2", 0, 1_000_000, nearlyFull: false);
            Assert.False(vm.HasUsage);
            Assert.False(vm.IsCardOpen);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HoverCard_OpensAfterDelay_StaysOpenInsideTheCard_AndClosesAfterLeaving()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var card = new Border { Width = 160, Height = 80, Background = Brushes.SteelBlue };
            var hover = new HoverCard
            {
                Content = new Border { Width = 60, Height = 24, Background = Brushes.Gray },
                Card = card,
                Placement = PlacementMode.TopEdgeAlignedRight,
                OpenDelay = TimeSpan.FromMilliseconds(80),
                CloseDelay = TimeSpan.FromMilliseconds(80),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 40, 40),
            };
            var window = new Avalonia.Controls.Window { Width = 400, Height = 300, Background = Brushes.Black, Content = hover };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            hover.Popup!.ShouldUseOverlayLayer = true;

            var trigger = hover.TranslatePoint(new Point(30, 12), window)!.Value;
            window.MouseMove(trigger);
            Dispatcher.UIThread.RunJobs();
            Assert.False(hover.IsOpen); // 还在延迟中
            await Wait(() => hover.IsOpen, "card opened");

            // 卡片本体贴着触发内容：右边对齐，底边在触发内容上方 Gap 处
            var cardBounds = new Rect(card.TranslatePoint(default, window)!.Value, card.Bounds.Size);
            var triggerBounds = new Rect(hover.TranslatePoint(default, window)!.Value, hover.Bounds.Size);
            Assert.Equal(triggerBounds.Right, cardBounds.Right, 1);
            Assert.Equal(triggerBounds.Top - hover.Gap, cardBounds.Bottom, 1);

            // 离开触发内容、在关闭延迟内移进卡片：保持打开
            window.MouseMove(cardBounds.Center);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(200);
            Dispatcher.UIThread.RunJobs();
            Assert.True(hover.IsOpen);

            // 离开卡片：延迟后关闭
            window.MouseMove(new Point(5, 5));
            Dispatcher.UIThread.RunJobs();
            Assert.True(hover.IsOpen);
            await Wait(() => !hover.IsOpen, "card closed");

            // 按下触发内容：立即打开
            window.MouseMove(trigger);
            window.MouseDown(trigger, MouseButton.Left);
            window.MouseUp(trigger, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(hover.IsOpen);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HoverCard_DoesNotFlicker_WhenTheCardsShadowSpaceCoversTheTrigger()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var card = new Border { Width = 160, Height = 80, Background = Brushes.SteelBlue };
            var hover = new HoverCard
            {
                Content = new Border { Width = 60, Height = 24, Background = Brushes.Gray },
                Card = card,
                Placement = PlacementMode.TopEdgeAlignedRight,
                OpenDelay = TimeSpan.FromMilliseconds(40),
                CloseDelay = TimeSpan.FromMilliseconds(40),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 40, 40),
            };
            var window = new Avalonia.Controls.Window { Width = 400, Height = 300, Background = Brushes.Black, Content = hover };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            hover.Popup!.ShouldUseOverlayLayer = true;
            var toggles = 0;
            hover.PropertyChanged += (_, e) => toggles += e.Property == HoverCard.IsOpenProperty ? 1 : 0;

            // 卡片下方的阴影留白比间距大，打开后盖住了触发内容。真实弹层是盖在上面的另一个窗口：
            // 主窗口报告指针“离开”（位置仍在触发内容里），而指针不动时弹层窗口不会报告“进入”。
            // 卡片要一直开着，不能关掉再打开（反复闪烁）
            var trigger = hover.TranslatePoint(new Point(30, 12), window)!.Value;
            window.MouseMove(trigger);
            await Wait(() => hover.IsOpen, "card opened");
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            hover.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, hover, pointer, window, trigger, 0,
                new PointerPointProperties(), KeyModifiers.None));
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            Assert.True(hover.IsOpen);

            // 同一窗口的浮层：留白盖住触发内容，指针在上面轻微晃动也不闪
            for (var i = 0; i < 10; i++)
            {
                window.MouseMove(trigger + new Point(i % 2, 0));
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(30);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.True(hover.IsOpen);
            Assert.Equal(1, toggles);

            // 移到窗口别处：关闭
            window.MouseMove(new Point(5, 5));
            await Wait(() => !hover.IsOpen, "card closed");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExternalRuntime_MessagesWithoutUsage_KeepTheLastUsage()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var session = new SessionViewModel(new SessionDto("s1", "/tmp", "claude/opus", "/tmp/session.jsonl", new SessionOptionsDto(), Runtime: "claude"));
            session.Apply(new AgentStarted());
            session.Apply(new MessageStarted("m1", "assistant", null));
            session.Apply(new MessageEnded("m1", "assistant", "Stop", null, new UsageDto(100, 10, 5_000, 0)));
            Assert.Equal(5_110, session.LastUsageTokens);

            // 外部运行时一轮中间的助手消息没有用量（全 0）：不覆盖，占用环不会消失
            session.Apply(new MessageStarted("m2", "assistant", null));
            session.Apply(new MessageEnded("m2", "assistant", "ToolUse", null, new UsageDto(0, 0, 0, 0)));
            Assert.Equal(5_110, session.LastUsageTokens);
            Assert.NotNull(session.LastUsage);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HoveringTheRing_ShowsTheContextBreakdownFromTheHost()
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
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
            window.Show();
            await Wait(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            vm.Conversation.ComposerText = "看看目录里有哪些文件";
            vm.Conversation.SendCommand.Execute(null);
            await Wait(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "conversation finished");

            var ring = window.FindControl<HoverCard>("ContextUsageCard")!;
            await Wait(() => ring.IsEffectivelyVisible, "context ring shown");
            ring.Popup!.ShouldUseOverlayLayer = true;
            ring.OpenDelay = TimeSpan.Zero;
            window.MouseMove(ring.TranslatePoint(new Point(ring.Bounds.Width / 2, ring.Bounds.Height / 2), window)!.Value);
            var usage = vm.Conversation.ContextUsage;
            await Wait(() => usage.IsCardOpen && usage.HasCategories, "breakdown loaded");

            // 内置 Agent：系统提示、内置工具与消息都有占比；比例合计约 100%
            var labels = usage.Categories.Select(c => c.Label).ToList();
            Assert.Contains(Strings.Get(TKCodeLangResourceKind.ContextCategorySystemTools), labels);
            Assert.Contains(Strings.Get(TKCodeLangResourceKind.ContextCategorySystemPrompt), labels);
            Assert.Contains(Strings.Get(TKCodeLangResourceKind.ContextCategoryMessages), labels);
            Assert.InRange(usage.Categories.Sum(c => double.Parse(c.Share.TrimEnd('%'), CultureInfo.InvariantCulture)), 99.5, 100.5);
            var card = window.GetVisualDescendants().OfType<InfoCard>().Single();
            Assert.True(card.IsEffectivelyVisible);
            Assert.Equal(usage.Categories.Count, card.GetVisualDescendants().OfType<CardStatRow>().Count() - 1);
            HeadlessApp.Save(window, "context-card-dark");

            var themes = provider.GetRequiredService<ThemeService>();
            themes.Attach(Application.Current!);
            await themes.SetModeAsync(ThemeMode.Light);
            await Settle();
            HeadlessApp.Save(window, "context-card-light");
            await themes.SetModeAsync(ThemeMode.Dark);

            // 移开：卡片关闭
            ring.CloseDelay = TimeSpan.Zero;
            window.MouseMove(new Point(500, 200));
            await Wait(() => !usage.IsCardOpen, "card closed");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static async Task Wait(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        await Settle();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
