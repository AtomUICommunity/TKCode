using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Motion;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 活动组：两段正文之间的思考与工具调用合并为一行摘要，进行中展开、结束后折叠；步骤可单独展开查看参数与输出。
/// 会话由真实 Host（Fake 模型）创建，之后向会话注入一轮多步骤的事件（模拟外部 Agent 扫描项目）。
/// </summary>
public sealed class ActivityGroupSnapshotTests
{
    [Fact]
    public async Task ThinkingAndTools_AreGroupedBetweenText_AndCollapseWhenDone()
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

            // 第二轮：思考 → 正文 → 3 条命令 → 思考 → 1 条命令 → （等待）
            Apply(session, new AgentStarted(), new MessageStarted("u2", "user", "扫描一下当前项目"),
                new MessageStarted("m1", "assistant", null),
                new ThinkingDelta("m1", 0, "The user wants a project overview. "),
                new TextDelta("m1", 1, "我先看仓库结构、技术栈和当前改动状态。"));
            RunCommand(session, "m1", 2, "c1", "git status --short && echo \"--- branch ---\" && git branch --show-current", " M src/App.cs\n?? docs/new.md\n--- branch ---\nmain");
            Apply(session, new MessageEnded("m1", "assistant", "ToolUse", null, null), new MessageStarted("m2", "assistant", null));
            RunCommand(session, "m2", 0, "c2", "echo \"--- code lines by extension ---\" && git ls-files | grep -E '\\.(cs|ts|tsx)$' | sed 's/.*\\.//' | sort | uniq -c", "  42 cs\n  17 ts");
            RunTool(session, "m2", 1, "c3", "Read", """{"file_path":"/Users/dev/cowork/README.md"}""", "# CoWork\nAI gateway");
            Apply(session, new MessageEnded("m2", "assistant", "ToolUse", null, null), new MessageStarted("m3", "assistant", null),
                new ThinkingDelta("m3", 0, "README is stale: it references global.json 10.0.301, the actual SDK is 10.0.400."));
            await Settle();
            var group = FindGroup(window);
            // 流式输出中的思考展开，结束后收起
            var pondering = group.Steps.OfType<ThinkingBlockViewModel>().Single();
            Assert.True(pondering.IsExpanded);
            Apply(session, new ThinkingEnded("m3", 0));
            Assert.False(pondering.IsExpanded);
            RunCommand(session, "m3", 1, "c4", "echo \"--- diffstat ---\" && git diff --stat", "error: unknown option", failed: true);
            // 执行中的命令展开显示实时输出；已完成的步骤都收起
            Apply(session, new ToolCallStarted("m3", 2, "c5", "Bash"), new ToolCallEnded("m3", 2, "c5", """{"command":"dotnet build"}"""),
                new ToolExecutionStarted("c5", "Bash", """{"command":"dotnet build"}"""),
                new ToolExecutionUpdated("c5", "Bash", "Restoring…\nBuilding TKCode.Desktop…"));
            await Settle();
            Assert.True(group.IsActive);
            // 进行中只显示正在进行的步骤：已完成的收起、计入摘要；执行中的命令展开显示实时输出
            Assert.False(group.IsExpanded);
            Assert.True(group.ShowSteps);
            // m1 的思考紧跟正文，单独显示；组内是 4 条命令、1 次读取与 m3 的思考
            Assert.Equal("思考过程 · 运行 4 条命令 · 读取 1 个文件", group.Summary);
            Assert.Equal(6, group.Steps.Count);
            var running = group.Steps.OfType<ToolCallBlockViewModel>().Single(s => s.IsBusy);
            Assert.True(running.IsExpanded);
            Assert.All(group.Steps.OfType<ToolCallBlockViewModel>().Where(s => !s.IsBusy), s => Assert.False(s.IsExpanded));
            var stepView = Assert.Single(window.GetVisualDescendants().OfType<ToolStepView>());
            Assert.Same(running, stepView.DataContext);
            HeadlessApp.Save(window, "activity-running");

            // 完成后整行带动画收起（连同详情），收起播完后才释放；没有正在进行的步骤时整组收起
            var stepRow = stepView.GetVisualAncestors().OfType<Reveal>().First();
            var detail = stepView.GetVisualDescendants().OfType<Reveal>().Single();
            Assert.NotNull(detail.Child);
            HeadlessApp.WithMotion(() =>
            {
                Apply(session, new ToolExecutionEnded("c5", "Bash", false, "Build succeeded.", 1800));
                Assert.False(running.IsExpanded);
                Assert.False(group.ShowSteps);
                Assert.True(stepRow.IsAnimating);
                Assert.True(detail.IsAnimating);
                Assert.NotNull(stepRow.Child);
                HeadlessApp.Step(window, 60);
                Assert.InRange(stepRow.Bounds.Height, 1, stepView.Bounds.Height - 1);
                HeadlessApp.Save(window, "activity-step-collapsing");
                MotionTicker.FinishAll();
            });
            // 整行（连同详情）一起释放
            window.UpdateLayout();
            Assert.Null(stepRow.Child);
            Assert.Empty(window.GetVisualDescendants().OfType<ToolStepView>());
            Apply(session, new MessageEnded("m3", "assistant", "ToolUse", null, null));
            await Settle();

            // 最终回答：组结束并自动折叠
            Apply(session, new MessageStarted("m4", "assistant", null),
                new TextDelta("m4", 0, "## 项目概览：CoWork\n\n**定位**：AI 模型 API 网关与计费平台。"),
                new MessageEnded("m4", "assistant", "Stop", null, new UsageDto(1200, 300, 8000, 0)), new AgentEnded());
            await Settle();
            Assert.False(group.IsActive);
            // 一轮结束：中间的思考、工具与正文收进“已工作”工作空间组，默认收起，最后一段正文留在外面。
            // 进行中的活动组就地变成工作空间组（同一行，收起有动画）
            var done = FindGroup(window);
            Assert.Same(group, done);
            Assert.True(done.IsTurnWork);
            Assert.False(done.IsExpanded);
            Assert.StartsWith("已工作 ", done.Summary);
            HeadlessApp.Save(window, "activity-done");

            // 手动展开工作空间（展开时才同步步骤），再展开一条命令与一段思考
            done.ToggleCommand.Execute(null);
            Assert.Equal(8, done.Steps.Count);
            ((ToolCallBlockViewModel)done.Steps[2]).ToggleCommand.Execute(null);
            ((ThinkingBlockViewModel)done.Steps[5]).ToggleCommand.Execute(null);
            await Settle();
            Assert.True(done.IsExpanded);
            HeadlessApp.Save(window, "activity-expanded");

            // 思考行右侧：耗时 · 估算 token 数
            var thought = (ThinkingBlockViewModel)done.Steps[5];
            Assert.Matches(@"^\d+ 秒 · 约 \d+ tokens$", thought.Stats);

            // 步骤行悬停只提亮文字，不填充背景
            var row = window.GetVisualDescendants().OfType<ToolStepView>().First()
                .GetVisualDescendants().OfType<Avalonia.Controls.Button>().First();
            ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pointerover", true);
            await Task.Delay(300);
            await Settle();
            Assert.Equal(Avalonia.Media.Colors.Transparent, ((Avalonia.Media.ISolidColorBrush)row.Background!).Color);
            Assert.True(row.TryFindResource("TKCode.TextPrimary", row.ActualThemeVariant, out var primary));
            Assert.Equal(((Avalonia.Media.ISolidColorBrush)primary!).Color,
                ((Avalonia.Media.ISolidColorBrush)row.Foreground!).Color);
            HeadlessApp.Save(window, "activity-hover");
            ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pointerover", false);

            // 向上滚动离开底部：浮出“回到底部”，点击后回到底部并隐藏
            var transcript = window.FindControl<StreamMark.Avalonia.Transcript.TranscriptView>("Transcript")!;
            var pill = window.FindControl<Avalonia.Controls.Button>("ScrollToBottomButton")!;
            transcript.Offset = new Avalonia.Vector(0, 0);
            await Settle();
            Assert.True(pill.IsVisible);
            HeadlessApp.Save(window, "activity-scroll-pill");
            pill.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Settle();
            Assert.False(pill.IsVisible);
            Assert.True(transcript.IsPinnedToBottom);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>一轮结束：进行中的活动组就地变成工作空间组，条目不被移除重建（收起动画在同一行上播放），收起期间步骤不变。</summary>
    [Fact]
    public async Task TurnEnd_CollapsesLiveGroupInPlace()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var entries = new System.Collections.ObjectModel.ObservableCollection<StreamMark.Avalonia.Transcript.TranscriptEntry>();
            var adapter = new TKCode.Desktop.Controls.TranscriptAdapter(entries);
            var session = new SessionViewModel(new SessionDto("s1", "/tmp", "m", "/tmp/s1.jsonl", new SessionOptionsDto()));
            adapter.Attach(session);
            ActivityGroupViewModel[] Groups() => entries.OfType<StreamMark.Avalonia.Transcript.ContentEntry>()
                .Select(e => e.Content).OfType<ActivityGroupViewModel>().ToArray();

            // 思考 → 正文 → 两条命令（第二条仍在运行）：组的第一个块不是这一段的第一个块
            Apply(session, new AgentStarted(), new MessageStarted("u1", "user", "扫描项目"),
                new MessageStarted("m1", "assistant", null),
                new ThinkingDelta("m1", 0, "Look at the layout first."), new ThinkingEnded("m1", 0),
                new TextDelta("m1", 1, "先看一下目录结构。"));
            RunCommand(session, "m1", 2, "c1", "ls", "src\ntests");
            Apply(session, new ToolCallStarted("m1", 3, "c2", "Bash"), new ToolCallEnded("m1", 3, "c2", """{"command":"git status"}"""),
                new ToolExecutionStarted("c2", "Bash", """{"command":"git status"}"""));
            var live = Assert.Single(Groups());
            Assert.False(live.IsExpanded);
            Assert.True(live.ShowSteps);
            Assert.Equal(2, live.Steps.Count);

            var removed = new List<StreamMark.Avalonia.Transcript.TranscriptEntry>();
            entries.CollectionChanged += (_, e) =>
            {
                if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove or System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
                {
                    removed.AddRange(e.OldItems!.OfType<StreamMark.Avalonia.Transcript.TranscriptEntry>());
                }
            };
            Apply(session, new ToolExecutionEnded("c2", "Bash", false, "clean", 300),
                new MessageEnded("m1", "assistant", "Stop", null, null), new AgentEnded());

            var done = Assert.Single(Groups());
            Assert.Same(live, done);
            Assert.True(done.IsTurnWork);
            Assert.False(done.IsExpanded);
            Assert.False(done.ShowSteps);
            Assert.DoesNotContain(removed, e => e is StreamMark.Avalonia.Transcript.ContentEntry { Content: ActivityGroupViewModel });
            // 收起期间步骤保持原样；展开时才换成整段内容（思考 + 两条命令）
            Assert.Equal(2, done.Steps.Count);
            done.ToggleCommand.Execute(null);
            Assert.Equal(3, done.Steps.Count);
            Assert.IsType<ThinkingBlockViewModel>(done.Steps[0]);
        }, TestContext.Current.CancellationToken);
    }

    private static void Apply(SessionViewModel session, params SessionEvent[] events)
    {
        foreach (var e in events)
        {
            session.Apply(e);
        }
    }

    private static void RunCommand(SessionViewModel session, string message, int index, string id, string command, string output, bool failed = false) =>
        RunTool(session, message, index, id, "Bash", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = command },
            TestJsonContext.Default.DictionaryStringString), output, failed);

    private static void RunTool(SessionViewModel session, string message, int index, string id, string name, string args, string output, bool failed = false) =>
        Apply(session, new ToolCallStarted(message, index, id, name), new ToolCallEnded(message, index, id, args),
            new ToolExecutionStarted(id, name, args), new ToolExecutionEnded(id, name, failed, output, 420));

    private static ActivityGroupViewModel FindGroup(MainWindow window) =>
        window.GetVisualDescendants().Select(v => v.DataContext).OfType<ActivityGroupViewModel>().Last();

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

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class TestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
