using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Arc;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>运行中发送的消息进入队列：可编辑、移除、立即发送；本轮正常结束后自动发出，被停止后暂停。</summary>
public sealed class PromptQueueTests
{
    [Fact]
    public async Task QueuedPrompts_AreEditable_AndSentInOrder()
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
            var conversation = vm.Conversation;
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await WaitUntil(() => conversation.Picker.Selected is not null, "models");
            await Send(conversation, "你好");
            await WaitUntil(() => conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "first turn");
            var session = conversation.Active!;

            // 模拟 Agent 正在运行：再发送的消息进入队列，不发给 Host
            session.Apply(new AgentStarted());
            await Send(conversation, "顺便把测试也跑一下");
            await Send(conversation, "然后更新 README");
            await Send(conversation, "这条会被删掉");
            Assert.Equal(3, session.QueuedPrompts.Count);
            Assert.Equal("排队中（3）", session.QueueTitle);

            // 编辑、移除
            var first = session.QueuedPrompts[0];
            first.BeginEditCommand.Execute(null);
            first.EditText = "顺便把单元测试也跑一下";
            first.CommitEditCommand.Execute(null);
            Assert.Equal("顺便把单元测试也跑一下", first.Text);
            session.QueuedPrompts[2].RemoveCommand.Execute(null);
            Assert.Equal(2, session.QueuedPrompts.Count);
            await Settle();
            HeadlessApp.Save(window, "queue-card");

            // 长消息收起到 2 行（Arc ExpandableText），图标和按钮对齐第一行；展开后显示全部
            await Send(conversation, string.Join("", Enumerable.Repeat("把设置页里的模型列表改成按厂商分组，每组可以折叠；搜索时自动展开命中的分组。", 5)));
            await Settle();
            var longText = window.GetVisualDescendants().OfType<ExpandableText>().Single(t => t.Text?.Length > 100);
            Assert.True(longText.IsOverflowing);
            HeadlessApp.Save(window, "queue-card-long");
            longText.IsExpanded = true;
            await Settle();
            HeadlessApp.Save(window, "queue-card-long-expanded");
            session.QueuedPrompts[2].RemoveCommand.Execute(null);
            Assert.Equal(2, session.QueuedPrompts.Count);

            // 被停止：队列暂停，不自动发出
            session.Apply(new MessageStarted("x", "assistant", null));
            session.Apply(new MessageEnded("x", "assistant", "Aborted", "Request aborted", null));
            session.Apply(new AgentEnded());
            Assert.True(session.IsQueuePaused);
            Assert.Equal(2, session.QueuedPrompts.Count);
            await Settle();
            HeadlessApp.Save(window, "queue-paused");

            // 继续：发出队首，Host 真实执行一轮；该轮正常结束后自动发出下一条
            var userCount = session.Items.OfType<UserMessageItemViewModel>().Count();
            await conversation.ResumeQueueCommand.ExecuteAsync(null);
            await WaitUntil(() => session.QueuedPrompts.Count == 0 && !session.IsRunning &&
                                  session.Items.OfType<UserMessageItemViewModel>().Count() == userCount + 2, "queue drained", 30_000);
            var texts = session.Items.OfType<UserMessageItemViewModel>().Skip(userCount).Select(u => u.Text).ToArray();
            Assert.Equal(["顺便把单元测试也跑一下", "然后更新 README"], texts);
            Assert.False(session.IsQueuePaused);

            // 立即发送：空闲时直接开始新的一轮
            session.Apply(new AgentStarted());
            await Send(conversation, "立即执行这条");
            session.Apply(new MessageStarted("y", "assistant", null));
            session.Apply(new MessageEnded("y", "assistant", "Error", "boom", null));
            session.Apply(new AgentEnded());
            await session.QueuedPrompts[0].SendNowCommand.ExecuteAsync(null);
            Assert.Empty(session.QueuedPrompts);
            await WaitUntil(() => session.Items.OfType<UserMessageItemViewModel>().Any(u => u.Text == "立即执行这条") && !session.IsRunning,
                "send now", 30_000);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static async Task Send(ConversationViewModel conversation, string text)
    {
        conversation.ComposerText = text;
        await conversation.SendCommand.ExecuteAsync(null);
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
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
