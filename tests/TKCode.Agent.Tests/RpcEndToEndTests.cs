using System.Diagnostics;
using System.Threading.Channels;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Agent.Tests;

public sealed class RpcEndToEndTests
{
    [Fact]
    public async Task HostProcess_StreamsSessionEventsOverStdio()
    {
        // TKCODE_TEST_HOST_EXE 指向 NativeAOT 发布的 Host 可执行文件时，验证 AOT 版本的 RPC 链路。
        var aotHost = Environment.GetEnvironmentVariable("TKCODE_TEST_HOST_EXE");
        var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0/TKCode.AgentHost.dll"));
        var start = aotHost is { Length: > 0 } ? new ProcessStartInfo(aotHost) : new ProcessStartInfo("dotnet", $"\"{hostDll}\"");
        Assert.True(File.Exists(aotHost is { Length: > 0 } ? aotHost : hostDll));

        using var process = Process.Start(new ProcessStartInfo(start.FileName, start.Arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // 隔离数据目录，只使用 Fake 厂商，不读写用户的 ~/.tkcode
            Environment =
            {
                ["TKCODE_HOME"] = Directory.CreateTempSubdirectory("tkcode-rpc-home-").FullName,
                ["TKCODE_FAKE_PROVIDER"] = "1",
                // 不探测、不启动用户安装的外部 Agent CLI
                ["TKCODE_EXTERNAL_RUNTIMES"] = "0",
            },
        })!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();

        var client = new RecordingClient();
        await using var rpc = RpcSerialization.CreateConnection(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        AgentHostClientDispatcher.Register(rpc, client);
        var server = new AgentHostServerProxy(rpc);
        rpc.Start();

        try
        {
            var info = await server.InitializeAsync(new InitializeRequest(RpcProtocol.Version, "test"));
            Assert.Equal(RpcProtocol.Version, info.ProtocolVersion);

            var models = await server.ListModelsAsync();
            var cwd = Directory.CreateTempSubdirectory("tkcode-rpc-").FullName;
            var session = await server.CreateSessionAsync(new CreateSessionRequest(cwd, models[0].Ref));
            Assert.Equal(models[0].DefaultThinkingLevel, session.Options.ThinkingLevel);
            Assert.Equal(models[0].ContextWindows[0], session.Options.ContextWindow);
            // fake 模型不支持 fast mode：请求会被修正为 false
            // 完全授权：内置 Agent 的非只读工具也不再询问
            var options = await server.SetOptionsAsync(session.SessionId, new SessionOptionsDto("Low", FastMode: true, PermissionMode: "fullAccess"));
            Assert.Equal(new SessionOptionsDto("Low", false, models[0].ContextWindows[0], "fullAccess", "agent"), options);
            var result = await server.PromptAsync(new PromptRequest(session.SessionId, "看看目录里有哪些文件"));
            Assert.True(result.Started);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var seen = new List<SessionEvent>();
            await foreach (var n in client.Events.Reader.ReadAllAsync(timeout.Token))
            {
                seen.Add(n.Event);
                if (n.Event is AgentEnded)
                {
                    break;
                }
            }

            Assert.Contains(seen, e => e is TextDelta);
            Assert.Contains(seen, e => e is TextEnded);
            Assert.Contains(seen, e => e is ThinkingDelta);
            Assert.Contains(seen, e => e is ThinkingEnded);
            Assert.Contains(seen, e => e is ToolExecutionEnded { IsError: false, ToolName: "ls" });

            // 压缩上下文（docs/design/compaction.md §12）：作为一次运行执行，事件与摘要消息经 RPC 到达
            await server.CompactAsync(session.SessionId, "keep file names");
            var compaction = new List<SessionEvent>();
            await foreach (var n in client.Events.Reader.ReadAllAsync(timeout.Token))
            {
                compaction.Add(n.Event);
                if (n.Event is AgentEnded)
                {
                    break;
                }
            }
            Assert.Contains(compaction, e => e is CompactionStarted { Trigger: "manual" });
            Assert.Contains(compaction, e => e is CompactionEnded { Error: null, PostTokens: > 0 });
            Assert.Contains(compaction, e => e is MessageStarted { Source: "compact-summary" });
            var listed = Assert.Single(await server.ListSessionsAsync(cwd));
            Assert.Equal(session.SessionId, listed.SessionId);
            Assert.Equal("看看目录里有哪些文件", listed.Title);
            Assert.Equal(models[0].Ref, listed.ModelId);

            // 已打开的会话直接返回同一个实例，历史含工具调用与结果
            var reopened = await server.OpenSessionAsync(listed.FilePath);
            Assert.Equal(session.SessionId, reopened.Session.SessionId);
            // 压缩前的消息仍在显示用的历史中
            Assert.Contains(reopened.History, m => m.Blocks.Any(b => b is HistoryToolResult { ToolName: "ls" }));
            Assert.Contains(reopened.History, m => m.Source == "compact-summary");

            // 关闭后从文件恢复：参数沿用最后一次设置
            await server.CloseSessionAsync(session.SessionId);
            var restored = await server.OpenSessionAsync(listed.FilePath);
            Assert.Equal("Low", restored.Session.Options.ThinkingLevel);
            // 完全授权写入会话文件，重新打开后沿用
            Assert.Equal("fullAccess", restored.Session.Options.PermissionMode);
            Assert.Equal(reopened.History.Count, restored.History.Count);
            await server.CloseSessionAsync(restored.Session.SessionId);

            // 历史分页：打开时只取最近 1 轮。压缩摘要开始新的一轮，之前的 1 轮按需加载：
            // 会话开头的记忆索引 + 4 条消息 + 压缩后重新注入的记忆索引（docs/design/memory.md §4.2，界面不显示系统消息）
            var paged = await server.OpenSessionAsync(listed.FilePath, 1);
            Assert.Equal(6, paged.HistoryStart);
            Assert.Equal("compact-summary", Assert.Single(paged.History).Source);
            var earlier = await server.GetHistoryAsync(paged.Session.SessionId, paged.HistoryStart, 10);
            Assert.Equal(0, earlier.Start);
            Assert.Equal(restored.History.Count - 1, earlier.History.Count);
            await server.CloseSessionAsync(paged.Session.SessionId);

            // 会话管理：分页、重命名、置顶、在新会话中继续、删除
            var page = await server.QuerySessionsAsync(cwd, 0, 10);
            Assert.Equal(1, page.Total);
            Assert.Null(page.NextOffset);
            await server.RenameSessionAsync(listed.FilePath, "扫描目录");
            await server.PinSessionAsync(listed.FilePath, true);
            var renamed = Assert.Single((await server.QuerySessionsAsync(cwd, 0, 10)).Items);
            Assert.Equal("扫描目录", renamed.Title);
            Assert.True(renamed.HasCustomTitle);
            Assert.True(renamed.IsPinned);

            var fork = await server.ForkSessionAsync(listed.FilePath);
            Assert.NotEqual(session.SessionId, fork.Session.SessionId);
            Assert.Equal(restored.History.Count, fork.History.Count);
            Assert.Equal("fullAccess", fork.Session.Options.PermissionMode);
            await server.CloseSessionAsync(fork.Session.SessionId);
            var two = await server.QuerySessionsAsync(cwd, 0, 1);
            Assert.Equal(2, two.Total);
            Assert.Equal(1, two.NextOffset);
            Assert.True(two.Items[0].IsPinned); // 置顶在前

            await server.DeleteSessionAsync(listed.FilePath);
            Assert.False(File.Exists(listed.FilePath));
            var left = Assert.Single((await server.QuerySessionsAsync(cwd, 0, 10)).Items);
            Assert.Equal(fork.Session.SessionId, left.SessionId);
            await Assert.ThrowsAsync<RpcRemoteException>(() => server.DeleteSessionAsync("/etc/hosts"));

            // 提问（docs/design/ask-user.md §6）：input/request 的请求与回答经 RPC 往返（含 AOT 版 Host 的序列化）
            var asking = await server.CreateSessionAsync(new CreateSessionRequest(cwd, models[0].Ref));
            await server.PromptAsync(new PromptRequest(asking.SessionId,
                """mcp:ask_user {"questions":[{"question":"Which DB?","header":"Database","options":[{"label":"Postgres","description":"Full"},{"label":"SQLite","description":"Small"}]}]}"""));
            var asked = new List<SessionEvent>();
            await foreach (var n in client.Events.Reader.ReadAllAsync(timeout.Token))
            {
                if (n.SessionId != asking.SessionId)
                {
                    continue;
                }
                asked.Add(n.Event);
                if (n.Event is AgentEnded)
                {
                    break;
                }
            }
            var question = Assert.Single(client.Questions);
            Assert.Equal(("builtin", "Which DB?", 2), (question.Source, question.Questions[0].Question, question.Questions[0].Options.Count));
            var ended = Assert.Single(asked.OfType<ToolExecutionEnded>(), e => e.ToolName == "ask_user");
            Assert.Contains("\"status\":\"answered\"", ended.DetailsJson);
            Assert.Contains("\"selected\":[\"Postgres\"]", ended.DetailsJson);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    internal sealed class RecordingClient : IAgentHostClient
    {
        public Channel<SessionEventNotification> Events { get; } = Channel.CreateUnbounded<SessionEventNotification>();

        public void OnSessionEvent(SessionEventNotification notification) => Events.Writer.TryWrite(notification);

        public void OnModelsChanged(ModelsChangedNotification notification)
        {
        }

        public Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolApprovalResponse(true));

        public void OnMcpChanged(McpChangedNotification notification)
        {
        }

        public void OnAssetsChanged(AssetsChangedNotification notification)
        {
        }

        public Task<McpElicitResponse> RequestMcpElicitationAsync(McpElicitRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new McpElicitResponse("cancel"));

        public Task<McpOpenUrlResponse> OpenMcpUrlAsync(McpOpenUrlRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new McpOpenUrlResponse(false));

        public List<UserInputRequest> Questions { get; } = [];

        /// <summary>每个问题选第一个选项。</summary>
        public Task<PlanReviewResponse> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlanReviewResponse.Dismiss);

        public Task<UserInputResponse> RequestUserInputAsync(UserInputRequest request, CancellationToken cancellationToken = default)
        {
            Questions.Add(request);
            return Task.FromResult(new UserInputResponse(UserInputResponse.Answered,
                request.Questions.Select(q => new UserAnswerDto(q.Id, [q.Options[0].Label])).ToList()));
        }
    }
}
