using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.AgentHost.Catalog;
using TKCode.AgentHost.Peers;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Config;
using TKCode.Rpc;
using TKCode.Runtimes;
using Xunit;

namespace TKCode.AgentHost.Tests;

public sealed class RuntimeModelRefreshTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("tkcode-runtime-models-").FullName;
    private readonly string? _savedHome = Environment.GetEnvironmentVariable(ConfigPaths.HomeVariable);
    private readonly string? _savedRuntimes = Environment.GetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES");

    public RuntimeModelRefreshTests()
    {
        Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _home);
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ConfigPaths.HomeVariable, _savedHome);
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", _savedRuntimes);
    }

    [Fact]
    public async Task RuntimeChanged_EnablesReasoningBeforeNotifying_WithoutResettingSelectionOrOptions()
    {
        var runtime = new UpdatingRuntime();
        var original = Model(runtime, "provider/model", reasoning: false);
        runtime.SetModels(Model(runtime, "default", reasoning: false), original);
        await using var sessions = CreateManager(runtime);
        var session = sessions.CreateExternal(_home, runtime, original);
        SessionOptions.Initialize(session);
        var before = SessionOptions.Apply(session, new SessionOptionsDto("Off", true, 1_000_000, "fullAccess", "plan"));
        var another = sessions.CreateExternal(_home, runtime, original);
        SessionOptions.Initialize(another);
        ModelInfo? modelAtNotification = null;
        SessionOptionsDto? optionsAtNotification = null;
        SessionOptionsDto? applied = null;
        var client = new ModelClient(() =>
        {
            modelAtNotification = session.Agent.Model;
            optionsAtNotification = SessionOptions.Current(session);
            // UI 收到新档位后立即提交：必须已经按新能力校验，而不是把 High 改回 Off。
            applied = SessionOptions.Apply(session, new SessionOptionsDto(ThinkingLevel: "High"));
        });
        sessions.AttachClient(client);

        var updated = original with
        {
            Name = "ACP model name",
            Description = "ACP model description",
            Reasoning = true,
            Compat = new ModelCompat { ReasoningEfforts = ["low", "high"] },
        };
        runtime.SetModels(Model(runtime, "default", reasoning: false), updated);

        Assert.Same(session, sessions.Get(session.Id));
        Assert.Same(updated, session.Agent.Model);
        Assert.Same(updated, another.Agent.Model);
        Assert.Same(updated, modelAtNotification);
        Assert.Equal(before, optionsAtNotification);
        Assert.Equal(before with { ThinkingLevel = "High" }, applied);
        Assert.Equal($"{runtime.Id}/provider/model", ModelCatalog.RefOf(session.Agent.Model));
        var dto = Assert.Single(Assert.Single(client.Notifications).Models, m => m.Ref == ModelCatalog.RefOf(updated));
        Assert.True(dto.Reasoning);
        Assert.Equal(["Low", "High"], dto.ThinkingLevels);
        Assert.Equal(updated.Name, dto.Name);
        Assert.Equal(updated.Description, dto.Description);
        Assert.Equal(0, runtime.Starts);
    }

    [Theory]
    [InlineData("low,high", "low,medium,high,max", "Max", "Max")]
    [InlineData("low,high,max", "low,medium", "High", "Medium")]
    [InlineData("low,high", "", "High", "Off")]
    public async Task RuntimeChanged_UsesAddedAndRemovedEffortsForOptionValidation(
        string previousEfforts, string nextEfforts, string requested, string expected)
    {
        var runtime = new UpdatingRuntime();
        var original = Model(runtime, "provider/model", efforts: previousEfforts.Split(','));
        runtime.SetModels(original);
        await using var sessions = CreateManager(runtime);
        var session = sessions.CreateExternal(_home, runtime, original);
        SessionOptions.Initialize(session);
        var before = SessionOptions.Apply(session, new SessionOptionsDto("High", true, 1_000_000, "autoApprove", "plan"));
        var client = new ModelClient();
        sessions.AttachClient(client);
        var updated = original with
        {
            Reasoning = nextEfforts.Length > 0,
            Compat = new ModelCompat { ReasoningEfforts = nextEfforts.Split(',', StringSplitOptions.RemoveEmptyEntries) },
        };

        runtime.SetModels(updated);

        Assert.Same(updated, session.Agent.Model);
        Assert.Equal(before, SessionOptions.Current(session));
        var dto = Assert.Single(Assert.Single(client.Notifications).Models, m => m.Ref == ModelCatalog.RefOf(updated));
        Assert.Equal(ThinkingLevels.Available(updated).Select(l => l.ToString()), dto.ThinkingLevels);
        var applied = SessionOptions.Apply(session, new SessionOptionsDto(ThinkingLevel: requested));
        Assert.Equal(before with { ThinkingLevel = expected }, applied);
        Assert.Equal(ModelCatalog.RefOf(original), ModelCatalog.RefOf(session.Agent.Model));
        // options/set 只校验参数，不反向触发 DescriptorChanged / models/changed。
        Assert.Single(client.Notifications);
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task RuntimeChanged_DoesNotTouchBuiltinOtherRuntimeOrMissingModels()
    {
        var builtinModel = FakeWireApi.Models[0];
        // 连 provider / model 引用也碰撞：是否为外部会话必须看 Agent，而不是只看 provider。
        var runtime = new UpdatingRuntime(builtinModel.Provider);
        var original = Model(runtime, builtinModel.Id);
        var removed = Model(runtime, "removed/model");
        runtime.SetModels(original, removed);
        var otherRuntime = new UpdatingRuntime("other-cli");
        var otherModel = Model(otherRuntime, original.Id);
        otherRuntime.SetModels(otherModel);
        await using var sessions = CreateManager(runtime, otherRuntime);
        var builtin = sessions.Create(_home, builtinModel);
        SessionOptions.Initialize(builtin);
        var builtinOptions = SessionOptions.Current(builtin);
        var external = sessions.CreateExternal(_home, runtime, original);
        var missing = sessions.CreateExternal(_home, runtime, removed);
        var other = sessions.CreateExternal(_home, otherRuntime, otherModel);
        var client = new ModelClient();
        sessions.AttachClient(client);
        var updated = original with { Name = "Updated", Reasoning = false };

        runtime.SetModels(updated);

        Assert.Same(updated, external.Agent.Model);
        Assert.Same(builtinModel, builtin.Agent.Model);
        Assert.Equal(builtinOptions, SessionOptions.Current(builtin));
        Assert.Same(otherModel, other.Agent.Model);
        Assert.Same(removed, missing.Agent.Model);
        Assert.Equal("removed/model", missing.Agent.Model.Id);
        Assert.Single(client.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeChanged_DuringActiveTurn_KeepsSnapshotAndReusesNativeSession(bool duringStartup)
    {
        var runtime = new UpdatingRuntime { HoldFirstTurn = true };
        var original = Model(runtime, "provider/model", reasoning: false);
        var updated = original with
        {
            Reasoning = true,
            Compat = new ModelCompat { ReasoningEfforts = ["low", "high"] },
        };
        runtime.SetModels(original);
        await using var sessions = CreateManager(runtime);
        var session = sessions.CreateExternal(_home, runtime, original);
        SessionOptions.Initialize(session);
        var client = new ModelClient();
        sessions.AttachClient(client);
        if (duringStartup)
        {
            // ACP 的 session/new 配置先于 StartSessionAsync 返回，当前回合已经捕获参数。
            runtime.OnStart = () => runtime.SetModels(updated);
        }
        var run = session.Agent.PromptAsync(UserMessage.FromText("first") with { Source = "session-message" });
        try
        {
            var active = await runtime.FirstTurn.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.Agent.IsRunning);
            if (!duringStartup)
            {
                runtime.SetModels(updated);
            }
            Assert.Same(updated, session.Agent.Model);
            Assert.Same(original, active.Model);
            Assert.Equal(ThinkingLevel.Off, active.ThinkingLevel);
            Assert.Equal("High", SessionOptions.Apply(session, new SessionOptionsDto(ThinkingLevel: "High")).ThinkingLevel);
            Assert.Same(original, active.Model);
            Assert.Equal(ThinkingLevel.Off, active.ThinkingLevel);
            Assert.Single(client.Notifications);
        }
        finally
        {
            runtime.ReleaseFirstTurn.TrySetResult();
        }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("native-session", session.External!.NativeSessionId);
        Assert.Equal(StopReason.Stop, Assert.Single(session.Agent.Messages.OfType<AssistantMessage>()).StopReason);

        await session.Agent.PromptAsync(UserMessage.FromText("next") with { Source = "session-message" })
            .WaitAsync(TimeSpan.FromSeconds(5));

        var turns = runtime.Turns.ToArray();
        Assert.Equal(2, turns.Length);
        Assert.Same(original, turns[0].Model);
        Assert.Same(updated, turns[1].Model);
        Assert.Equal(ThinkingLevel.High, turns[1].ThinkingLevel);
        Assert.Equal(1, runtime.Starts);
        Assert.Equal("native-session", session.External.NativeSessionId);
        Assert.Single(client.Notifications);
        Assert.All(session.Agent.Messages.OfType<AssistantMessage>(), m => Assert.Equal(StopReason.Stop, m.StopReason));
    }

    private static ModelInfo Model(IAgentRuntime runtime, string id, bool reasoning = true, string[]? efforts = null) =>
        RuntimeModels.Create(runtime, id, reasoning: reasoning, contextWindow: 1_000_000) with
        {
            SupportsFastMode = true,
            Compat = efforts is null ? null : new ModelCompat { ReasoningEfforts = efforts },
        };

    private static SessionManager CreateManager(params UpdatingRuntime[] runtimes)
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        return new SessionManager(new WireApiRegistry([new FakeWireApi()]), catalog, new RuntimeRegistry(runtimes),
            new PeerDirectory(NullLogger<PeerDirectory>.Instance),
            new TKCode.Mcp.McpManager(new TKCode.Mcp.McpHost(), null, () => []), NullLogger<SessionManager>.Instance);
    }

    private sealed class UpdatingRuntime(string id = "acp:opencode") : IAgentRuntime
    {
        public string Id => id;
        public string DisplayName => "Updating CLI";
        public RuntimeDescriptor Descriptor { get; private set; } = new(id, "Updating CLI", RuntimeStatus.Ready);
        public event Action? DescriptorChanged;
        public int Starts { get; private set; }
        public bool HoldFirstTurn { get; init; }
        public Action? OnStart { get; set; }
        public ConcurrentQueue<TurnOptions> Turns { get; } = new();
        public TaskCompletionSource<TurnOptions> FirstTurn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstTurn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetModels(params ModelInfo[] models)
        {
            Descriptor = Descriptor with { Models = models };
            DescriptorChanged?.Invoke();
        }

        public Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Descriptor);

        public Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
        {
            Starts++;
            OnStart?.Invoke();
            return Task.FromResult<IRuntimeSession>(new Session(this));
        }

        private sealed class Session(UpdatingRuntime owner) : IRuntimeSession
        {
            public string? NativeSessionId => "native-session";

            public async Task RunTurnAsync(UserMessage message, TurnOptions options, TurnWriter writer, CancellationToken cancellationToken)
            {
                owner.Turns.Enqueue(options);
                if (owner.FirstTurn.TrySetResult(options) && owner.HoldFirstTurn)
                {
                    await owner.ReleaseFirstTurn.Task.WaitAsync(cancellationToken);
                }
                await writer.TextAsync("ok");
            }

            public ValueTask<bool> SteerAsync(UserMessage message, CancellationToken cancellationToken) => ValueTask.FromResult(false);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ModelClient(Action? onChanged = null) : IAgentHostClient
    {
        public List<ModelsChangedNotification> Notifications { get; } = [];
        public void OnModelsChanged(ModelsChangedNotification notification)
        {
            Notifications.Add(notification);
            onChanged?.Invoke();
        }
        public void OnSessionEvent(SessionEventNotification notification) { }
        public void OnMcpChanged(McpChangedNotification notification) { }
        public void OnAssetsChanged(AssetsChangedNotification notification) { }
        public Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolApprovalResponse(false));
        public Task<McpElicitResponse> RequestMcpElicitationAsync(McpElicitRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpElicitResponse("cancel"));
        public Task<McpOpenUrlResponse> OpenMcpUrlAsync(McpOpenUrlRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpOpenUrlResponse(false));
        public Task<UserInputResponse> RequestUserInputAsync(UserInputRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserInputResponse("declined", []));
        public Task<PlanReviewResponse> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlanReviewResponse.Dismiss);
    }
}
