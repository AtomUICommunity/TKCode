using Avalonia.Threading;
using TKCode.Desktop.Localization;
using TKCode.Desktop.ViewModels;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class StreamingLifecycleTests
{
    private static SessionViewModel CreateSession() => new(
        new SessionDto("s1", "/tmp/project", "test", "/tmp/session.jsonl", new SessionOptionsDto(), "codex"));

    [Fact]
    public async Task ContentBlocks_EndWhileMessageAndToolAreStillRunning()
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var session = CreateSession();
            session.Apply(new AgentStarted());
            Assert.True(session.IsPondering);
            session.Apply(new MessageStarted("m1", "assistant", null));
            session.Apply(new ThinkingDelta("m1", 0, "Inspect the build output."));
            session.Apply(new TextDelta("m1", 7, "Running the build."));

            var message = Assert.Single(session.Items.OfType<AssistantMessageItemViewModel>());
            var thinking = Assert.IsType<ThinkingBlockViewModel>(message.Blocks[0]);
            var text = Assert.IsType<TextBlockViewModel>(message.Blocks[1]);
            Assert.True(thinking.IsStreaming);
            Assert.False(session.IsPondering);
            Assert.Equal("Thinking", thinking.Header);
            Assert.Equal(Strings.Get(TKCodeLangResourceKind.Thinking), thinking.Header);
            Assert.True(text.IsStreaming);

            session.Apply(new ThinkingEnded("m1", 0));
            Assert.False(thinking.IsStreaming);
            Assert.Equal(Strings.Get(TKCodeLangResourceKind.ThoughtProcess), thinking.Header);
            Assert.True(text.IsStreaming);

            session.Apply(new TextEnded("m1", 7));
            Assert.False(text.IsStreaming);
            session.Apply(new ToolCallStarted("m1", 8, "build", "shell"));
            session.Apply(new ToolCallEnded("m1", 8, "build", "{\"command\":\"dotnet build\"}"));
            session.Apply(new ToolExecutionStarted("build", "shell", "{\"command\":\"dotnet build\"}"));

            // 工具没有返回结果、助手消息也未结束，正文流仍应排空并关闭光标。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!text.Stream!.Document.IsComplete)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10, timeout.Token);
            }
            Assert.False(text.Stream.State.IsActive);
            Assert.Equal("Running the build.", text.Text);
            Assert.True(message.IsStreaming);
            Assert.True(session.IsRunning);
            Assert.Equal(ToolCallStatus.Running, Assert.IsType<ToolCallBlockViewModel>(message.Blocks[2]).Status);

            session.Apply(new MessageEnded("m1", "assistant", "Stop", null, null));
            session.Apply(new AgentEnded());
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BlockEnds_UseMessageOffset_AndIgnoreStaleOrEmptyBlocks()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var session = CreateSession();
            session.Apply(new MessageStarted("m1", "assistant", null));
            session.Apply(new ThinkingDelta("m1", 0, "First thought."));
            session.Apply(new TextDelta("m1", 1, "First reply."));
            session.Apply(new MessageEnded("m1", "assistant", "ToolUse", null, null));

            session.Apply(new MessageStarted("m2", "assistant", null));
            session.Apply(new ThinkingDelta("m2", 0, "Second thought."));
            session.Apply(new TextDelta("m2", 4, "Second reply."));
            var message = Assert.Single(session.Items.OfType<AssistantMessageItemViewModel>());
            var thinking = Assert.IsType<ThinkingBlockViewModel>(message.Blocks[2]);
            var text = Assert.IsType<TextBlockViewModel>(message.Blocks[3]);

            session.Apply(new ThinkingEnded("m1", 0));
            session.Apply(new TextEnded("m1", 4));
            Assert.True(thinking.IsStreaming);
            Assert.True(text.IsStreaming);

            session.Apply(new ThinkingEnded("m2", 0));
            Assert.False(thinking.IsStreaming);
            Assert.True(text.IsStreaming);
            session.Apply(new TextEnded("m2", 4));
            Assert.False(text.IsStreaming);

            // 重复结束与没有增量的空块不能新增界面条目。
            session.Apply(new ThinkingEnded("m2", 0));
            session.Apply(new TextEnded("m2", 4));
            session.Apply(new ThinkingEnded("m2", 5));
            session.Apply(new TextEnded("m2", 6));
            Assert.Equal(4, message.Blocks.Count);
            Assert.True(message.IsStreaming);
            session.Apply(new MessageEnded("m2", "assistant", "Stop", null, null));
        }, TestContext.Current.CancellationToken);
    }
}
