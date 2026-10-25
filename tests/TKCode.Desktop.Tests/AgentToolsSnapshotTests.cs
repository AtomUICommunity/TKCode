using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 内置 Agent 工具的界面：edit 的着色 diff、bash 的实时输出、子 Agent 进度、任务清单、
/// 后台任务通知与其他会话的消息（非用户输入，不显示为用户气泡）。
/// </summary>
public sealed class AgentToolsSnapshotTests
{
    [Fact]
    public async Task DiffOutputSubagentTasksAndNotifications_Render()
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
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            vm.Conversation.ComposerText = "你好";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "first turn finished");
            var session = vm.Conversation.Active!;

            const string diff = "--- a/src/App.cs\n+++ b/src/App.cs\n@@ -1,4 +1,4 @@\n class App\n {\n-    void Run() {}\n+    void Run() => Console.WriteLine(\"hi\");\n }\n";
            const string tasks = """{"kind":"tasks","tasks":[{"id":"1","subject":"Read the code","description":"","status":"completed"},{"id":"2","subject":"Fix Run","description":"","status":"in_progress","activeForm":"Fixing Run"},{"id":"3","subject":"Run tests","description":"","status":"pending"}]}""";
            Apply(session, new AgentStarted(), new MessageStarted("u2", "user", "修一下 Run"),
                new MessageStarted("m1", "assistant", null),
                new TextDelta("m1", 0, "我来修改 Run，并跑一下测试。"));
            Tool(session, "m1", 1, "t1", "task_create", """{"subject":"Fix Run","description":"x"}""", "Created task #2", tasks);
            Tool(session, "m1", 2, "e1", "edit", """{"path":"src/App.cs","edits":[{"oldText":"a","newText":"b"}]}""", "Replaced 1 block(s) in src/App.cs.",
                $$"""{"kind":"diff","path":"src/App.cs","diff":"{{System.Text.Json.JsonEncodedText.Encode(diff)}}","added":1,"removed":1,"firstChangedLine":3}""");
            // 运行中的命令：实时输出
            Apply(session, new ToolCallStarted("m1", 3, "b1", "bash"), new ToolCallEnded("m1", 3, "b1", """{"command":"dotnet test"}"""),
                new ToolExecutionStarted("b1", "bash", """{"command":"dotnet test"}"""),
                new ToolExecutionUpdated("b1", "bash", "Restoring…\nBuilding…\nRunning 12 tests"));
            // 运行中的子 Agent：当前动作与统计
            Apply(session, new ToolCallStarted("m1", 4, "a1", "agent"),
                new ToolCallEnded("m1", 4, "a1", """{"description":"Review the change","prompt":"..."}"""),
                new ToolExecutionStarted("a1", "agent", "{}"),
                new ToolExecutionUpdated("a1", "agent", "grep: Run(",
                    """{"kind":"subagent","agentId":"x","name":"reviewer","agentType":"explore","description":"Review","status":"running","background":false,"toolUses":7,"tokens":34567,"currentActivity":"grep: Run("}"""));
            Apply(session, new MessageStarted("n1", "user",
                "<task-notification>\n<task-id>b2</task-id>\n<type>bash</type>\n<status>completed</status>\n<description>npm run build</description>\n<result>\nExit code: 0\n</result>\n</task-notification>",
                Source: "task-notification"));
            Apply(session, new MessageStarted("n2", "user",
                "<session-message from=\"cowork-1a2b\" from-ref=\"abcd1234\">\nThe API contract changed, please regenerate the client.\n</session-message>",
                Source: "session-message"));
            await Settle();

            Assert.Equal(2, session.Items.OfType<NotificationItemViewModel>().Count());
            Assert.Single(session.Items.OfType<UserMessageItemViewModel>(), u => u.Text == "修一下 Run");
            Assert.True(session.TaskList.IsVisible);
            Assert.Equal("任务 1/3", session.TaskList.Header);

            var group = window.GetVisualDescendants().Select(v => v.DataContext).OfType<ActivityGroupViewModel>().Last();
            if (!group.IsExpanded)
            {
                group.ToggleCommand.Execute(null);
            }
            var edit = group.Steps.OfType<ToolCallBlockViewModel>().Single(s => s.ToolName == "edit");
            Assert.True(edit.HasDiff);
            Assert.Equal("+1 −1", edit.DiffStats);
            Assert.False(edit.ShowResult);
            edit.ToggleCommand.Execute(null);
            var bash = group.Steps.OfType<ToolCallBlockViewModel>().Single(s => s.ToolName == "bash");
            Assert.Contains("Running 12 tests", bash.Result);
            // 运行中的命令自动展开，显示实时输出
            Assert.True(bash.IsExpanded);
            // 子 Agent 是独立卡片（不进活动组），只显示名称、模型与后台标记（docs/design/subagent-view.md §6.2）
            Assert.DoesNotContain(group.Steps.OfType<ToolCallBlockViewModel>(), s => s.ToolName == "agent");
            var agent = Assert.Single(window.GetVisualDescendants().OfType<SubagentCardView>()).DataContext as SubagentCardViewModel;
            Assert.Equal(("reviewer", false), (agent!.DisplayName, agent.IsBackground));
            session.Items.OfType<NotificationItemViewModel>().Last().ToggleCommand.Execute(null);
            await Settle();
            HeadlessApp.Save(window, "agent-tools");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 端到端：真实 Host 中的内置 Agent 调用 task_create（Fake 模型），任务清单经 RPC 的 DetailsJson 到达界面并显示。
    /// </summary>
    [Fact]
    public async Task RealHost_TaskCreate_ShowsTaskList()
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
            vm.Conversation.ComposerText = "帮我建个任务";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false, TaskList.Items.Count: 1 }, "task created through the real host");
            var session = vm.Conversation.Active!;
            Assert.True(session.TaskList.IsVisible);
            Assert.Equal("Fake task", session.TaskList.Items[0].Subject);
            HeadlessApp.Save(window, "agent-tools-real-host");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static void Apply(SessionViewModel session, params SessionEvent[] events)
    {
        foreach (var e in events)
        {
            session.Apply(e);
        }
    }

    private static void Tool(SessionViewModel session, string message, int index, string id, string name, string args, string output, string details) =>
        Apply(session, new ToolCallStarted(message, index, id, name), new ToolCallEnded(message, index, id, args),
            new ToolExecutionStarted(id, name, args), new ToolExecutionEnded(id, name, false, output, 120, details));

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
