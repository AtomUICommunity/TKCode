using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 一轮里的文件改动：工具结果的 diff 在轮次结束后汇总成卡片，撤销经宿主发出，变更面板列出整个会话的改动。
/// </summary>
public sealed class ChangeFlowTests
{
    private const string EditDetails =
        """{"kind":"diff","path":"/p/notes.txt","diff":"--- a/notes.txt\n+++ b/notes.txt\n@@ -1,1 +1,1 @@\n-one\n+two\n","added":1,"removed":1,"firstChangedLine":1,"before":"one\n","afterSha256":"abc123"}""";

    [Fact]
    public async Task TurnEnd_AddsChangeCard_AndRevertGoesThroughHost()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-change-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-change-project-").FullName;
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
            vm.Conversation.ComposerText = "你好";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "first turn finished");
            var session = vm.Conversation.Active!;

            RecordedRevert? sent = null;
            session.RevertFile = request =>
            {
                sent = new RecordedRevert(request.Path, request.ExpectedSha256, request.RestoreText);
                return Task.FromResult(new RevertFileResult(null));
            };

            session.Apply(new AgentStarted());
            session.Apply(new MessageStarted("u2", "user", "把 one 改成 two"));
            session.Apply(new MessageStarted("m1", "assistant", null));
            session.Apply(new ToolCallStarted("m1", 0, "e1", "edit"));
            session.Apply(new ToolCallEnded("m1", 0, "e1", """{"path":"/p/notes.txt"}"""));
            session.Apply(new ToolExecutionStarted("e1", "edit", """{"path":"/p/notes.txt"}"""));
            session.Apply(new ToolExecutionEnded("e1", "edit", false, "Replaced 1 block(s) in /p/notes.txt.", 12, EditDetails));
            session.Apply(new MessageEnded("m1", "assistant", "Stop", null, null));
            session.Apply(new AgentEnded());

            var card = Assert.Single(session.Items.OfType<TurnChangesItemViewModel>());
            var file = Assert.Single(card.Files);
            Assert.Equal("/p/notes.txt", file.Path);
            Assert.Equal("+1 −1", file.Stats);
            Assert.True(session.HasChanges);
            Assert.Equal("+1 −1", session.ChangesStats);

            await card.RevertAllCommand.ExecuteAsync(null);

            Assert.Equal(new RecordedRevert("/p/notes.txt", "abc123", "one\n"), sent);
            Assert.True(file.IsReverted);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PlanFileDiffs_AreNotCountedAsChanges()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-plan-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-plan-project-").FullName;
        var previous = Environment.GetEnvironmentVariable("TKCODE_HOME");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        try
        {
            await HeadlessApp.Session.Dispatch(async () =>
            {
                var session = new SessionViewModel(new SessionDto("s1", project, "m", Path.Combine(home, "s.jsonl"), new SessionOptionsDto()));
                session.RevertFile = _ => Task.FromResult(new RevertFileResult(null));

                // 第一轮只写了计划文件（~/.tkcode/plans/<会话>.md）：这是会话数据，不算工作区改动
                session.Apply(new AgentStarted());
                session.Apply(new MessageStarted("u1", "user", "写个计划"));
                session.Apply(new MessageStarted("m1", "assistant", null));
                AddDiff(session, "m1", 0, "e1", Path.Combine(home, "plans", "s.md"));
                session.Apply(new MessageEnded("m1", "assistant", "Stop", null, null));
                session.Apply(new AgentEnded());

                Assert.Empty(session.Items.OfType<TurnChangesItemViewModel>());
                Assert.False(session.HasChanges);
                Assert.Empty(session.FileChanges());

                // 第二轮改了工作区里的文件：卡片与变更面板只有它，且路径相对工作目录
                session.Apply(new AgentStarted());
                session.Apply(new MessageStarted("u2", "user", "改代码"));
                session.Apply(new MessageStarted("m2", "assistant", null));
                AddDiff(session, "m2", 0, "e2", Path.Combine(project, "a.cs"));
                session.Apply(new MessageEnded("m2", "assistant", "Stop", null, null));
                session.Apply(new AgentEnded());

                var card = Assert.Single(session.Items.OfType<TurnChangesItemViewModel>());
                Assert.Equal("a.cs", Assert.Single(card.Files).DisplayPath);
                Assert.Equal("a.cs", Assert.Single(session.FileChanges()).DisplayPath);
                Assert.True(session.HasChanges);
                return true;
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TKCODE_HOME", previous);
        }
    }

    /// <summary>一轮里的一个写入工具调用，工具结果带 diff 详情。</summary>
    private static void AddDiff(SessionViewModel session, string messageId, int index, string toolId, string path)
    {
        var quoted = System.Text.Json.JsonSerializer.Serialize(path);
        session.Apply(new ToolCallStarted(messageId, index, toolId, "write"));
        session.Apply(new ToolCallEnded(messageId, index, toolId, "{\"path\":" + quoted + "}"));
        session.Apply(new ToolExecutionStarted(toolId, "write", "{\"path\":" + quoted + "}"));
        session.Apply(new ToolExecutionEnded(toolId, "write", false, "Wrote " + path, 5,
            "{\"kind\":\"diff\",\"path\":" + quoted +
            ",\"diff\":\"--- a/x\\n+++ b/x\\n@@ -1 +1 @@\\n+x\\n\",\"added\":1,\"removed\":0,\"afterSha256\":\"h\"}"));
    }

    private sealed record RecordedRevert(string Path, string Sha, string? Restore);

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
    }
}
