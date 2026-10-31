using System.Text.Json.Nodes;
using Avalonia.Controls;
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
/// 编辑重发、回退到这里、从这里分叉（docs/design/message-rewind.md）：走真实的 Host（Fake 厂商），
/// 分叉出来的新会话只带截止点之前的历史，原会话不变；回退代码时文件真的被恢复。
/// </summary>
public sealed class MessageRewindTests
{
    [Fact]
    public async Task ForkFromReply_ContinuesInNewSession_WithOrigin()
    {
        await WithMainWindow(async (window, conversation) =>
        {
            await SendAndWaitAsync(conversation, "第一个问题");
            await SendAndWaitAsync(conversation, "第二个问题");
            var original = conversation.Active!;
            var firstReply = original.Items.OfType<AssistantMessageItemViewModel>().First();
            Assert.True(firstReply.CanFork);

            firstReply.ForkHereCommand.Execute(null);
            await WaitUntil(() => conversation.Active is { } s && !ReferenceEquals(s, original), "fork opened");
            var fork = conversation.Active!;
            Assert.Equal(["第一个问题"], Prompts(fork));
            Assert.Equal(["第一个问题", "第二个问题"], Prompts(original));
            Assert.NotNull(fork.ForkOrigin);
            Assert.Equal(original.SessionId, fork.ForkOrigin!.Origin.SessionId);

            // 顶部的「分叉自…」
            var transcript = window.GetVisualDescendants().OfType<TranscriptView>().First(t => t.Name == "Transcript");
            Assert.Contains(transcript.Entries, e => e is ContentEntry { Content: ForkOriginViewModel });
            HeadlessApp.Save(window, "fork-from-reply");

            // 点「分叉自…」回到原会话
            fork.ForkOrigin.OpenCommand.Execute(null);
            await WaitUntil(() => ReferenceEquals(conversation.Active, original), "origin opened");
        });
    }

    [Fact]
    public async Task EditAndResend_ForksBeforeTheMessage_AndSendsTheEditedText()
    {
        await WithMainWindow(async (window, conversation) =>
        {
            await SendAndWaitAsync(conversation, "第一个问题");
            await SendAndWaitAsync(conversation, "第二个问题");
            var original = conversation.Active!;
            conversation.ComposerText = "还没发的草稿";
            var second = original.Items.OfType<UserMessageItemViewModel>().Last();
            Assert.True(second.CanRewind);

            second.ActCommand.Execute(MessageAction.Edit);
            await Settle();
            Assert.True(conversation.IsEditingMessage);
            Assert.Equal("第二个问题", conversation.ComposerText);
            HeadlessApp.Save(window, "edit-message-banner");

            conversation.ComposerText = "第二个问题（改过）";
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.Active is { IsRunning: false } s && !ReferenceEquals(s, original) &&
                                  s.Items.OfType<AssistantMessageItemViewModel>().Count() >= 2, "edited turn finished");
            var fork = conversation.Active!;
            Assert.Equal(["第一个问题", "第二个问题（改过）"], Prompts(fork));
            Assert.Equal(["第一个问题", "第二个问题"], Prompts(original));
            Assert.False(conversation.IsEditingMessage);

            // 原会话的草稿还给它
            await conversation.OpenSessionCommand.ExecuteAsync(conversation.Sessions.First(s => s.SessionId == original.SessionId));
            await Settle();
            Assert.Equal("还没发的草稿", conversation.ComposerText);
        });
    }

    [Fact]
    public async Task CancelEdit_RestoresTheDraft()
    {
        await WithMainWindow(async (_, conversation) =>
        {
            await SendAndWaitAsync(conversation, "第一个问题");
            conversation.ComposerText = "写了一半";
            var first = conversation.Active!.Items.OfType<UserMessageItemViewModel>().Single();
            first.ActCommand.Execute(MessageAction.Edit);
            Assert.Equal("第一个问题", conversation.ComposerText);
            conversation.CancelEditCommand.Execute(null);
            Assert.False(conversation.IsEditingMessage);
            Assert.Equal("写了一半", conversation.ComposerText);
        });
    }

    [Fact]
    public async Task RewindCodeAndConversation_RestoresFiles_AndPrefillsThePrompt()
    {
        await WithMainWindow(async (_, conversation) =>
        {
            await SendAndWaitAsync(conversation, "你好");
            var session = conversation.Active!;
            // Agent 新建的文件：回退 = 删除
            var file = Path.Combine(session.Cwd, "notes.txt");
            var prompt = "mcp:write " + new JsonObject { ["path"] = file, ["content"] = "two\n" }.ToJsonString();
            conversation.ComposerText = prompt;
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => session.HasPendingApproval || !session.IsRunning && session.Items.OfType<TurnChangesItemViewModel>().Any(), "write approval");
            if (session.CurrentApproval is { } approval)
            {
                approval.AllowOnceCommand.Execute(null);
            }
            await WaitUntil(() => !session.IsRunning && session.Items.OfType<TurnChangesItemViewModel>().Any(), "write turn finished");
            Assert.Equal("two\n", File.ReadAllText(file));

            var writeTurn = session.Items.OfType<UserMessageItemViewModel>().Last();
            Assert.Equal(1, session.ChangedFilesFrom(writeTurn));
            conversation.ConfirmAsync = (_, _, _) => Task.FromResult(true);
            writeTurn.ActCommand.Execute(MessageAction.RewindAll);
            await WaitUntil(() => !ReferenceEquals(conversation.Active, session), "rewound into a new session");

            Assert.False(File.Exists(file));
            Assert.Equal(["你好"], Prompts(conversation.Active!));
            // 原提示回到输入框（不发送）
            Assert.Equal(prompt, conversation.ComposerText);
            // 原会话的改动卡片显示已撤销
            Assert.All(session.Items.OfType<TurnChangesItemViewModel>().SelectMany(c => c.Files), f => Assert.True(f.IsReverted));
        });
    }

    [Fact]
    public async Task RewindIsUnavailable_WhileRunning_AndBeforeCompaction()
    {
        await WithMainWindow(async (_, conversation) =>
        {
            await SendAndWaitAsync(conversation, "第一个问题");
            var session = conversation.Active!;
            var first = session.Items.OfType<UserMessageItemViewModel>().Single();
            Assert.True(first.CanRewind);

            session.Apply(new TKCode.Rpc.AgentStarted());
            Assert.False(first.CanRewind);
            session.Apply(new TKCode.Rpc.AgentEnded());
            Assert.True(first.CanRewind);

            // 压缩之后，之前的消息不能再分叉（Host 只能复制压缩之后的部分）
            session.Apply(new TKCode.Rpc.AgentStarted());
            session.Apply(new TKCode.Rpc.CompactionStarted("manual", 1000));
            session.Apply(new TKCode.Rpc.CompactionEnded("manual", 1000, 200, null));
            session.Apply(new TKCode.Rpc.AgentEnded());
            await Settle();
            Assert.False(first.CanRewind);
        });
    }

    [Fact]
    public async Task ErrorCard_RetriesWithoutNewInput()
    {
        await WithMainWindow(async (window, conversation) =>
        {
            conversation.ComposerText = "fake:fail-once";
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.Active is { IsRunning: false } s &&
                                  s.Items.OfType<AssistantMessageItemViewModel>().Any(m => m.HasError), "request failed");
            var session = conversation.Active!;
            var failed = session.Items.OfType<AssistantMessageItemViewModel>().Last();
            Assert.True(failed.IsRequestFailed);
            Assert.True(failed.CanRetry);
            Assert.Contains("503", failed.ErrorMessage);
            HeadlessApp.Save(window, "error-card-retry");

            failed.RetryCommand.Execute(null);
            await WaitUntil(() => !session.IsRunning && session.Items.OfType<AssistantMessageItemViewModel>().All(m => !m.HasError) &&
                                  session.Items.OfType<AssistantMessageItemViewModel>().Last().ShowFooter, "retry finished");
            Assert.Single(session.Items.OfType<UserMessageItemViewModel>());
            Assert.All(session.Items.OfType<AssistantMessageItemViewModel>(), m => Assert.False(m.CanRetry));
        });
    }

    [Fact]
    public async Task RetryingRequest_ShowsReasonAndCountdown_UntilOutputArrives()
    {
        await WithMainWindow(async (_, conversation) =>
        {
            await SendAndWaitAsync(conversation, "你好");
            var session = conversation.Active!;
            session.Apply(new TKCode.Rpc.AgentStarted());
            session.Apply(new TKCode.Rpc.MessageStarted("m-retry", "assistant", null));
            session.Apply(new TKCode.Rpc.RequestRetrying("m-retry", 1, 2, 8000, "429 Too Many Requests"));
            Assert.NotNull(session.RetryText);
            Assert.Contains("429 Too Many Requests", session.RetryText);
            Assert.Contains("1/2", session.RetryText);
            Assert.Matches("[78]", session.RetryText!);

            session.Apply(new TKCode.Rpc.TextDelta("m-retry", 0, "好了"));
            Assert.Null(session.RetryText);
            session.Apply(new TKCode.Rpc.TextEnded("m-retry", 0));
            session.Apply(new TKCode.Rpc.MessageEnded("m-retry", "assistant", "Stop", null, null));
            session.Apply(new TKCode.Rpc.AgentEnded());
        });
    }

    private static string[] Prompts(SessionViewModel session) =>
        session.Items.OfType<UserMessageItemViewModel>().Select(u => u.DisplayText).ToArray();

    private static async Task SendAndWaitAsync(ConversationViewModel conversation, string text)
    {
        var before = conversation.Active?.Items.OfType<AssistantMessageItemViewModel>().Count() ?? 0;
        conversation.ComposerText = text;
        await conversation.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => conversation.Active is { IsRunning: false } s &&
                              s.Items.OfType<AssistantMessageItemViewModel>().Count() > before, $"reply to {text}");
    }

    private static async Task WithMainWindow(Func<Window, ConversationViewModel, Task> body)
    {
        var home = Directory.CreateTempSubdirectory("tkcode-rewind-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-rewind-project-").FullName;
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
            await body(window, vm.Conversation);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 30_000)
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
