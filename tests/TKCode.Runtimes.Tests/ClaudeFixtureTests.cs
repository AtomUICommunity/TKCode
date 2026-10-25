using System.Text.Json;
using TKCode.Agent;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Runtimes.Claude;
using TKCode.Runtimes.Tests.Fixtures;
using Xunit;

namespace TKCode.Runtimes.Tests;

/// <summary>docs/protocols/claude-code-stream-json.md 附录 A 的夹具。(a)–(d2) 在同一个进程里依次运行。</summary>
public sealed class ClaudeFixtureTests
{
    private const string Spec = "claude-code-stream-json.md";
    private const string SessionId = "5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static IReadOnlyList<TranscriptLine> Load(string heading) => TranscriptFixture.Load(Spec, heading);

    [Fact]
    public async Task Session_TextThenToolAllowThenDenyThenInterrupts()
    {
        var peer = new ScriptedPeer();
        var uuids = new Queue<string>();
        var runtime = new ClaudeFixtureRuntime(peer, uuids, SessionId);
        Func<RuntimeApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>> approve = (_, _) => ValueTask.FromResult(ApprovalDecision.AllowOnce);
        var agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, ClaudeRuntime.DefaultModelId), "/Users/dev/proj")
        {
            ApprovalHandler = (r, ct) => approve(r, ct),
        };
        var events = Record(agent);

        // (a) 握手 + thinking + 文本
        var a = Load("### A.1 (a)");
        uuids.Enqueue(UuidOf(a));
        var run = agent.PromptAsync(UserMessage.FromText("用一句话解释什么是递归。"));
        await peer.RunAsync(a);
        await run.WaitAsync(Timeout);
        var answer = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(StopReason.Stop, answer.StopReason);
        Assert.Equal("用户想要一个简洁的定义，一句话即可。", Assert.IsType<ThinkingContent>(answer.Content[0]).Thinking);
        Assert.Equal("递归就是一个函数在定义中调用自身，把问题拆成更小的同类问题，直到遇到可以直接回答的基本情况。",
            Assert.IsType<TextContent>(answer.Content[1]).Text);
        Assert.Equal(new Usage(3, 96, 14210, 1520), answer.Usage);
        Assert.Equal(SessionId, agent.NativeSessionId);
        // assistant 帧不产生重复增量
        Assert.Equal(2, events.OfType<MessageUpdateEvent>().Count(u => u.Event is TKCode.Ai.Streaming.TextDeltaEvent or TKCode.Ai.Streaming.ThinkingDeltaEvent
            && u.MessageId == events.OfType<MessageStartEvent>().First(s => s.Message is null).MessageId && u.Event is TKCode.Ai.Streaming.TextDeltaEvent));

        // (b) 工具调用 → 允许一次 → tool_result → 最终回答
        RuntimeApprovalRequest? asked = null;
        approve = (r, _) =>
        {
            asked = r;
            return ValueTask.FromResult(ApprovalDecision.AllowOnce);
        };
        var b = Load("### A.2 (b)");
        uuids.Enqueue(UuidOf(b));
        var before = agent.Messages.Count;
        run = agent.PromptAsync(UserMessage.FromText("列出 src 目录下的文件。"));
        await peer.RunAsync(b);
        await run.WaitAsync(Timeout);
        Assert.Equal(ApprovalKind.Command, asked!.Kind);
        Assert.Equal("List files in src", asked.Title);
        var turnB = agent.Messages.Skip(before).ToArray();
        var call = Assert.IsType<AssistantMessage>(turnB[1]);
        Assert.Equal("我来列出 src 目录。", Assert.IsType<TextContent>(call.Content[0]).Text);
        var tool = Assert.IsType<ToolCallContent>(call.Content[1]);
        Assert.Equal("Bash", tool.Name);
        Assert.Equal("ls -1 src", JsonDocument.Parse(tool.ArgumentsJson).RootElement.GetProperty("command").GetString());
        var result = Assert.IsType<ToolResultMessage>(turnB[2]);
        Assert.Equal("App.cs\nProgram.cs\nViews", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal("src 下有 App.cs、Program.cs 和 Views 目录。",
            Assert.IsType<TextContent>(Assert.Single(Assert.IsType<AssistantMessage>(turnB[3]).Content)).Text);

        // (c) 拒绝
        approve = (_, _) => ValueTask.FromResult(ApprovalDecision.Deny());
        var c = Load("### A.3 (c)");
        uuids.Enqueue(UuidOf(c));
        run = agent.PromptAsync(UserMessage.FromText("清理 build 目录。"));
        await peer.RunAsync(c);
        await run.WaitAsync(Timeout);
        var denied = agent.Messages.OfType<ToolResultMessage>().Last();
        Assert.True(denied.IsError);
        Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessage>(agent.Messages[^1]).StopReason);

        // (d1) 文本流式输出时中断
        var d1 = Load("#### (d1)");
        uuids.Enqueue(UuidOf(d1));
        var interruptAt = d1.ToList().FindIndex(l => l.FromClient && l.Json.Contains("\"interrupt\""));
        run = agent.PromptAsync(UserMessage.FromText("详细介绍一下这个项目的架构。"));
        await peer.RunAsync(d1.Take(interruptAt));
        agent.Abort();
        await peer.RunAsync(d1.Skip(interruptAt));
        await run.WaitAsync(Timeout);
        var aborted = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(StopReason.Aborted, aborted.StopReason);
        Assert.Equal("这个项目分为三层：桌面 UI、Agent Host 和", Assert.IsType<TextContent>(Assert.Single(aborted.Content)).Text);

        // (d2) 等待审批时中断：CLI 撤回审批请求，TKCode 不应答
        approve = async (_, ct) =>
        {
            agent.Abort();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return ApprovalDecision.AllowOnce;
        };
        var d2 = Load("#### (d2)");
        uuids.Enqueue(UuidOf(d2));
        run = agent.PromptAsync(UserMessage.FromText("运行全部测试。"));
        await peer.RunAsync(d2);
        await run.WaitAsync(Timeout);
        Assert.Equal(StopReason.Aborted, Assert.IsType<AssistantMessage>(agent.Messages[^1]).StopReason);
        Assert.Equal("[Request interrupted by user for tool use]",
            Assert.IsType<TextContent>(Assert.Single(agent.Messages.OfType<ToolResultMessage>().Last().Content)).Text);
        Assert.Null(await peer.ReadExtraAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task E1_AuthFailure_SyntheticAssistantAndErrorResult()
    {
        var peer = new ScriptedPeer();
        var uuids = new Queue<string>();
        var runtime = new ClaudeFixtureRuntime(peer, uuids, SessionId);
        var agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, ClaudeRuntime.DefaultModelId), "/Users/dev/proj");
        var e1 = Load("#### (e1)");
        uuids.Enqueue(UuidOf(e1));
        var run = agent.PromptAsync(UserMessage.FromText("你好"));
        // (e1) 省略了握手：先完成 initialize
        await peer.RunAsync(Load("### A.1 (a)").Take(2));
        await peer.RunAsync(e1);
        await run.WaitAsync(Timeout);
        var error = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(StopReason.Error, error.StopReason);
        Assert.Equal("Invalid API key · Fix external API key", error.ErrorMessage);
        Assert.Equal("Invalid API key · Fix external API key", Assert.IsType<TextContent>(Assert.Single(error.Content)).Text);
    }

    [Fact]
    public async Task E2_ResumeOfMissingSession_FailsInitializeWithCliError()
    {
        var peer = new ScriptedPeer();
        var session = new ClaudeSession(peer.ClientInput, peer.ClientOutput, new NullHost(), "0d9b7e55-1c2a-4f3e-8b6d-5a4c3b2a1f00");
        var start = session.StartAsync(CancellationToken.None);
        await peer.RunAsync(Load("#### (e2)"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(Timeout));
        Assert.Equal("No conversation found with session ID: 0d9b7e55-1c2a-4f3e-8b6d-5a4c3b2a1f00", error.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task FullAccess_SwitchesPermissionModeBeforeTheTurn()
    {
        var peer = new ScriptedPeer();
        var uuids = new Queue<string>();
        var runtime = new ClaudeFixtureRuntime(peer, uuids, SessionId);
        var agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, ClaudeRuntime.DefaultModelId), "/Users/dev/proj")
        {
            PermissionMode = TKCode.Agent.PermissionMode.FullAccess,
        };
        uuids.Enqueue("00000000-0000-4000-8000-000000000001");
        var run = agent.PromptAsync(UserMessage.FromText("hi"));
        await peer.RunAsync(Load("### A.1 (a)").Take(2));
        await peer.RunAsync([
            new TranscriptLine(true, """{"type":"control_request","request_id":"tkcode_mode_1","request":{"subtype":"set_permission_mode","mode":"bypassPermissions"}}"""),
            new TranscriptLine(false, """{"type":"control_response","response":{"subtype":"success","request_id":"tkcode_mode_1","response":{}}}"""),
            new TranscriptLine(true, """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"hi"}]},"parent_tool_use_id":null,"uuid":"00000000-0000-4000-8000-000000000001"}"""),
            new TranscriptLine(false, """{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11","terminal_reason":"completed"}"""),
        ]);
        await run.WaitAsync(Timeout);
        // 没有助手内容的成功轮次不产生空消息
        Assert.IsType<UserMessage>(Assert.Single(agent.Messages));
        Assert.False(agent.IsRunning);
    }

    private const string AskInput =
        """{"questions":[{"question":"Which database?","header":"Database","options":[{"label":"Postgres","description":"Full featured"},{"label":"SQLite","description":"Zero setup"}],"multiSelect":false},{"question":"Which features?","header":"Features","options":[{"label":"Auth"},{"label":"Logs, metrics"}],"multiSelect":true}]}""";

    /// <summary>AskUserQuestion 的一轮：assistant 帧 → can_use_tool → TKCode 的应答 → tool_result → result。</summary>
    private static TranscriptLine[] AskTurn(string uuid, string response, string toolResult, bool isError)
    {
        string Fill(string template) => template.Replace("@UUID", uuid).Replace("@SESSION", SessionId).Replace("@INPUT", AskInput)
            .Replace("@RESPONSE", response).Replace("@RESULT", toolResult).Replace("@ERROR", isError ? "true" : "false");
        return
        [
            new(true, Fill("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"build it"}]},"parent_tool_use_id":null,"uuid":"@UUID"}""")),
            new(false, Fill("""{"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_ask","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_ask","name":"AskUserQuestion","input":@INPUT}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}},"parent_tool_use_id":null,"session_id":"@SESSION","uuid":"a0000000-0000-4000-8000-000000000001"}""")),
            new(false, Fill("""{"type":"control_request","request_id":"req_ask","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":@INPUT,"tool_use_id":"toolu_ask","requires_user_interaction":true}}""")),
            new(true, Fill("""{"type":"control_response","response":{"subtype":"success","request_id":"req_ask","response":@RESPONSE}}""")),
            new(false, Fill("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_ask","type":"tool_result","content":"@RESULT","is_error":@ERROR}]},"parent_tool_use_id":null,"session_id":"@SESSION","uuid":"a0000000-0000-4000-8000-000000000002"}""")),
            new(false, Fill("""{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"@SESSION","terminal_reason":"completed"}""")),
        ];
    }

    /// <summary>ExitPlanMode 的一轮（docs/design/plan-mode.md §15）：assistant 帧 → can_use_tool → TKCode 的应答 → tool_result → result。</summary>
    private static TranscriptLine[] PlanTurn(string uuid, string input, string response, string toolResult, bool isError, string? beforeResult = null)
    {
        string Fill(string template) => template.Replace("@UUID", uuid).Replace("@SESSION", SessionId).Replace("@INPUT", input)
            .Replace("@RESPONSE", response).Replace("@RESULT", toolResult).Replace("@ERROR", isError ? "true" : "false");
        TranscriptLine[] lines =
        [
            new(true, Fill("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"build it"}]},"parent_tool_use_id":null,"uuid":"@UUID"}""")),
            new(false, Fill("""{"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_plan","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_plan","name":"ExitPlanMode","input":@INPUT}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}},"parent_tool_use_id":null,"session_id":"@SESSION","uuid":"a0000000-0000-4000-8000-000000000011"}""")),
            new(false, Fill("""{"type":"control_request","request_id":"req_plan","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":@INPUT,"tool_use_id":"toolu_plan","requires_user_interaction":true}}""")),
            new(true, Fill("""{"type":"control_response","response":{"subtype":"success","request_id":"req_plan","response":@RESPONSE}}""")),
            new(false, Fill("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_plan","type":"tool_result","content":"@RESULT","is_error":@ERROR}]},"parent_tool_use_id":null,"session_id":"@SESSION","uuid":"a0000000-0000-4000-8000-000000000012"}""")),
        ];
        return
        [
            .. lines,
            .. beforeResult is null ? [] : new TranscriptLine[] { new(false, Fill(beforeResult)) },
            new(false, Fill("""{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"@SESSION","terminal_reason":"completed"}""")),
        ];
    }

    [Fact]
    public async Task ExitPlanMode_GoesToPlanReview_AndApprovalSetsTheMode()
    {
        var planFile = Path.Combine(Directory.CreateTempSubdirectory("tkcode-claude-plan-").FullName, "plan.md");
        File.WriteAllText(planFile, "# Plan");
        var input = $$"""{"plan":"# Plan","planFilePath":{{JsonSerializer.Serialize(planFile)}}}""";
        var peer = new ScriptedPeer();
        var uuids = new Queue<string>();
        var runtime = new ClaudeFixtureRuntime(peer, uuids, SessionId);
        RuntimePlanReviewRequest? reviewed = null;
        ExternalAgent agent = null!;
        agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, ClaudeRuntime.DefaultModelId), "/Users/dev/proj")
        {
            // 计划审批不是工具审批：任何权限档位都交给审批面板
            PermissionMode = PermissionMode.AutoApprove,
            PlanReviewHandler = (request, _) =>
            {
                reviewed = request;
                // 宿主切换权限（这里模拟 SessionManager 选了“批准，逐项审批”）
                agent.PermissionMode = PermissionMode.Ask;
                return Task.FromResult(new TKCode.Agent.Plans.PlanReviewResult(TKCode.Agent.Plans.PlanReviewOutcome.Approved,
                    TKCode.Agent.Plans.PlanReviewChoice.Ask, EditedPlan: "# Edited"));
            },
            ApprovalHandler = (_, _) => throw new InvalidOperationException("ExitPlanMode is not a tool approval"),
        };
        var events = Record(agent);
        uuids.Enqueue("00000000-0000-4000-8000-0000000000b1");
        var run = agent.PromptAsync(UserMessage.FromText("build it"));
        await peer.RunAsync(Load("### A.1 (a)").Take(2));
        var response = $$"""{"behavior":"allow","updatedInput":{"planFilePath":{{JsonSerializer.Serialize(planFile)}},"plan":"# Edited"},"updatedPermissions":[{"type":"setMode","mode":"default","destination":"session"}],"toolUseID":"toolu_plan","decisionClassification":"user_temporary"}""";
        await peer.RunAsync(PlanTurn("00000000-0000-4000-8000-0000000000b1", input, response, "User has approved your plan.", isError: false));
        await run.WaitAsync(Timeout);

        Assert.Equal(("toolu_plan", "# Plan", planFile), (reviewed!.ToolCallId, reviewed.Plan, reviewed.PlanPath));
        Assert.Equal("# Edited", File.ReadAllText(planFile).Trim());
        var end = events.OfType<ToolExecutionEndEvent>().Single(e => e.ToolCallId == "toolu_plan");
        var details = JsonDocument.Parse(end.Result.Details!).RootElement;
        Assert.Equal(("planReview", "approved", "ask"),
            (details.GetProperty("kind").GetString(), details.GetProperty("status").GetString(), details.GetProperty("choice").GetString()));
    }

    [Fact]
    public async Task ExitPlanMode_Revise_DeniesWithTheFeedback()
    {
        const string input = """{"plan":"# Plan"}""";
        var peer = new ScriptedPeer();
        var uuids = new Queue<string>();
        var runtime = new ClaudeFixtureRuntime(peer, uuids, SessionId);
        var agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, ClaudeRuntime.DefaultModelId), "/Users/dev/proj")
        {
            PlanReviewHandler = (_, _) => Task.FromResult(new TKCode.Agent.Plans.PlanReviewResult(TKCode.Agent.Plans.PlanReviewOutcome.Revise,
                Feedback: "Split step one.")),
        };
        uuids.Enqueue("00000000-0000-4000-8000-0000000000b2");
        var run = agent.PromptAsync(UserMessage.FromText("build it"));
        await peer.RunAsync(Load("### A.1 (a)").Take(2));
        var message = JsonSerializer.Serialize(ClaudeSession.PlanFeedbackMessage("Split step one."));
        await peer.RunAsync(PlanTurn("00000000-0000-4000-8000-0000000000b2", input,
            """{"behavior":"deny","message":@MESSAGE,"toolUseID":"toolu_plan","decisionClassification":"user_reject"}""".Replace("@MESSAGE", message),
            "rejected", isError: true));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ReportedPermissionMode_EnteringPlan_SyncsTheMode()
    {
        const string input = """{"plan":"# Plan"}""";
        var peer = new ScriptedPeer();
        var uuids = new Queue<string>();
        var runtime = new ClaudeFixtureRuntime(peer, uuids, SessionId);
        var changes = new List<bool>();
        var agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, ClaudeRuntime.DefaultModelId), "/Users/dev/proj")
        {
            PlanReviewHandler = (_, _) => Task.FromResult(TKCode.Agent.Plans.PlanReviewResult.Dismissed),
        };
        agent.PlanModeChanged += changes.Add;
        uuids.Enqueue("00000000-0000-4000-8000-0000000000b3");
        var run = agent.PromptAsync(UserMessage.FromText("build it"));
        await peer.RunAsync(Load("### A.1 (a)").Take(2));
        var message = JsonSerializer.Serialize(ClaudeSession.PlanNotApprovedMessage);
        // 模型自己调用了 EnterPlanMode：CLI 上报 permissionMode 变成 plan
        await peer.RunAsync(PlanTurn("00000000-0000-4000-8000-0000000000b3", input,
            """{"behavior":"deny","message":@MESSAGE,"toolUseID":"toolu_plan","decisionClassification":"user_reject"}""".Replace("@MESSAGE", message),
            "rejected", isError: true,
            beforeResult: """{"type":"system","subtype":"status","status":null,"permissionMode":"plan","session_id":"@SESSION","uuid":"a0000000-0000-4000-8000-000000000013"}"""));
        await run.WaitAsync(Timeout);
        Assert.Equal([true], changes);
    }

    [Fact]
    public async Task AskUserQuestion_AnswersGoIntoUpdatedInput_KeyedByQuestionText()
    {
        var peer = new ScriptedPeer();
        var uuids = new Queue<string>();
        var runtime = new ClaudeFixtureRuntime(peer, uuids, SessionId);
        RuntimeInputRequest? asked = null;
        var agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, ClaudeRuntime.DefaultModelId), "/Users/dev/proj")
        {
            // 权限模式与提问无关：完全授权下也交给问答面板
            InputHandler = (request, _) =>
            {
                asked = request;
                return Task.FromResult(new TKCode.Agent.Questions.UserInputResult(TKCode.Agent.Questions.UserInputOutcome.Answered, [
                    new("Which database?", ["SQLite"]),
                    new("Which features?", ["Auth", "Logs, metrics"]),
                ]));
            },
            ApprovalHandler = (_, _) => throw new InvalidOperationException("AskUserQuestion is not an approval"),
        };
        var events = Record(agent);
        uuids.Enqueue("00000000-0000-4000-8000-0000000000a1");
        var run = agent.PromptAsync(UserMessage.FromText("build it"));
        await peer.RunAsync(Load("### A.1 (a)").Take(2));
        await peer.RunAsync(AskTurn("00000000-0000-4000-8000-0000000000a1",
            """{"behavior":"allow","updatedInput":@INPUT,"toolUseID":"toolu_ask","decisionClassification":"user_temporary"}"""
                .Replace("@INPUT", AskInput[..^1] + ""","answers":{"Which database?":"SQLite","Which features?":"Auth, \"Logs, metrics\""}}"""),
            "User answered", isError: false));
        await run.WaitAsync(Timeout);

        Assert.Equal("toolu_ask", asked!.ToolCallId);
        Assert.Equal(["Which database?", "Which features?"], asked.Questions.Select(q => q.Id));
        Assert.True(asked.Questions[1].MultiSelect);
        Assert.Equal("Zero setup", asked.Questions[0].Options[1].Description);
        // 转录卡片：工具结果的 Details 是 TKCode 记录的问答
        var end = events.OfType<ToolExecutionEndEvent>().Single(e => e.ToolCallId == "toolu_ask");
        var details = JsonDocument.Parse(end.Result.Details!).RootElement;
        Assert.Equal("question", details.GetProperty("kind").GetString());
        Assert.Equal("answered", details.GetProperty("status").GetString());
        Assert.Equal("SQLite", details.GetProperty("questions")[0].GetProperty("selected")[0].GetString());
    }

    [Fact]
    public async Task AskUserQuestion_Declined_DeniesWithDeclinedMessage()
    {
        var peer = new ScriptedPeer();
        var uuids = new Queue<string>();
        var runtime = new ClaudeFixtureRuntime(peer, uuids, SessionId);
        var agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, ClaudeRuntime.DefaultModelId), "/Users/dev/proj")
        {
            InputHandler = (_, _) => Task.FromResult(TKCode.Agent.Questions.UserInputResult.Declined),
        };
        var events = Record(agent);
        uuids.Enqueue("00000000-0000-4000-8000-0000000000a2");
        var run = agent.PromptAsync(UserMessage.FromText("build it"));
        await peer.RunAsync(Load("### A.1 (a)").Take(2));
        var declined = JsonSerializer.Serialize(TKCode.Agent.Questions.UserAnswers.DeclinedMessage);
        await peer.RunAsync(AskTurn("00000000-0000-4000-8000-0000000000a2",
            """{"behavior":"deny","message":@MESSAGE,"toolUseID":"toolu_ask","decisionClassification":"user_reject"}""".Replace("@MESSAGE", declined),
            "declined", isError: true));
        await run.WaitAsync(Timeout);
        var end = events.OfType<ToolExecutionEndEvent>().Single(e => e.ToolCallId == "toolu_ask");
        Assert.Equal("declined", JsonDocument.Parse(end.Result.Details!).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Models_AreParsedFromInitialize()
    {
        var init = JsonDocument.Parse(Load("### A.1 (a)")[1].Json).RootElement.GetProperty("response").GetProperty("response");
        var models = new ClaudeRuntime().ParseModels(init);
        Assert.Equal(["default", "sonnet", "haiku"], models.Select(m => m.Id));
        Assert.Equal("Default (recommended)", models[0].Name);
        Assert.True(models[0].SupportsFastMode);
        Assert.Equal("high", ClaudeRuntime.EffortFor(models[0], TKCode.Ai.Models.ThinkingLevel.High));
        Assert.Null(ClaudeRuntime.EffortFor(models[2], TKCode.Ai.Models.ThinkingLevel.High));
    }

    private static string UuidOf(IReadOnlyList<TranscriptLine> lines) =>
        JsonDocument.Parse(lines.First(l => l.FromClient && l.Json.Contains("\"type\":\"user\"")).Json).RootElement.GetProperty("uuid").GetString()!;

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

    [Fact]
    public void InitializeCommands_AndPlanModeMapping()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"commands":[{"name":"compact","description":"Clear conversation history but keep a summary in context","argumentHint":"<optional custom summarization instructions>"},{"name":"review","description":"Review a pull request","argumentHint":""}]}
            """);
        Assert.Equal([("compact", "Clear conversation history but keep a summary in context", "<optional custom summarization instructions>"), ("review", "Review a pull request", null)],
            ClaudeSession.ParseCommands(doc.RootElement).Select(c => (c.Name, c.Description, c.ArgumentHint)));

        var model = new ModelInfo { Id = "default", Name = "Default", Provider = "claude-code", Api = "claude-code" };
        var options = new TurnOptions(model, ThinkingLevel.Medium, false, TKCode.Agent.PermissionMode.FullAccess);
        Assert.Equal("bypassPermissions", ClaudeRuntime.PermissionModeFor(options));
        // 只读模式优先于权限档位：映射到 Claude 原生的 plan 权限模式
        Assert.Equal("plan", ClaudeRuntime.PermissionModeFor(options with { Mode = new RuntimeMode("plan", true) }));
        Assert.Equal("default", ClaudeRuntime.PermissionModeFor(options with { Permission = TKCode.Agent.PermissionMode.Ask, Mode = new RuntimeMode("acp", false) }));
    }

    private sealed class NullHost : IRuntimeHost
    {
        public ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalDecision.Deny());

        public void OnNativeSessionId(string nativeSessionId)
        {
        }
    }
}
