using System.Collections.Concurrent;
using System.Text.Json;
using TKCode.Agent;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Runtimes.Acp;
using TKCode.Runtimes.Tests.Fixtures;
using Xunit;

namespace TKCode.Runtimes.Tests;

/// <summary>docs/protocols/acp-v1.md 附录 A 的夹具。</summary>
public sealed class AcpFixtureTests
{
    private const string Spec = "acp-v1.md";
    private const string Cwd = "/Users/dev/project";

    static AcpFixtureTests() => AcpSession.ClientVersion = "0.1.0";

    private static readonly AcpAgentDefinition Agent = new("example", "Example Agent", "example", ["acp"], SilentAuthMethods: ["cursor_login"]);

    [Fact]
    public async Task A_ThinkingThenText_EndTurn()
    {
        var (agent, peer) = Create();
        var events = Record(agent);
        var run = agent.PromptAsync(UserMessage.FromText("What does the main function in src/main.rs do?"));
        // TKCode 的用户消息目前只有文本：期望的 prompt 去掉 resource_link 块
        await peer.RunAsync(TranscriptFixture.Load(Spec, "### A.1 夹具 A"), e => e.Contains("\"session/prompt\"")
            ? e.Replace(""",{"type":"resource_link","uri":"file:///Users/dev/project/src/main.rs","name":"main.rs","mimeType":"text/x-rust"}""", "")
            : e);
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        var assistant = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(StopReason.Stop, assistant.StopReason);
        Assert.Equal("The user is asking about the entry point. The attached file shows main() wiring config and server.",
            Assert.IsType<ThinkingContent>(assistant.Content[0]).Thinking);
        Assert.Equal("`main` parses the CLI arguments, loads `config.toml`, and starts the HTTP server on port 8080.",
            Assert.IsType<TextContent>(assistant.Content[1]).Text);
        Assert.Equal("sess_7f3a9c", agent.NativeSessionId);
        Assert.IsType<AgentEndEvent>(events[^1]);
        // available_commands_update：整体替换命令列表（§7.8）
        Assert.Equal([("init", "Analyze the project and create an AGENTS.md file", null), ("review", "Review pending changes", "optional focus area")],
            agent.Commands.Select(c => (c.Name, c.Description, c.ArgumentHint)));
    }

    [Fact]
    public async Task B_EditWithDiff_AllowOnce()
    {
        RuntimeApprovalRequest? asked = null;
        var (agent, peer) = Create((r, _) =>
        {
            asked = r;
            return ValueTask.FromResult(ApprovalDecision.AllowOnce);
        });
        var events = Record(agent);
        var run = agent.PromptAsync(UserMessage.FromText("Turn on debug mode in src/config.json"));
        await peer.RunAsync(TranscriptFixture.Load(Spec, "### A.2 夹具 B"));
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotNull(asked);
        Assert.Equal(ApprovalKind.FileChange, asked.Kind);
        Assert.Equal("Edit src/config.json", asked.Title);
        Assert.True(asked.CanAllowForSession);
        Assert.Contains("old_string", asked.ArgumentsJson);

        var m = agent.Messages;
        Assert.Collection(m,
            u => Assert.IsType<UserMessage>(u),
            a =>
            {
                var msg = Assert.IsType<AssistantMessage>(a);
                Assert.Equal("I'll enable debug mode in `src/config.json`.", Assert.IsType<TextContent>(msg.Content[0]).Text);
                var call = Assert.IsType<ToolCallContent>(msg.Content[1]);
                Assert.Equal("call_001", call.Id);
                Assert.Equal("Edit src/config.json", call.Name);
                Assert.Equal(StopReason.ToolUse, msg.StopReason);
            },
            r =>
            {
                var result = Assert.IsType<ToolResultMessage>(r);
                Assert.False(result.IsError);
                Assert.Equal("Edited /Users/dev/project/src/config.json", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
            },
            a => Assert.Equal("Debug mode is now enabled.", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<AssistantMessage>(a).Content)).Text));
        // diff 保留在 Details 中（completed 更新省略了 content），结构与内置 edit 相同，界面可解析并撤销
        var end = Assert.Single(events.OfType<ToolExecutionEndEvent>());
        Assert.Contains("\"kind\":\"diff\"", end.Result.Details);
        Assert.Contains("\"path\":\"/Users/dev/project/src/config.json\"", end.Result.Details);
        Assert.Single(events.OfType<ToolExecutionStartEvent>());
    }

    [Fact]
    public async Task C_PermissionRejected()
    {
        var (agent, peer) = Create((_, _) => ValueTask.FromResult(ApprovalDecision.Deny()));
        var run = agent.PromptAsync(UserMessage.FromText("Clean the build output and rebuild"));
        await peer.RunAsync(TranscriptFixture.Load(Spec, "### A.3 夹具 C"));
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        var result = agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.True(result.IsError);
        Assert.Equal("The user rejected this command.", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessage>(agent.Messages[^1]).StopReason);
    }

    [Fact]
    public async Task D_CancelWhilePermissionPending_SendsCancelBeforeReply()
    {
        ExternalAgent? self = null;
        var (agent, peer) = Create(async (_, ct) =>
        {
            self!.Abort();
            await Task.Delay(Timeout.Infinite, ct);
            return ApprovalDecision.AllowOnce;
        });
        self = agent;
        var events = Record(agent);
        var run = agent.PromptAsync(UserMessage.FromText("Run the full test suite"));
        await peer.RunAsync(TranscriptFixture.Load(Spec, "### A.4 夹具 D"));
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        var result = agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.Equal("call_003", result.ToolCallId);
        Assert.Equal("Operation aborted.", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal(StopReason.Aborted, Assert.IsType<AssistantMessage>(agent.Messages[^1]).StopReason);
        // 本地取消后，Agent 的 failed 更新不再产生生命周期事件
        Assert.Single(events.OfType<ToolExecutionEndEvent>());
    }

    [Fact]
    public async Task G_AuthRequired_SilentAuthenticateThenRetry()
    {
        var peer = new ScriptedPeer();
        var host = new RecordingHost();
        var session = new AcpSession(peer.ClientInput, peer.ClientOutput, Agent, host);
        var start = session.StartAsync(Cwd, null, CancellationToken.None);
        await peer.RunAsync(TranscriptFixture.Load(Spec, "### A.7 夹具 G"));
        await start.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("sess_c91d07", session.NativeSessionId);
        Assert.Equal(["sess_c91d07"], host.SessionIds);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task AuthRequired_WithoutSilentMethod_FailsWithLoginHint()
    {
        var peer = new ScriptedPeer();
        var session = new AcpSession(peer.ClientInput, peer.ClientOutput, Agent with { SilentAuthMethods = null }, new RecordingHost());
        var start = session.StartAsync(Cwd, null, CancellationToken.None);
        await peer.RunAsync(TranscriptFixture.Load(Spec, "### A.7 夹具 G").Take(4));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("example login", error.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConfigOptions_ModelAndThinkingAreAppliedBeforePrompt()
    {
        var lines = TranscriptFixture.Load(Spec, "### A.5 夹具 E");
        var peer = new ScriptedPeer();
        var runtime = new FixtureRuntime(peer);
        var model = RuntimeModels.Create(runtime, "flash-2");
        var agent = new ExternalAgent(runtime, model, Cwd) { ThinkingLevel = TKCode.Ai.Models.ThinkingLevel.Medium };
        var run = agent.PromptAsync(UserMessage.FromText("hi"));
        // initialize、session/new（带 config options）、set model=flash-2；响应后 thought 当前已是 medium，不再设置
        await peer.RunAsync(lines.Take(6));
        var prompt = JsonDocument.Parse((await peer.ReadExtraAsync(TimeSpan.FromSeconds(5)))!).RootElement;
        Assert.Equal("session/prompt", prompt.GetProperty("method").GetString());
        await peer.RunAsync([new TranscriptLine(false, """{"jsonrpc":"2.0","id":3,"result":{"stopReason":"end_turn"}}""")]);
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains(runtime.Started!.Config.Models, v => v.Value == "pro-1");
        Assert.Equal(["low", "medium"], runtime.Started.Config.ReasoningEfforts);
    }

    [Fact]
    public async Task EarlyNotification_IsReplayedAfterSessionResponse()
    {
        var peer = new ScriptedPeer();
        var session = new AcpSession(peer.ClientInput, peer.ClientOutput, Agent, new RecordingHost());
        var start = session.StartAsync(Cwd, null, CancellationToken.None);
        var lines = TranscriptFixture.Load(Spec, "### A.6 夹具 F").Take(5).ToList();
        // 用 config_option_update 代替命令列表，便于观察回放结果
        lines[3] = new TranscriptLine(false,
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"sess_b20e41","update":{"sessionUpdate":"config_option_update","configOptions":[{"id":"model","name":"Model","category":"model","type":"select","currentValue":"m1","options":[{"value":"m1","name":"M1"}]}]}}}""");
        await peer.RunAsync(lines);
        await start.WaitAsync(TimeSpan.FromSeconds(15));
        for (var i = 0; i < 50 && session.Config.CurrentModel is null; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal("m1", session.Config.CurrentModel);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task EarlyConfigNotification_PublishesFullModelList_AndIgnoresOtherSessions()
    {
        var peer = new ScriptedPeer();
        var session = new AcpSession(peer.ClientInput, peer.ClientOutput, Agent, new RecordingHost());
        var snapshots = RecordConfig(session);
        var options = ConfigOptions(TranscriptFixture.Load(Spec, "### A.5 夹具 E")[3]);
        await using (session)
        {
            var start = session.StartAsync(Cwd, null, CancellationToken.None);
            var lines = TranscriptFixture.Load(Spec, "### A.6 夹具 F");
            await peer.RunAsync([
                lines[0],
                new TranscriptLine(false, """{"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{}}}"""),
                lines[2],
            ]);
            // session/new 只返回 modes，没有 models/configOptions；完整列表只能由早期通知重放发布。
            await SendTogetherAsync(peer,
                ConfigUpdate("unknown-session", options.Replace("pro-2", "wrong-model", StringComparison.Ordinal)),
                ConfigUpdate("sess_b20e41", options),
                ConfigUpdate("another-session", options.Replace("pro-2", "another-wrong-model", StringComparison.Ordinal)),
                lines[4]);
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            // 已建立会话后，其他 session 的通知也不能发布事件或覆盖已有配置。
            await SendTogetherAsync(peer,
                ConfigUpdate("unknown-session", options.Replace("pro-2", "wrong-model", StringComparison.Ordinal)));
        }

        Assert.Collection(snapshots,
            response =>
            {
                Assert.Null(response.CurrentModel);
                Assert.Empty(response.Models);
                Assert.Empty(response.ReasoningEfforts);
            },
            replay =>
            {
                Assert.Equal("pro-2", replay.CurrentModel);
                Assert.Equal([
                    ("pro-2", "Pro 2", "Most capable", "recommended"),
                    ("flash-2", "Flash 2", "Fast and low cost", "recommended"),
                    ("pro-1", "Pro 1", (string?)null, "legacy"),
                ], replay.Models.Select(m => (m.Value, m.Name, m.Description, m.Group)));
                Assert.Equal(["low", "medium", "high"], replay.ReasoningEfforts);
            });
        Assert.Equal("pro-2", session.Config.CurrentModel);
        Assert.Equal(["pro-2", "flash-2", "pro-1"], session.Config.Models.Select(m => m.Value));
    }

    [Fact]
    public async Task SetConfigResponse_ThenNotification_PublishesSnapshotsInWireOrder()
    {
        var lines = TranscriptFixture.Load(Spec, "### A.5 夹具 E");
        var peer = new ScriptedPeer();
        var session = new AcpSession(peer.ClientInput, peer.ClientOutput, Agent, new RecordingHost());
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<ConfigSnapshot> snapshots;
        await using (session)
        {
            var start = session.StartAsync(Cwd, null, CancellationToken.None);
            await peer.RunAsync([
                lines[0],
                new TranscriptLine(false, """{"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{}}}"""),
                lines[2], lines[3],
            ]);
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            snapshots = RecordConfig(session, state =>
            {
                if (state.CurrentModel == "pro-2")
                {
                    entered.TrySetResult();
                    release.Wait();
                }
            });

            var model = RuntimeModels.Create(new FixtureRuntime(peer), "flash-2");
            List<Message> messages = [];
            var writer = new TurnWriter(model, _ => ValueTask.CompletedTask, messages.Add);
            var run = session.RunTurnAsync(UserMessage.FromText("hi"),
                new TurnOptions(model, ThinkingLevel.Medium, false, PermissionMode.Ask), writer, CancellationToken.None);
            try
            {
                await peer.RunAsync([lines[4], ConfigUpdate("sess_7f3a9c", ConfigOptions(lines[3]))]);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // 阻塞消费者但不阻塞 RPC 读循环；响应、更新及读取屏障在同一次 input write 中到达。
                await SendTogetherAsync(peer, lines[5], UpdatedConfig("sess_7f3a9c", lines[5]));
            }
            finally
            {
                release.Set();
            }
            await peer.RunAsync([
                new TranscriptLine(true, """{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"sess_7f3a9c","prompt":[{"type":"text","text":"hi"}]}}"""),
                new TranscriptLine(false, """{"jsonrpc":"2.0","id":3,"result":{"stopReason":"end_turn"}}"""),
            ]);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(writer.IsCompleted);
            // 此轮只返回 stopReason，没有内容块；完成轮次不必生成 assistant 消息。
            Assert.Empty(messages);
        }

        AssertConfigResponseThenNotification(snapshots.ToArray(), session);
    }

    [Theory]
    [InlineData("session/load", """{"loadSession":true}""")]
    [InlineData("session/resume", """{"sessionCapabilities":{"resume":{}}}""")]
    public async Task OpenConfigResponse_ThenNotification_PublishesSnapshotsInWireOrder(string method, string capabilities)
    {
        var lines = TranscriptFixture.Load(Spec, "### A.5 夹具 E");
        var peer = new ScriptedPeer();
        var session = new AcpSession(peer.ClientInput, peer.ClientOutput, Agent, new RecordingHost());
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = RecordConfig(session, state =>
        {
            if (state.CurrentModel == "pro-2")
            {
                entered.TrySetResult();
                release.Wait();
            }
        });
        await using (session)
        {
            var start = session.StartAsync(Cwd, "sess_7f3a9c", CancellationToken.None);
            try
            {
                await peer.RunAsync([
                    lines[0],
                    new TranscriptLine(false, $$$$"""{"jsonrpc":"2.0","id":0,"result":{"protocolVersion":1,"agentCapabilities":{{{{capabilities}}}}}}"""),
                    new TranscriptLine(true, $$$$"""{"jsonrpc":"2.0","id":1,"method":"{{{{method}}}}","params":{"sessionId":"sess_7f3a9c","cwd":"/Users/dev/project","mcpServers":[]}}"""),
                    ConfigUpdate("sess_7f3a9c", ConfigOptions(lines[3])),
                ]);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var response = lines[5] with { Json = lines[5].Json.Replace("\"id\":2", "\"id\":1", StringComparison.Ordinal) };
                await SendTogetherAsync(peer, response, UpdatedConfig("sess_7f3a9c", response));
            }
            finally
            {
                release.Set();
            }
            await start.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal("sess_7f3a9c", session.NativeSessionId);
        AssertConfigResponseThenNotification(snapshots.ToArray(), session);
    }

    [Fact]
    public async Task EarlyCommandsUpdate_ReachesHostAfterSessionResponse()
    {
        var peer = new ScriptedPeer();
        var host = new RecordingHost();
        var session = new AcpSession(peer.ClientInput, peer.ClientOutput, Agent, host);
        var start = session.StartAsync(Cwd, null, CancellationToken.None);
        await peer.RunAsync(TranscriptFixture.Load(Spec, "### A.6 夹具 F").Take(5));
        await start.WaitAsync(TimeSpan.FromSeconds(15));
        for (var i = 0; i < 50 && host.Commands is null; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal("compress", Assert.Single(host.Commands!).Name);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task UnknownAgentRequests_GetMethodNotFound_AndElicitationInvalidParams()
    {
        var peer = new ScriptedPeer();
        var host = new RecordingHost();
        var session = new AcpSession(peer.ClientInput, peer.ClientOutput, Agent, host);
        var start = session.StartAsync(Cwd, null, CancellationToken.None);
        await peer.RunAsync(TranscriptFixture.Load(Spec, "### A.1 夹具 A").Take(4));
        await start.WaitAsync(TimeSpan.FromSeconds(15));
        await peer.RunAsync([
            new TranscriptLine(false, """{"jsonrpc":"2.0","id":7,"method":"fs/read_text_file","params":{"sessionId":"sess_7f3a9c","path":"/x"}}"""),
            new TranscriptLine(true, """{"jsonrpc":"2.0","id":7,"error":{"code":-32601,"message":"Method not found"}}"""),
            // 只声明了 form：url 模式按规范回复 -32602
            new TranscriptLine(false, """{"jsonrpc":"2.0","id":"e1","method":"elicitation/create","params":{"sessionId":"sess_7f3a9c","mode":"url","elicitationId":"x","url":"https://example.com","message":"Sign in"}}"""),
            new TranscriptLine(true, """{"jsonrpc":"2.0","id":"e1","error":{"code":-32602,"message":"elicitation mode not supported"}}"""),
            // form 模式交给宿主的表单卡片（docs/design/ask-user.md §8）
            new TranscriptLine(false, """{"jsonrpc":"2.0","id":"e2","method":"elicitation/create","params":{"sessionId":"sess_7f3a9c","mode":"form","message":"How should I approach this?","requestedSchema":{"type":"object","properties":{"strategy":{"type":"string","enum":["conservative","balanced"]}},"required":["strategy"]}}}"""),
            new TranscriptLine(true, """{"jsonrpc":"2.0","id":"e2","result":{"action":"accept","content":{"strategy":"balanced"}}}"""),
        ]);
        Assert.Equal("How should I approach this?", host.Elicitation?.Message);
        Assert.Contains("\"conservative\"", host.Elicitation?.SchemaJson);
        await session.DisposeAsync();
    }

    private sealed record ConfigSnapshot(string? CurrentModel, AcpConfigValue[] Models, string[] ReasoningEfforts);

    private static ConcurrentQueue<ConfigSnapshot> RecordConfig(AcpSession session, Action<AcpConfigState>? afterSnapshot = null)
    {
        var snapshots = new ConcurrentQueue<ConfigSnapshot>();
        session.ConfigChanged += state =>
        {
            // AcpConfigState 是可变对象，必须在事件回调内复制，不能事后读取同一个引用。
            snapshots.Enqueue(new ConfigSnapshot(state.CurrentModel, state.Models.ToArray(), state.ReasoningEfforts.ToArray()));
            afterSnapshot?.Invoke(state);
        };
        return snapshots;
    }

    private static string ConfigOptions(TranscriptLine response)
    {
        using var json = JsonDocument.Parse(response.Json);
        return json.RootElement.GetProperty("result").GetProperty("configOptions").GetRawText();
    }

    private static TranscriptLine ConfigUpdate(string sessionId, string options) => new(false,
        $$$$"""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"{{{{sessionId}}}}","update":{"sessionUpdate":"config_option_update","configOptions":{{{{options}}}}}}}""");

    private static TranscriptLine UpdatedConfig(string sessionId, TranscriptLine response) => ConfigUpdate(sessionId,
        ConfigOptions(response)
            .Replace("\"name\":\"Flash 2\"", "\"name\":\"Flash 2 Updated\"", StringComparison.Ordinal)
            .Replace("Fast and low cost", "New metadata from notification", StringComparison.Ordinal)
            .Replace("[{\"value\":\"low\",\"name\":\"Low\"},{\"value\":\"medium\",\"name\":\"Medium\"}]",
                "[{\"value\":\"low\",\"name\":\"Low\"},{\"value\":\"medium\",\"name\":\"Medium\"},{\"value\":\"high\",\"name\":\"High\"},{\"value\":\"max\",\"name\":\"Maximum\"}]", StringComparison.Ordinal));

    private static Task SendTogetherAsync(ScriptedPeer peer, params TranscriptLine[] frames) => peer.RunAsync([
        // ScriptedPeer 每个 TranscriptLine 写入后 flush；把多帧合并，避免响应之间发生人为调度。
        // 未知请求的响应作为读循环屏障，保证 gate 释放前所有配置帧都已入队。
        new TranscriptLine(false, string.Join('\n', frames.Select(f => f.Json)) + "\n" +
            """{"jsonrpc":"2.0","id":"config-barrier","method":"test/config_barrier","params":{}}"""),
        new TranscriptLine(true, """{"jsonrpc":"2.0","id":"config-barrier","error":{"code":-32601,"message":"Method not found"}}"""),
    ]);

    private static void AssertConfigResponseThenNotification(ConfigSnapshot[] snapshots, AcpSession session)
    {
        Assert.Collection(snapshots,
            gate => Assert.Equal("pro-2", gate.CurrentModel),
            response =>
            {
                Assert.Equal("flash-2", response.CurrentModel);
                var model = Assert.Single(response.Models, m => m.Value == "flash-2");
                Assert.Equal("Flash 2", model.Name);
                Assert.Equal("Fast and low cost", model.Description);
                Assert.Equal(["low", "medium"], response.ReasoningEfforts);
            },
            notification =>
            {
                Assert.Equal("flash-2", notification.CurrentModel);
                var model = Assert.Single(notification.Models, m => m.Value == "flash-2");
                Assert.Equal("Flash 2 Updated", model.Name);
                Assert.Equal("New metadata from notification", model.Description);
                Assert.Equal(["low", "medium", "high", "max"], notification.ReasoningEfforts);
            });
        var finalModel = Assert.Single(session.Config.Models, m => m.Value == "flash-2");
        Assert.Equal("flash-2", session.Config.CurrentModel);
        Assert.Equal("Flash 2 Updated", finalModel.Name);
        Assert.Equal("New metadata from notification", finalModel.Description);
        Assert.Equal(["low", "medium", "high", "max"], session.Config.ReasoningEfforts);
    }

    private static (ExternalAgent Agent, ScriptedPeer Peer) Create(
        Func<RuntimeApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approve = null)
    {
        var peer = new ScriptedPeer();
        var runtime = new FixtureRuntime(peer);
        var agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, AcpRuntime.DefaultModelId), Cwd) { ApprovalHandler = approve };
        return (agent, peer);
    }

    private static List<AgentEvent> Record(IAgentLoop agent)
    {
        var events = new List<AgentEvent>();
        agent.Subscribe((e, _) =>
        {
            lock (events)
            {
                events.Add(e);
            }
            return ValueTask.CompletedTask;
        });
        return events;
    }

    private sealed class FixtureRuntime(ScriptedPeer peer) : IAgentRuntime
    {
        public AcpSession? Started;
        public string Id => "acp:example";
        public string DisplayName => "Example Agent";
        public RuntimeDescriptor Descriptor { get; } = new("acp:example", "Example Agent", RuntimeStatus.Ready);
        public event Action? DescriptorChanged { add { } remove { } }
        public Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Descriptor);

        public async Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
        {
            var session = new AcpSession(peer.ClientInput, peer.ClientOutput, Agent, request.Host);
            await session.StartAsync(request.Cwd, request.ResumeSessionId, cancellationToken);
            Started = session;
            return session;
        }
    }

    private sealed class RecordingHost : IRuntimeHost
    {
        public List<string> SessionIds { get; } = [];
        public ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalDecision.Deny());
        public void OnNativeSessionId(string nativeSessionId) => SessionIds.Add(nativeSessionId);
        public IReadOnlyList<RuntimeCommand>? Commands { get; private set; }
        public void OnCommands(IReadOnlyList<RuntimeCommand> commands) => Commands = commands;
        public RuntimeElicitationRequest? Elicitation { get; private set; }

        public ValueTask<RuntimeElicitationResult> RequestElicitationAsync(RuntimeElicitationRequest request, CancellationToken cancellationToken)
        {
            Elicitation = request;
            return ValueTask.FromResult(new RuntimeElicitationResult("accept", """{"strategy":"balanced"}"""));
        }
    }
}
