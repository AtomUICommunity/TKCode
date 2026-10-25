using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Agent.Coding.Tools.Agents;
using TKCode.AgentHost.Catalog;
using TKCode.AgentHost.Peers;
using TKCode.Ai;
using TKCode.Ai.Fake;
using TKCode.Ai.Messages;
using TKCode.Rpc;
using TKCode.Runtimes;
using Xunit;

namespace TKCode.AgentHost.Tests;

/// <summary>子 Agent 转录的打开、转发与查找（docs/design/subagent-view.md §5）。</summary>
public sealed class SubagentTranscriptTests
{
    private static SessionManager CreateManager(TimeSpan chunkDelay)
    {
        var catalog = new ModelCatalog(NullLogger<ModelCatalog>.Instance, new BuiltInCatalog(),
            new ModelDiscovery(new HttpClient(), NullLogger<ModelDiscovery>.Instance));
        return new SessionManager(new WireApiRegistry([new FakeWireApi(chunkDelay)]), catalog, new RuntimeRegistry([]),
            new PeerDirectory(NullLogger<PeerDirectory>.Instance), new TKCode.Mcp.McpManager(new TKCode.Mcp.McpHost(), null, () => []),
            NullLogger<SessionManager>.Instance);
    }

    private static string TempDir() => Directory.CreateTempSubdirectory("tkcode-subagent-").FullName;

    private static HistoryMessageDto[] ToHistory(IReadOnlyList<Message> messages) =>
        messages.Select(m => new HistoryMessageDto(m switch
        {
            UserMessage => "user",
            AssistantMessage => "assistant",
            _ => "toolResult",
        }, DateTimeOffset.Now, [])).ToArray();

    [Fact]
    public async Task OpenWhileRunning_HistoryReplayAndEvents_NeitherDuplicateNorLose()
    {
        await using var sessions = CreateManager(TimeSpan.FromMilliseconds(8));
        var client = new RecordingClient();
        sessions.AttachClient(client);
        var parent = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        // explore 有 ls：假模型先调用 ls，再回复，共两条助手消息
        var handle = await sessions.Coordinator.SpawnAsync(
            new SubagentRequest(parent.Id, "look", "List the files.", SubagentTypes.Explore, null, null, "call-1"), CancellationToken.None);
        await Task.Delay(120);

        var opened = sessions.Subagents.Open(parent.Id, "call-1", () => null, ToHistory);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(opened.Replay, e => e is MessageEnded);
        var after = client.SubagentEvents.Where(n => n.ToolCallId == "call-1").Select(n => n.Event).ToList();
        var started = opened.Replay.Concat(after).OfType<MessageStarted>().Select(m => m.MessageId).ToHashSet();
        var ended = after.OfType<MessageEnded>().Where(m => m.Role == "assistant").ToList();
        Assert.All(ended, m => Assert.Contains(m.MessageId, started));
        var total = sessions.Subagents.Find(parent.Id, handle.AgentId)!;
        var finalAssistants = sessions.Subagents.Open(parent.Id, "call-1", () => null, ToHistory).History.Count(h => h.Role == "assistant");
        Assert.True(finalAssistants >= 2);
        Assert.Equal(finalAssistants, opened.History.Count(h => h.Role == "assistant") + ended.Count);
        Assert.Equal("explore", total.Name);
        Assert.Equal(FakeWireApi.Models[1].Name, opened.Model);
    }

    [Fact]
    public async Task ClosedTranscript_StopsForwarding()
    {
        await using var sessions = CreateManager(TimeSpan.Zero);
        var client = new RecordingClient();
        sessions.AttachClient(client);
        var parent = sessions.Create(TempDir(), FakeWireApi.Models[1]);
        var handle = await sessions.Coordinator.SpawnAsync(
            new SubagentRequest(parent.Id, "hi", "Reply with a greeting.", SubagentTypes.Explore, null, "greeter", "call-2"), CancellationToken.None);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        // 没打开时不转发
        Assert.Empty(client.SubagentEvents);
        var opened = sessions.Subagents.Open(parent.Id, "call-2", () => null, ToHistory);
        Assert.False(opened.IsRunning);
        Assert.Equal(["user", "assistant"], opened.History.Select(h => h.Role));
        sessions.Subagents.Close(parent.Id, "call-2");
        await sessions.Coordinator.SendAsync(parent.Id, "greeter", "Again.", null, CancellationToken.None);
        await WaitUntilAsync(() => parent.Agent.Messages.OfType<UserMessage>().Any(m => m.Source == "task-notification"));
        Assert.Empty(client.SubagentEvents);
    }

    [Fact]
    public async Task AfterTheParentIsClosed_TranscriptIsFoundThroughTheIndex_OrTheLegacyDetailsFallback()
    {
        var cwd = TempDir();
        string parentId, parentFile, toolCallId;
        await using (var sessions = CreateManager(TimeSpan.Zero))
        {
            sessions.AttachClient(new RecordingClient());
            var parent = sessions.Create(cwd, FakeWireApi.Models[1]);
            parentId = parent.Id;
            parentFile = parent.Store.FilePath;
            await parent.Agent.PromptAsync(UserMessage.FromText(
                """mcp:agent {"description":"greet","prompt":"Reply with a greeting.","subagent_type":"explore","name":"Greeter"}"""));
            await parent.Agent.WaitForIdleAsync();
            toolCallId = parent.Agent.Messages.OfType<ToolResultMessage>().Single(r => r.ToolName == "agent").ToolCallId;
            var details = parent.Agent.Messages.OfType<ToolResultMessage>().Single(r => r.ToolName == "agent").Details!;
            Assert.Contains("\"model\":\"Fake Mini\"", details);
            await sessions.CloseAsync(parent.Id);
        }

        await using (var sessions = CreateManager(TimeSpan.Zero))
        {
            var opened = sessions.Subagents.Open(parentId, toolCallId, () => null, ToHistory);
            Assert.Equal("greeter", opened.Name);
            Assert.Equal("Fake Mini", opened.Model);
            Assert.Equal(["user", "assistant"], opened.History.Select(h => h.Role));

            // 旧转录没有索引：按父会话工具结果 Details 中的 agentId 匹配文件头
            File.Delete(Path.Combine(SubagentHub.DirectoryOf(parentId), "index.jsonl"));
            Assert.Throws<KeyNotFoundException>(() => sessions.Subagents.Open(parentId, toolCallId, () => null, ToHistory));
            var parentHistory = TKCode.Agent.Coding.Sessions.SessionStore.Read(parentFile).Messages;
            var legacy = sessions.Subagents.Open(parentId, toolCallId, () => parentHistory, ToHistory);
            Assert.Equal(opened.AgentId, legacy.AgentId);
            Assert.Equal(2, legacy.History.Count);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private sealed class RecordingClient : IAgentHostClient
    {
        private readonly List<SubagentEventNotification> _subagentEvents = [];

        public IReadOnlyList<SubagentEventNotification> SubagentEvents
        {
            get
            {
                lock (_subagentEvents)
                {
                    return _subagentEvents.ToArray();
                }
            }
        }

        public void OnSubagentEvent(SubagentEventNotification notification)
        {
            lock (_subagentEvents)
            {
                _subagentEvents.Add(notification);
            }
        }

        public void OnSessionEvent(SessionEventNotification notification)
        {
        }

        public void OnModelsChanged(ModelsChangedNotification notification)
        {
        }

        public Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolApprovalResponse(true));

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

        public Task<UserInputResponse> RequestUserInputAsync(UserInputRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(UserInputResponse.Decline);

        public Task<PlanReviewResponse> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlanReviewResponse.Dismiss);
    }
}
