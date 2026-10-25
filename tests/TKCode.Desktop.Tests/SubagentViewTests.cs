using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamMark.Avalonia.Transcript;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 子 Agent 卡片与右侧转录标签（docs/design/subagent-view.md §6）：真实 Host 中的内置 Agent（Fake 模型）派生子 Agent，
/// 卡片只显示名称、模型与后台标记；点击后右侧标签用主会话的同一套模板显示它的消息。
/// </summary>
public sealed class SubagentViewTests
{
    [Fact]
    public async Task RealHost_SubagentCard_OpensTranscriptTab()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        File.WriteAllText(Path.Combine(project, "README.md"), "# demo\n");
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
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 820 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");

            vm.Conversation.ComposerText =
                """mcp:agent {"description":"look around","prompt":"List the files.","subagent_type":"explore","name":"scout"}""";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false } s && AgentCalls(s).Count == 1, "foreground subagent finished");
            vm.Conversation.ComposerText =
                """mcp:agent {"description":"in background","prompt":"Reply with a greeting.","subagent_type":"explore","name":"helper","run_in_background":true}""";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false } s && AgentCalls(s).Count == 2 && !AgentCalls(s)[1].IsBusy,
                "background subagent launched");
            var session = vm.Conversation.Active!;

            // 卡片：名称、模型、后台标记（不进活动组）
            var cards = window.GetVisualDescendants().OfType<SubagentCardView>().Select(v => (SubagentCardViewModel)v.DataContext!).ToList();
            Assert.Equal(2, cards.Count);
            Assert.Equal(("scout", false), (cards[0].DisplayName, cards[0].IsBackground));
            Assert.Equal(("helper", true), (cards[1].DisplayName, cards[1].IsBackground));
            Assert.All(cards, c => Assert.False(string.IsNullOrEmpty(c.Model)));
            HeadlessApp.Save(window, "subagent-cards");

            // 点击卡片：右侧打开它的转录标签，再次点击只激活
            cards[0].OpenCommand.Execute(null);
            var tab = Assert.IsType<SubagentTabViewModel>(vm.Workbench.ActiveTab);
            await WaitUntil(() => !tab.IsLoading, "subagent transcript loaded");
            Assert.Null(tab.Error);
            Assert.True(vm.IsWorkbenchOpen);
            Assert.Equal("scout", tab.Title);
            Assert.IsType<UserMessageItemViewModel>(tab.Transcript.Items[0]);
            Assert.Contains(tab.Transcript.Items, i => i is AssistantMessageItemViewModel);
            cards[0].OpenCommand.Execute(null);
            Assert.Single(vm.Workbench.Tabs.OfType<SubagentTabViewModel>());

            // 标签里是同一套转录视图与模板，没有输入框
            await Settle();
            var tabView = window.GetVisualDescendants().OfType<SubagentTabView>().Single();
            var transcript = tabView.GetVisualDescendants().OfType<TranscriptView>().Single();
            Assert.NotEmpty(transcript.Entries);
            Assert.Empty(tabView.GetVisualDescendants().OfType<ComposerView>());
            HeadlessApp.Save(window, "subagent-tab");

            // 后台子 Agent 结束后也能打开
            cards[1].OpenCommand.Execute(null);
            var background = Assert.IsType<SubagentTabViewModel>(vm.Workbench.ActiveTab);
            await WaitUntil(() => !background.IsLoading && !background.IsRunning && background.Transcript.Items.Count >= 2, "background transcript");
            Assert.Equal(2, vm.Workbench.Tabs.OfType<SubagentTabViewModel>().Count());

            // 切换到新对话：子 Agent 标签关闭
            vm.Conversation.NewChatCommand.Execute(null);
            await Settle();
            Assert.Empty(vm.Workbench.Tabs.OfType<SubagentTabViewModel>());
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static List<ToolCallBlockViewModel> AgentCalls(SessionViewModel session) =>
        session.Items.OfType<AssistantMessageItemViewModel>().SelectMany(m => m.Blocks).OfType<ToolCallBlockViewModel>()
            .Where(c => c.ToolName == "agent").ToList();

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
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
