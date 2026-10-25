using System.Collections.ObjectModel;
using StreamMark.Avalonia.Transcript;
using TKCode.Desktop.Controls;
using TKCode.Desktop.ViewModels;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>会话条目到 StreamMark 会话视图条目的投影：条目不能重复（会话面板按引用建索引，重复条目在下一次同步时抛出异常）。</summary>
public sealed class TranscriptAdapterTests
{
    /// <summary>
    /// 一轮结束后，后台任务通知让会话无用户消息地继续运行：这一轮重新按进行中显示，原来的多个活动组不能取到同一个组
    /// （否则条目重复，会话面板下一次同步时抛出 “An item with the same key has already been added”）。
    /// </summary>
    [Fact]
    public async Task TurnResumedByNotification_DoesNotDuplicateGroups()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var entries = new ObservableCollection<TranscriptEntry>();
            var adapter = new TranscriptAdapter(entries);
            var session = new SessionViewModel(new SessionDto("s1", "/tmp", "m", "/tmp/s1.jsonl", new SessionOptionsDto()));
            adapter.Attach(session);
            void AssertDistinct() =>
                Assert.Equal(entries.Count, entries.Distinct(ReferenceEqualityComparer.Instance).Count());

            // 思考 + 命令 → 正文 → 两条命令 → 正文：进行中是两个活动组，结束后合成一个工作空间组
            Apply(session, new AgentStarted(), new MessageStarted("u1", "user", "扫描项目"),
                new MessageStarted("m1", "assistant", null),
                new ThinkingDelta("m1", 0, "Look at the layout first."), new ThinkingEnded("m1", 0));
            RunCommand(session, "m1", 1, "c1", "ls", "src\ntests");
            Apply(session, new TextDelta("m1", 2, "先看一下状态。"));
            RunCommand(session, "m1", 3, "c2", "git status", "clean");
            RunCommand(session, "m1", 4, "c3", "git log -1", "abc");
            Apply(session, new TextDelta("m1", 5, "都看过了。"),
                new MessageEnded("m1", "assistant", "Stop", null, null), new AgentEnded());
            AssertDistinct();

            // 后台任务完成的通知唤醒 Agent，没有新的用户消息
            Apply(session, new MessageStarted("n1", "user", "<task-notification/>", Source: "task-notification"), new AgentStarted());
            AssertDistinct();
            Apply(session, new MessageStarted("m2", "assistant", null), new TextDelta("m2", 0, "后台任务已完成。"),
                new MessageEnded("m2", "assistant", "Stop", null, null), new AgentEnded());
            AssertDistinct();
        }, TestContext.Current.CancellationToken);
    }

    private static void Apply(SessionViewModel session, params SessionEvent[] events)
    {
        foreach (var e in events)
        {
            session.Apply(e);
        }
    }

    private static void RunCommand(SessionViewModel session, string message, int index, string id, string command, string output)
    {
        var args = $$"""{"command":"{{command}}"}""";
        Apply(session, new ToolCallStarted(message, index, id, "Bash"), new ToolCallEnded(message, index, id, args),
            new ToolExecutionStarted(id, "Bash", args), new ToolExecutionEnded(id, "Bash", false, output, 420));
    }
}
