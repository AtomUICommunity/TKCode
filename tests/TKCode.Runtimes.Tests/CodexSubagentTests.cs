using System.Collections.Concurrent;
using System.Text.Json;
using TKCode.Agent;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Runtimes.Codex;
using TKCode.Runtimes.Tests.Fixtures;
using Xunit;

namespace TKCode.Runtimes.Tests;

/// <summary>
/// Codex 的子 Agent 线程进入各自的转录（docs/design/subagent-view.md §5.6）：thread/started（parentThreadId 为主线程）登记，
/// 之前到达的通知补上；spawnAgent 关联到子线程并带 SubagentDetails；子线程的审批带名称前缀。
/// </summary>
public sealed class CodexSubagentTests
{
    private const string T = "019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f";
    private const string TT = "019a6f1e-3c4d-7e5f-9061-7b8c9d0e1f20";
    private const string C = "019a6f20-aaaa-7bbb-8ccc-000000000001";

    static CodexSubagentTests() => CodexSession.ClientVersion = "0.1.0";

    private static TranscriptLine Server(string json) => new(false, json.Replace("MAIN_TURN", TT).Replace("MAIN", T).Replace("CHILD", C));
    private static TranscriptLine Client(string json) => new(true, json);

    [Fact]
    public async Task ChildThread_GoesToItsOwnTranscript_AndIsLinkedToSpawnAgent()
    {
        var peer = new ScriptedPeer();
        var runtime = new CodexFixtureTests.FixtureRuntime(peer);
        var model = RuntimeModels.Create(runtime, "gpt-6.1-sol") with
        {
            Compat = new ModelCompat { ReasoningEfforts = ["low", "medium", "high", "xhigh"] },
        };
        var transcripts = new ConcurrentDictionary<string, Recorded>();
        var approvals = new List<RuntimeApprovalRequest>();
        var agent = new ExternalAgent(runtime, model, "/Users/dev/acme-app")
        {
            ThinkingLevel = ThinkingLevel.Medium,
            ApprovalHandler = (r, _) =>
            {
                approvals.Add(r);
                return ValueTask.FromResult(ApprovalDecision.AllowOnce);
            },
            SubagentHandler = (key, name, m) => transcripts.GetOrAdd(key, _ => new Recorded(name)).Subagent,
        };

        var handshake = TranscriptFixture.Load("codex-app-server.md", "### C-a", "C> ", "S> ");
        var turnStarted = handshake.ToList().FindIndex(l => l.Json.Contains("\"turn/started\""));
        var run = agent.PromptAsync(UserMessage.FromText("这个仓库是做什么的？"));
        await peer.RunAsync(handshake.Take(turnStarted + 1));
        await peer.RunAsync(
        [
            Server("""{"method":"item/started","params":{"threadId":"MAIN","turnId":"MAIN_TURN","item":{"type":"collabAgentToolCall","id":"call_spawn","tool":"spawnAgent","status":"inProgress","senderThreadId":"MAIN","receiverThreadIds":[],"agentsStates":{},"prompt":"Read the README.","model":"gpt-6.1-mini"}}}"""),
            // 子线程的通知先于 thread/started 到达
            Server("""{"method":"turn/started","params":{"threadId":"CHILD","turn":{"id":"ct1","items":[],"status":"inProgress"}}}"""),
            Server("""{"method":"thread/started","params":{"thread":{"id":"CHILD","parentThreadId":"MAIN","agentNickname":"Scout","agentRole":"explorer","model":"gpt-6.1-mini","source":{"subAgent":{"thread_spawn":{"parent_thread_id":"MAIN","depth":1}}}}}}"""),
            Server("""{"method":"item/completed","params":{"threadId":"CHILD","turnId":"ct1","item":{"type":"userMessage","id":"cu1","content":[{"type":"text","text":"Read the README."}]}}}"""),
            Server("""{"method":"item/started","params":{"threadId":"CHILD","turnId":"ct1","item":{"type":"agentMessage","id":"cm1","text":""}}}"""),
            Server("""{"method":"item/agentMessage/delta","params":{"threadId":"CHILD","turnId":"ct1","itemId":"cm1","delta":"I'll read it."}}"""),
            Server("""{"method":"item/completed","params":{"threadId":"CHILD","turnId":"ct1","item":{"type":"agentMessage","id":"cm1","text":"I'll read it."}}}"""),
            Server("""{"method":"item/started","params":{"threadId":"CHILD","turnId":"ct1","item":{"type":"commandExecution","id":"ccmd","command":"cat README.md","cwd":"/Users/dev/acme-app","status":"inProgress"}}}"""),
            Server("""{"id":90,"method":"item/commandExecution/requestApproval","params":{"threadId":"CHILD","turnId":"ct1","itemId":"ccmd","startedAtMs":1,"command":"cat README.md","cwd":"/Users/dev/acme-app"}}"""),
            Client("""{"id":90,"result":{"decision":"accept"}}"""),
            Server("""{"method":"item/completed","params":{"threadId":"CHILD","turnId":"ct1","item":{"type":"commandExecution","id":"ccmd","command":"cat README.md","cwd":"/Users/dev/acme-app","status":"completed","exitCode":0,"aggregatedOutput":"# Acme"}}}"""),
            Server("""{"method":"item/completed","params":{"threadId":"CHILD","turnId":"ct1","item":{"type":"agentMessage","id":"cm2","text":"It is the Acme app."}}}"""),
            Server("""{"method":"thread/tokenUsage/updated","params":{"threadId":"CHILD","turnId":"ct1","tokenUsage":{"total":{"inputTokens":999999,"cachedInputTokens":0,"outputTokens":1,"reasoningOutputTokens":0,"totalTokens":1000000}}}}"""),
            Server("""{"method":"turn/completed","params":{"threadId":"CHILD","turn":{"id":"ct1","items":[],"status":"completed","error":null}}}"""),
            Server("""{"method":"item/completed","params":{"threadId":"MAIN","turnId":"MAIN_TURN","item":{"type":"collabAgentToolCall","id":"call_spawn","tool":"spawnAgent","status":"completed","senderThreadId":"MAIN","receiverThreadIds":["CHILD"],"agentsStates":{"CHILD":{"status":"running"}},"prompt":"Read the README.","model":"gpt-6.1-mini"}}}"""),
            Server("""{"method":"item/completed","params":{"threadId":"MAIN","turnId":"MAIN_TURN","item":{"type":"agentMessage","id":"mm1","text":"Scout 读完了 README。"}}}"""),
            Server("""{"method":"turn/completed","params":{"threadId":"MAIN","turn":{"id":"MAIN_TURN","items":[],"status":"completed","error":null}}}"""),
        ]);
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        var child = transcripts[C];
        Assert.Equal(("Scout", "call_spawn"), (child.Name, child.Link));
        Assert.True(child.Ended);
        Assert.Collection(child.Messages,
            m => Assert.Equal("Read the README.", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(m).Content[0]).Text),
            m =>
            {
                var a = Assert.IsType<AssistantMessage>(m);
                Assert.Equal("I'll read it.", Assert.IsType<TextContent>(a.Content[0]).Text);
                Assert.Equal("shell", Assert.IsType<ToolCallContent>(a.Content[1]).Name);
            },
            m => Assert.Equal("# Acme", Assert.IsType<TextContent>(Assert.IsType<ToolResultMessage>(m).Content[0]).Text),
            m => Assert.Equal("It is the Acme app.", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(m).Content[0]).Text));
        Assert.Equal("[Scout] cat README.md", Assert.Single(approvals).Title);

        // 主线程：spawnAgent 显示为子 Agent 卡片，Details 为 SubagentDetails；子线程的内容与用量不混进来
        var call = agent.Messages.OfType<AssistantMessage>().SelectMany(m => m.Content).OfType<ToolCallContent>().Single();
        Assert.Equal("spawn_agent", call.Name);
        Assert.Equal("Read the README.", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("prompt").GetString());
        var details = JsonDocument.Parse(agent.Messages.OfType<ToolResultMessage>().Single().Details!).RootElement;
        Assert.Equal(("subagent", "Scout", "gpt-6.1-mini"),
            (details.GetProperty("kind").GetString(), details.GetProperty("name").GetString(), details.GetProperty("model").GetString()));
        Assert.DoesNotContain(agent.Messages.OfType<AssistantMessage>(), m => m.Content.OfType<TextContent>().Any(t => t.Text == "I'll read it."));
        Assert.True(Assert.IsType<AssistantMessage>(agent.Messages[^1]).Usage.Input < 999999);
    }

    private sealed class Recorded
    {
        public Recorded(string name)
        {
            Name = name;
            Subagent = new RuntimeSubagent(e =>
            {
                lock (Messages)
                {
                    switch (e)
                    {
                        case MessageEndEvent end:
                            Messages.Add(end.Message);
                            break;
                        case AgentEndEvent:
                            Ended = true;
                            break;
                    }
                }
                return ValueTask.CompletedTask;
            }, id => Link = id, (n, _) => Name = n ?? Name);
        }

        public string Name { get; private set; }
        public RuntimeSubagent Subagent { get; }
        public List<Message> Messages { get; } = [];
        public string? Link { get; private set; }
        public volatile bool Ended;
    }
}
