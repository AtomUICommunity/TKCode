using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Agent;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Assets;
using TKCode.Agent.Coding.Sessions;
using TKCode.Agent.Coding.Tools.Agents;
using TKCode.Agent.Coding.Tools.Plan;
using TKCode.Agent.Coding.Workspace;
using TKCode.AgentHost.Catalog;
using TKCode.AgentHost.Peers;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Ai.Messages;
using TKCode.Rpc;
using TKCode.Runtimes;
using Xunit;

namespace TKCode.AgentHost.Tests;

/// <summary>内置 exit_plan_mode / enter_plan_mode 经 Host 的 plan/review、input/request 到 UI（docs/design/plan-mode.md §10、§13）。</summary>
public sealed class PlanModeTests
{
    static PlanModeTests()
    {
        // 计划文件在 ~/.tkcode/plans 下：测试进程改到临时目录，不碰真实的家目录
        if (Environment.GetEnvironmentVariable(TKCodePaths.HomeVariable) is not { Length: > 0 })
        {
            Environment.SetEnvironmentVariable(TKCodePaths.HomeVariable, Directory.CreateTempSubdirectory("tkcode-plan-home-").FullName);
        }
    }

    private static SessionManager CreateManager()
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        return new SessionManager(new WireApiRegistry([new FakeWireApi(TimeSpan.Zero)]), catalog, new RuntimeRegistry([]),
            new PeerDirectory(NullLogger<PeerDirectory>.Instance), new TKCode.Mcp.McpManager(new TKCode.Mcp.McpHost(), null, () => []),
            NullLogger<SessionManager>.Instance);
    }

    private static string TempDir() => Directory.CreateTempSubdirectory("tkcode-plan-").FullName;

    /// <summary>新会话切到 Plan，并写好计划文件。</summary>
    private static AgentSession PlanSession(SessionManager sessions, string plan = "# Plan\n\n- step one", string permission = "ask")
    {
        var session = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        SessionOptions.Apply(session, new SessionOptionsDto(Role: SessionRole.PlanName, PermissionMode: permission));
        var path = session.Tools!.PlanFile!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, plan);
        return session;
    }

    private static async Task<ToolResultMessage> RunAsync(AgentSession session, string prompt)
    {
        await session.Agent.PromptAsync(UserMessage.FromText(prompt));
        await session.Agent.WaitForIdleAsync();
        return session.Agent.Messages.OfType<ToolResultMessage>().Last();
    }

    private static string TextOf(ToolResultMessage result) => string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));

    [Fact]
    public async Task ExitPlanMode_Approved_SwitchesModeAndPermission_AndTellsTheUi()
    {
        await using var sessions = CreateManager();
        var client = new PlanClient(_ => Task.FromResult(new PlanReviewResponse(PlanReviewResponse.Approved, "auto")));
        sessions.AttachClient(client);
        var session = PlanSession(sessions);

        var result = await RunAsync(session, "mcp:exit_plan_mode {}");

        var request = Assert.Single(client.Reviews);
        Assert.Equal(("builtin", session.Id, "ask"), (request.Source, request.SessionId, request.PermissionMode));
        Assert.Equal(["clear", "auto", "ask", "acp"], request.Choices);
        Assert.Equal("# Plan\n\n- step one", request.Plan);
        Assert.Equal(session.Tools!.PlanFile, request.PlanPath);
        Assert.Equal(request.ToolCallId, result.ToolCallId);
        Assert.StartsWith("The user approved the plan.", TextOf(result));

        Assert.Equal(SessionRole.AgentName, session.Role.Name);
        Assert.Equal(PermissionMode.AutoApprove, session.Agent.PermissionMode);
        var changed = client.Events.OfType<SessionOptionsChanged>().Last();
        Assert.Equal(("agent", "autoApprove"), (changed.Options.Role, changed.Options.PermissionMode));
        // 持久化：重新打开时恢复为批准后的模式
        var snapshot = SessionStore.Read(session.Store.FilePath);
        Assert.Equal("agent", snapshot.Role);
    }

    [Fact]
    public async Task ExitPlanMode_ApprovedAuto_KeepsFullAccess()
    {
        await using var sessions = CreateManager();
        sessions.AttachClient(new PlanClient(_ => Task.FromResult(new PlanReviewResponse(PlanReviewResponse.Approved, "auto"))));
        var session = PlanSession(sessions, permission: "fullAccess");
        await RunAsync(session, "mcp:exit_plan_mode {}");
        Assert.Equal(PermissionMode.FullAccess, session.Agent.PermissionMode);
    }

    [Fact]
    public async Task ExitPlanMode_ApprovedAcp_KeepsThePermission()
    {
        await using var sessions = CreateManager();
        sessions.AttachClient(new PlanClient(_ => Task.FromResult(new PlanReviewResponse(PlanReviewResponse.Approved, "acp"))));
        var session = PlanSession(sessions);
        var result = await RunAsync(session, "mcp:exit_plan_mode {}");
        Assert.Equal((SessionRole.AcpName, PermissionMode.Ask), (session.Role.Name, session.Agent.PermissionMode));
        Assert.Contains("The session is now in ACP mode", TextOf(result));
    }

    [Fact]
    public async Task ExitPlanMode_ApprovedAsk_FromAutoApprove()
    {
        await using var sessions = CreateManager();
        sessions.AttachClient(new PlanClient(_ => Task.FromResult(new PlanReviewResponse(PlanReviewResponse.Approved, "ask"))));
        var session = PlanSession(sessions, permission: "autoApprove");
        await RunAsync(session, "mcp:exit_plan_mode {}");
        Assert.Equal((SessionRole.AgentName, PermissionMode.Ask), (session.Role.Name, session.Agent.PermissionMode));
    }

    [Fact]
    public async Task ExitPlanMode_Revise_StaysInPlan()
    {
        await using var sessions = CreateManager();
        var client = new PlanClient(_ => Task.FromResult(new PlanReviewResponse(PlanReviewResponse.Revise, Feedback: "Split step one.")));
        sessions.AttachClient(client);
        var session = PlanSession(sessions);
        var result = await RunAsync(session, "mcp:exit_plan_mode {}");
        Assert.Equal(ExitPlanModeTool.FeedbackText("Split step one."), TextOf(result));
        Assert.Equal(SessionRole.PlanName, session.Role.Name);
        Assert.Empty(client.Events.OfType<SessionOptionsChanged>());
    }

    [Fact]
    public async Task ExitPlanMode_UnknownChoice_IsNotAnApproval()
    {
        await using var sessions = CreateManager();
        sessions.AttachClient(new PlanClient(_ => Task.FromResult(new PlanReviewResponse(PlanReviewResponse.Approved, "bogus"))));
        var session = PlanSession(sessions);
        var result = await RunAsync(session, "mcp:exit_plan_mode {}");
        Assert.Equal(ExitPlanModeTool.NotApprovedMessage, TextOf(result));
        Assert.Equal(SessionRole.PlanName, session.Role.Name);
    }

    [Fact]
    public async Task ExitPlanMode_WithoutAClient_IsAnError()
    {
        await using var sessions = CreateManager();
        var session = PlanSession(sessions);
        var result = await RunAsync(session, "mcp:exit_plan_mode {}");
        Assert.True(result.IsError);
        Assert.Equal(ExitPlanModeTool.UnavailableMessage(session.Tools!.PlanFile), TextOf(result));
    }

    [Fact]
    public async Task ExitPlanMode_Abort_WithdrawsTheRequest()
    {
        await using var sessions = CreateManager();
        var asked = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        sessions.AttachClient(new PlanClient(async ct =>
        {
            asked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return PlanReviewResponse.Dismiss;
        }));
        var session = PlanSession(sessions);
        var run = session.Agent.PromptAsync(UserMessage.FromText("mcp:exit_plan_mode {}"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Agent.Abort();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Operation aborted.", TextOf(session.Agent.Messages.OfType<ToolResultMessage>().Single()));
        Assert.Equal(SessionRole.PlanName, session.Role.Name);
    }

    [Fact]
    public async Task EnterPlanMode_AsksThroughTheQuestionPanel_AndSwitchesToPlan()
    {
        await using var sessions = CreateManager();
        var client = new PlanClient(_ => Task.FromResult(PlanReviewResponse.Dismiss))
        {
            Answer = request => new UserInputResponse(UserInputResponse.Answered, [new UserAnswerDto(request.Questions[0].Id, [EnterPlanModeTool.PlanOption])]),
        };
        sessions.AttachClient(client);
        var session = sessions.Create(TempDir(), FakeWireApi.Models[1]);

        var result = await RunAsync(session, """mcp:enter_plan_mode {"reason":"Several designs are possible."}""");

        var question = Assert.Single(client.Questions);
        Assert.Equal(SessionManager.EnterPlanModeSource, question.Source);
        Assert.Equal("Several designs are possible.", question.Questions[0].Question);
        Assert.False(question.Questions[0].AllowOther);
        Assert.Equal(EnterPlanModeTool.AcceptedMessage, TextOf(result));
        Assert.Equal(SessionRole.PlanName, session.Role.Name);
        Assert.Equal("plan", client.Events.OfType<SessionOptionsChanged>().Last().Options.Role);
    }

    [Fact]
    public async Task PlanMode_RejectsGeneralPurposeSubagents()
    {
        await using var sessions = CreateManager();
        sessions.AttachClient(new PlanClient(_ => Task.FromResult(PlanReviewResponse.Dismiss)));
        var session = PlanSession(sessions);
        var result = await RunAsync(session,
            """mcp:agent {"description":"impl","prompt":"change things","subagent_type":"general-purpose"}""");
        Assert.True(result.IsError);
        Assert.Equal(AgentTool.ReadOnlySubagentMessage, TextOf(result));
    }

    [Fact]
    public async Task PlanMode_CommandsThatMayChangeTheWorkspace_AlwaysAsk()
    {
        await using var sessions = CreateManager();
        var client = new PlanClient(_ => Task.FromResult(PlanReviewResponse.Dismiss)) { Approve = false };
        sessions.AttachClient(client);
        var session = PlanSession(sessions, permission: "fullAccess");
        var result = await RunAsync(session, """mcp:bash {"command":"touch created.txt"}""");

        var approval = Assert.Single(client.Approvals);
        Assert.Equal((PermissionPolicy.ReadOnlyCommandKind, false), (approval.Kind, approval.CanAllowForSession));
        Assert.True(result.IsError);
        Assert.False(File.Exists(Path.Combine(session.Cwd, "created.txt")));
    }

    [Fact]
    public async Task ReadOnlySubagents_FollowTheParentsFullAccess()
    {
        await using var sessions = CreateManager();
        var client = new PlanClient(_ => Task.FromResult(PlanReviewResponse.Dismiss)) { Approve = false };
        sessions.AttachClient(client);
        var parent = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        SessionOptions.Apply(parent, new SessionOptionsDto(PermissionMode: "fullAccess"));
        var handle = await sessions.Coordinator.SpawnAsync(
            new SubagentRequest(parent.Id, "child", """mcp:bash {"command":"touch created.txt"}""", SubagentTypes.Explore, null, null),
            CancellationToken.None);
        var outcome = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("completed", outcome.Status);
        Assert.Empty(client.Approvals);
        Assert.True(File.Exists(Path.Combine(parent.Cwd, "created.txt")));
    }

    [Fact]
    public async Task Subagents_DoNotGetPlanTools()
    {
        await using var sessions = CreateManager();
        var client = new PlanClient(_ => Task.FromResult(PlanReviewResponse.Dismiss))
        {
            Answer = request => new UserInputResponse(UserInputResponse.Answered, [new UserAnswerDto(request.Questions[0].Id, [EnterPlanModeTool.PlanOption])]),
        };
        sessions.AttachClient(client);
        var parent = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        Assert.Contains(((TKCode.Agent.Agent)parent.Agent).Tools, t => t.Name == EnterPlanModeTool.ToolName);
        // 假模型只在工具存在时才调用它：子 Agent 没有 enter_plan_mode，所以不会向用户提问
        var handle = await sessions.Coordinator.SpawnAsync(
            new SubagentRequest(parent.Id, "child", "mcp:enter_plan_mode {}", SubagentTypes.GeneralPurpose, null, null), CancellationToken.None);
        var outcome = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", outcome.Status);
        Assert.Empty(client.Questions);
    }

    private sealed class PlanClient(Func<CancellationToken, Task<PlanReviewResponse>> review) : IAgentHostClient
    {
        public List<PlanReviewRequest> Reviews { get; } = [];
        public List<UserInputRequest> Questions { get; } = [];
        public List<ToolApprovalRequest> Approvals { get; } = [];
        public List<SessionEvent> Events { get; } = [];
        public Func<UserInputRequest, UserInputResponse> Answer { get; init; } = _ => UserInputResponse.Decline;
        public bool Approve { get; init; } = true;

        public Task<PlanReviewResponse> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken = default)
        {
            lock (Reviews)
            {
                Reviews.Add(request);
            }
            return review(cancellationToken);
        }

        public Task<UserInputResponse> RequestUserInputAsync(UserInputRequest request, CancellationToken cancellationToken = default)
        {
            lock (Questions)
            {
                Questions.Add(request);
            }
            return Task.FromResult(Answer(request));
        }

        public void OnSessionEvent(SessionEventNotification notification)
        {
            lock (Events)
            {
                Events.Add(notification.Event);
            }
        }

        public void OnModelsChanged(ModelsChangedNotification notification)
        {
        }

        public Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            lock (Approvals)
            {
                Approvals.Add(request);
            }
            return Task.FromResult(new ToolApprovalResponse(Approve));
        }

        public void OnMcpChanged(McpChangedNotification notification)
        {
        }

        public Task<McpElicitResponse> RequestMcpElicitationAsync(McpElicitRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpElicitResponse("cancel"));

        public Task<McpOpenUrlResponse> OpenMcpUrlAsync(McpOpenUrlRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpOpenUrlResponse(false));

        public void OnAssetsChanged(AssetsChangedNotification notification)
        {
        }
    }
}
