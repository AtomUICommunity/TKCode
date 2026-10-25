using System.Collections.Concurrent;
using System.Text.Json;
using TKCode.Agent;
using TKCode.Ai.Messages;
using TKCode.Runtimes.Claude;
using TKCode.Runtimes.Tests.Fixtures;
using Xunit;

namespace TKCode.Runtimes.Tests;

/// <summary>
/// Claude 的子 Agent 帧（parent_tool_use_id 非空）进入各自的转录（docs/design/subagent-view.md §5.5）：
/// 前台子 Agent 以父会话的 tool_result 结束，后台子 Agent 在主轮次结束后继续写入、以 task_notification 结束；
/// 子 Agent 的审批带名称前缀；Agent 工具的结果规范化为 SubagentDetails。
/// </summary>
public sealed class ClaudeSubagentTests
{
    private const string S = "5f0c2d1e-8a47-4c3b-9e21-7d6b4a9f0c11";
    private const string Uuid = "7c2e9b40-5a1f-4d63-8e07-9b3c2d1e0f99";

    private static TranscriptLine Server(string json) => new(false, json.Replace("SID", S));
    private static TranscriptLine Client(string json) => new(true, json.Replace("UUID", Uuid));

    [Fact]
    public async Task SubagentFrames_GoToTheirOwnTranscripts()
    {
        var peer = new ScriptedPeer();
        var runtime = new ClaudeFixtureRuntime(peer, new Queue<string>([Uuid]), S);
        var transcripts = new ConcurrentDictionary<string, Recorded>();
        var approvals = new List<RuntimeApprovalRequest>();
        var agent = new ExternalAgent(runtime, RuntimeModels.Create(runtime, ClaudeRuntime.DefaultModelId), "/Users/dev/proj")
        {
            ApprovalHandler = (r, _) =>
            {
                approvals.Add(r);
                return ValueTask.FromResult(ApprovalDecision.AllowOnce);
            },
            SubagentHandler = (key, name, model) => transcripts.GetOrAdd(key, _ => new Recorded(name, model)).Subagent,
        };

        var run = agent.PromptAsync(UserMessage.FromText("派两个子 Agent。"));
        await peer.RunAsync(TranscriptFixture.Load("claude-code-stream-json.md", "### A.1 (a)").Take(2));
        await peer.RunAsync(
        [
            Client("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"派两个子 Agent。"}]},"parent_tool_use_id":null,"uuid":"UUID"}"""),
            Server("""{"type":"system","subtype":"init","session_id":"SID","model":"claude-opus-5-5","permissionMode":"default"}"""),
            Server("""{"type":"assistant","message":{"id":"msg_m1","role":"assistant","content":[{"type":"text","text":"派两个子 Agent。"},{"type":"tool_use","id":"toolu_fg","name":"Agent","input":{"description":"find entry","prompt":"Find the entry point.","subagent_type":"Explore","model":"haiku"}},{"type":"tool_use","id":"toolu_bg","name":"Agent","input":{"description":"run tests","prompt":"Run the tests.","name":"tester","run_in_background":true}}]},"parent_tool_use_id":null,"session_id":"SID"}"""),
            Server("""{"type":"user","message":{"role":"user","content":"Find the entry point."},"parent_tool_use_id":"toolu_fg","session_id":"SID"}"""),
            Server("""{"type":"assistant","message":{"id":"msg_s1","role":"assistant","content":[{"type":"text","text":"Looking."},{"type":"tool_use","id":"toolu_s1","name":"Bash","input":{"command":"ls"}}]},"parent_tool_use_id":"toolu_fg","agent_id":"a_fg","session_id":"SID"}"""),
            Server("""{"type":"control_request","request_id":"req1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"},"tool_use_id":"toolu_s1","agent_id":"a_fg","description":"List files"}}"""),
            Client("""{"type":"control_response","response":{"subtype":"success","request_id":"req1","response":{"behavior":"allow","updatedInput":{"command":"ls"},"toolUseID":"toolu_s1","decisionClassification":"user_temporary"}}}"""),
            Server("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_s1","type":"tool_result","content":"Program.cs","is_error":false}]},"parent_tool_use_id":"toolu_fg","session_id":"SID"}"""),
            Server("""{"type":"assistant","message":{"id":"msg_s2","role":"assistant","content":[{"type":"text","text":"Entry is Program.cs."}]},"parent_tool_use_id":"toolu_fg","session_id":"SID"}"""),
            Server("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_bg","type":"tool_result","content":"Async agent launched","is_error":false}]},"parent_tool_use_id":null,"session_id":"SID","tool_use_result":{"status":"async_launched","agentId":"a_bg","description":"run tests","prompt":"Run the tests.","outputFile":"/tmp/a_bg.output"}}"""),
            Server("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_fg","type":"tool_result","content":[{"type":"text","text":"Entry is Program.cs."}],"is_error":false}]},"parent_tool_use_id":null,"session_id":"SID","tool_use_result":{"status":"completed","agentId":"a_fg","agentType":"Explore","content":[{"type":"text","text":"Entry is Program.cs."}],"resolvedModel":"claude-haiku-4-5","totalToolUseCount":1,"totalDurationMs":1000,"totalTokens":1234,"prompt":"Find the entry point."}}"""),
            Server("""{"type":"assistant","message":{"id":"msg_m2","role":"assistant","content":[{"type":"text","text":"入口是 Program.cs，测试在后台运行。"}]},"parent_tool_use_id":null,"session_id":"SID"}"""),
            Server("""{"type":"result","subtype":"success","is_error":false,"session_id":"SID","terminal_reason":"completed","usage":{"input_tokens":1,"output_tokens":1}}"""),
        ]);
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        // 主轮次之后：后台子 Agent 继续写入，task_notification 结束
        await peer.RunAsync(
        [
            Server("""{"type":"user","message":{"role":"user","content":"Run the tests."},"parent_tool_use_id":"toolu_bg","session_id":"SID"}"""),
            Server("""{"type":"assistant","message":{"id":"msg_b1","role":"assistant","content":[{"type":"text","text":"All tests pass."}]},"parent_tool_use_id":"toolu_bg","session_id":"SID"}"""),
            Server("""{"type":"system","subtype":"task_notification","task_id":"a_bg","tool_use_id":"toolu_bg","status":"completed","output_file":"/tmp/a_bg.output","summary":"done","session_id":"SID"}"""),
        ]);
        await WaitUntil(() => transcripts.TryGetValue("toolu_bg", out var b) && b.Ended);
        var background = transcripts["toolu_bg"];

        var foreground = transcripts["toolu_fg"];
        Assert.Equal(("Explore", "haiku", "toolu_fg"), (foreground.Name, foreground.Model, foreground.Link));
        Assert.Equal("claude-haiku-4-5", foreground.Described);
        Assert.True(foreground.Ended);
        Assert.Collection(foreground.Messages,
            m => Assert.Equal("Find the entry point.", Assert.IsType<TextContent>(Assert.IsType<UserMessage>(m).Content[0]).Text),
            m => Assert.Equal("Bash", Assert.IsType<ToolCallContent>(Assert.IsType<AssistantMessage>(m).Content[1]).Name),
            m => Assert.Equal("Program.cs", Assert.IsType<TextContent>(Assert.IsType<ToolResultMessage>(m).Content[0]).Text),
            m => Assert.Equal("Entry is Program.cs.", Assert.IsType<TextContent>(Assert.IsType<AssistantMessage>(m).Content[0]).Text));
        Assert.Equal("[Explore] List files", Assert.Single(approvals).Title);

        Assert.Equal(("tester", "toolu_bg"), (background.Name, background.Link));
        Assert.Equal(2, background.Messages.Count);

        // 主会话：子 Agent 的帧不混进来；Agent 结果规范化为 SubagentDetails
        Assert.DoesNotContain(agent.Messages.OfType<AssistantMessage>(), m => m.Content.OfType<TextContent>().Any(t => t.Text == "Looking."));
        var fgDetails = JsonDocument.Parse(agent.Messages.OfType<ToolResultMessage>().Single(r => r.ToolCallId == "toolu_fg").Details!).RootElement;
        Assert.Equal(("subagent", "Explore", "claude-haiku-4-5", "completed", false, 1234L),
            (fgDetails.GetProperty("kind").GetString(), fgDetails.GetProperty("name").GetString(), fgDetails.GetProperty("model").GetString(),
             fgDetails.GetProperty("status").GetString(), fgDetails.GetProperty("background").GetBoolean(), fgDetails.GetProperty("tokens").GetInt64()));
        var bgDetails = JsonDocument.Parse(agent.Messages.OfType<ToolResultMessage>().Single(r => r.ToolCallId == "toolu_bg").Details!).RootElement;
        Assert.True(bgDetails.GetProperty("background").GetBoolean());
        Assert.Equal("tester", bgDetails.GetProperty("name").GetString());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private sealed class Recorded
    {
        public Recorded(string name, string? model)
        {
            Name = name;
            Model = model;
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
            }, id => Link = id, (_, m) => Described = m ?? Described);
        }

        public string Name { get; }
        public string? Model { get; }
        public RuntimeSubagent Subagent { get; }
        public List<Message> Messages { get; } = [];
        public string? Link { get; private set; }
        public string? Described { get; private set; }
        public volatile bool Ended;
    }
}
