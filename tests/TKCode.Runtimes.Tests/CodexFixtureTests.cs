using System.Text.Json;
using TKCode.Agent;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Runtimes.Codex;
using TKCode.Runtimes.Json;
using TKCode.Runtimes.Tests.Fixtures;
using Xunit;

namespace TKCode.Runtimes.Tests;

/// <summary>docs/protocols/codex-app-server.md 附录 C 的夹具。</summary>
public sealed class CodexFixtureTests
{
    private const string Spec = "codex-app-server.md";
    private const string Cwd = "/Users/dev/acme-app";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    static CodexFixtureTests() => CodexSession.ClientVersion = "0.1.0";

    private static IReadOnlyList<TranscriptLine> Load(string heading) => TranscriptFixture.Load(Spec, heading, "C> ", "S> ");

    [Fact]
    public async Task A_TextTurn_WithReasoningSummaryAndUsage()
    {
        var (agent, peer, _) = Create();
        var events = Record(agent);
        var run = agent.PromptAsync(UserMessage.FromText("这个仓库是做什么的？"));
        await peer.RunAsync(Load("### C-a"));
        await run.WaitAsync(Timeout);

        var assistant = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(StopReason.Stop, assistant.StopReason);
        Assert.Equal("**Inspecting the repository layout**\n\nThe user wants\n\n a summary; README.md describes the app.",
            Assert.IsType<ThinkingContent>(assistant.Content[0]).Thinking);
        Assert.Equal("这是一个基于 Avalonia 的桌面 Agent 客户端。", Assert.IsType<TextContent>(assistant.Content[1]).Text);
        // 新线程：整轮用量 = total；Input 扣除缓存
        Assert.Equal(new Usage(12850 - 11264, 412, 11264, 0, 256), assistant.Usage);
        Assert.Equal("019a6f1e-2b3c-7d4e-8f50-6a7b8c9d0e1f", agent.NativeSessionId);
        Assert.IsType<AgentEndEvent>(events[^1]);
    }

    [Fact]
    public async Task B_CommandApproval_Accept_StreamsOutput()
    {
        RuntimeApprovalRequest? asked = null;
        var (agent, peer, _) = Create((r, _) =>
        {
            asked = r;
            return ValueTask.FromResult(ApprovalDecision.AllowOnce);
        });
        var events = Record(agent);
        var run = agent.PromptAsync(UserMessage.FromText("运行测试"));
        await peer.RunAsync(Load("### C-b"));
        await run.WaitAsync(Timeout);

        Assert.NotNull(asked);
        Assert.Equal(ApprovalKind.Command, asked.Kind);
        Assert.Equal("/bin/zsh -lc 'npm test'", asked.Title);
        Assert.StartsWith("Run the project's test suite", asked.Reason);

        Assert.Collection(agent.Messages,
            u => Assert.IsType<UserMessage>(u),
            a =>
            {
                var msg = Assert.IsType<AssistantMessage>(a);
                Assert.Equal("我先运行测试套件。", Assert.IsType<TextContent>(msg.Content[0]).Text);
                Assert.Equal("shell", Assert.IsType<ToolCallContent>(msg.Content[1]).Name);
            },
            r =>
            {
                var result = Assert.IsType<ToolResultMessage>(r);
                Assert.False(result.IsError);
                Assert.Contains("Tests  3 passed", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
            },
            a => Assert.Equal("3 个测试全部通过。", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<AssistantMessage>(a).Content)).Text));
        Assert.Equal(2, events.OfType<ToolExecutionUpdateEvent>().Count());
        Assert.Single(events.OfType<ToolExecutionStartEvent>());
        Assert.Equal(new Usage(28432 - 25344, 410, 25344, 0, 180), Assert.IsType<AssistantMessage>(agent.Messages[^1]).Usage);
    }

    [Fact]
    public async Task RequestUserInput_AnswersById_WithUserNote_AndShowsSyntheticQuestionTool()
    {
        var (agent, peer, _) = Create((_, _) => throw new InvalidOperationException("requestUserInput is not an approval"));
        RuntimeInputRequest? asked = null;
        agent.InputHandler = (request, _) =>
        {
            asked = request;
            return Task.FromResult(new TKCode.Agent.Questions.UserInputResult(TKCode.Agent.Questions.UserInputOutcome.Answered,
                [new("db_choice", ["SQLite"], "keep it small")]));
        };
        var events = Record(agent);
        var run = agent.PromptAsync(UserMessage.FromText("运行测试"));
        // 借用 C-b 的握手与收尾，把中间的命令审批换成一次提问
        var b = Load("### C-b").ToList();
        var toolStart = b.FindIndex(l => !l.FromClient && l.Json.Contains("\"type\":\"commandExecution\"") && l.Json.Contains("item/started"));
        var tail = b.FindIndex(l => !l.FromClient && l.Json.Contains("msg_1b2c3d4e5f607183") && l.Json.Contains("item/started"));
        const string thread = "019a6f20-1a2b-7c3d-8e4f-5a6b7c8d9e0f";
        const string turn = "019a6f20-2b3c-7d4e-9f50-6a7b8c9d0e1f";
        await peer.RunAsync([
            .. b.Take(toolStart),
            new TranscriptLine(false, """{"id":0,"method":"item/tool/requestUserInput","params":{"threadId":"@T","turnId":"@U","itemId":"call_rui","isBlocking":true,"autoResolutionMs":null,"questions":[{"id":"db_choice","header":"Database","question":"Which DB?","isOther":true,"isSecret":false,"options":[{"label":"Postgres (Recommended)","description":"Full featured"},{"label":"SQLite","description":"Zero setup"}]}]}}"""
                .Replace("@T", thread).Replace("@U", turn)),
            new TranscriptLine(true, """{"id":0,"result":{"answers":{"db_choice":{"answers":["SQLite","user_note: keep it small"]}}}}"""),
            new TranscriptLine(false, """{"method":"serverRequest/resolved","params":{"threadId":"@T","requestId":0}}""".Replace("@T", thread)),
            .. b.Skip(tail),
        ]);
        await run.WaitAsync(Timeout);

        Assert.Equal("call_rui", asked!.ToolCallId);
        var question = Assert.Single(asked.Questions);
        Assert.Equal(("db_choice", "Database", "Which DB?", true), (question.Id, question.Header, question.Question, question.AllowOther));
        Assert.Equal(["Postgres (Recommended)", "SQLite"], question.Options.Select(o => o.Label));
        // 没有对应的 ThreadItem：转录里合成一个 request_user_input 工具调用
        var call = agent.Messages.OfType<AssistantMessage>().SelectMany(m => m.Content).OfType<ToolCallContent>().Single(c => c.Id == "call_rui");
        Assert.Equal("request_user_input", call.Name);
        var end = events.OfType<ToolExecutionEndEvent>().Single(e => e.ToolCallId == "call_rui");
        var details = JsonDocument.Parse(end.Result.Details!).RootElement;
        Assert.Equal("answered", details.GetProperty("status").GetString());
        Assert.Equal("keep it small", details.GetProperty("questions")[0].GetProperty("text").GetString());
        Assert.Equal("3 个测试全部通过。", Assert.IsType<TextContent>(Assert.Single(Assert.IsType<AssistantMessage>(agent.Messages[^1]).Content)).Text);
    }

    [Fact]
    public async Task C_FileChangeApproval_Decline_UsesPatchFromItem()
    {
        RuntimeApprovalRequest? asked = null;
        var (agent, peer, _) = Create((r, _) =>
        {
            asked = r;
            return ValueTask.FromResult(ApprovalDecision.Deny());
        });
        var run = agent.PromptAsync(UserMessage.FromText("把 README 的标题改成 Acme"));
        // TKCode 的默认沙箱是 workspace-write（夹具用 read-only 演示另一种取值）
        await peer.RunAsync(Load("### C-c"), e => e.Replace("\"sandbox\":\"read-only\"", "\"sandbox\":\"workspace-write\""));
        await run.WaitAsync(Timeout);

        Assert.NotNull(asked);
        Assert.Equal(ApprovalKind.FileChange, asked.Kind);
        Assert.Equal("/Users/dev/acme-app/README.md", asked.Title);
        Assert.Contains("+# Acme", asked.ArgumentsJson);
        var result = agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.True(result.IsError);
        Assert.Equal("The user declined this change.", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task D_InterruptWhileApprovalPending_DoesNotReplyToApproval()
    {
        ExternalAgent? self = null;
        var (agent, peer, _) = Create(async (_, ct) =>
        {
            self!.Abort();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return ApprovalDecision.AllowOnce;
        });
        self = agent;
        var run = agent.PromptAsync(UserMessage.FromText("清理构建产物"));
        await peer.RunAsync(Load("### C-d"));
        await run.WaitAsync(Timeout);

        var result = agent.Messages.OfType<ToolResultMessage>().Single();
        Assert.Equal("Operation aborted.", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        Assert.Equal(StopReason.Aborted, Assert.IsType<AssistantMessage>(agent.Messages[^1]).StopReason);
        // 审批请求 0 已被服务端清除，TKCode 不能再回复
        Assert.Null(await peer.ReadExtraAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task E_TurnFailed_ThenSecondTurnFailsImmediately()
    {
        var (agent, peer, _) = Create();
        var lines = Load("### C-e");
        var second = lines.ToList().FindIndex(l => l.FromClient && l.Json.Contains("\"继续\""));
        var run = agent.PromptAsync(UserMessage.FromText("解释 src/main.ts"));
        await peer.RunAsync(lines.Take(second));
        await run.WaitAsync(Timeout);
        var first = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(StopReason.Error, first.StopReason);
        Assert.Equal("unexpected status 503 Service Unavailable: upstream connect error", first.ErrorMessage);

        run = agent.PromptAsync(UserMessage.FromText("继续"));
        await peer.RunAsync(lines.Skip(second));
        await run.WaitAsync(Timeout);
        Assert.Equal("You've hit your usage limit. Try again later.", Assert.IsType<AssistantMessage>(agent.Messages[^1]).ErrorMessage);
    }

    [Fact]
    public async Task PermissionModes_OverrideApprovalPolicyAndSandboxPerTurn()
    {
        var (agent, peer, _) = Create();
        var a = Load("### C-a");
        var firstTurn = a.ToList().FindIndex(l => l.FromClient && l.Json.Contains("\"turn/start\""));
        agent.PermissionMode = TKCode.Agent.PermissionMode.FullAccess;
        var run = agent.PromptAsync(UserMessage.FromText("这个仓库是做什么的？"));
        // thread/start 按完全授权启动：never + danger-full-access
        await peer.RunAsync(a.Take(firstTurn), e => e.Contains("\"thread/start\"")
            ? e.Replace("\"approvalPolicy\":\"on-request\"", "\"approvalPolicy\":\"never\"").Replace("\"sandbox\":\"workspace-write\"", "\"sandbox\":\"danger-full-access\"")
            : e);
        // 响应里的生效值仍是 on-request + workspaceWrite（夹具），所以第一轮显式覆盖
        var turnStart = JsonDocument.Parse((await peer.ReadExtraAsync(Timeout))!).RootElement.GetProperty("params");
        Assert.Equal("never", turnStart.GetProperty("approvalPolicy").GetString());
        Assert.Equal("dangerFullAccess", turnStart.GetProperty("sandboxPolicy").GetProperty("type").GetString());
        await peer.RunAsync(a.Skip(firstTurn + 1));
        await run.WaitAsync(Timeout);

        // 回到“询问”：approvalPolicy on-request + workspaceWrite（不联网）
        agent.PermissionMode = TKCode.Agent.PermissionMode.Ask;
        run = agent.PromptAsync(UserMessage.FromText("再说一遍"));
        var second = JsonDocument.Parse((await peer.ReadExtraAsync(Timeout))!).RootElement.GetProperty("params");
        Assert.Equal("on-request", second.GetProperty("approvalPolicy").GetString());
        Assert.Equal("workspaceWrite", second.GetProperty("sandboxPolicy").GetProperty("type").GetString());
        Assert.False(second.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean());
        agent.Abort();
    }

    [Fact]
    public async Task AdditionalDirectories_AreSentAsWritableRoots_WhenTheyChange()
    {
        var (agent, peer, _) = Create();
        IReadOnlyList<string> directories = ["/Users/dev/shared-lib"];
        agent.AdditionalDirectories = () => directories;
        var a = Load("### C-a");
        var firstTurn = a.ToList().FindIndex(l => l.FromClient && l.Json.Contains("\"turn/start\""));
        var run = agent.PromptAsync(UserMessage.FromText("这个仓库是做什么的？"));
        await peer.RunAsync(a.Take(firstTurn));
        // thread/start 不能携带可写目录：第一轮用 sandboxPolicy 覆盖（docs/design/projects.md §6.8）
        var first = JsonDocument.Parse((await peer.ReadExtraAsync(Timeout))!).RootElement.GetProperty("params").GetProperty("sandboxPolicy");
        Assert.Equal("workspaceWrite", first.GetProperty("type").GetString());
        Assert.Equal(["/Users/dev/shared-lib"], first.GetProperty("writableRoots").EnumerateArray().Select(r => r.GetString()));
        await peer.RunAsync(a.Skip(firstTurn + 1));
        await run.WaitAsync(Timeout);

        // 目录变了：下一轮覆盖成新的列表，不重启 app-server
        directories = ["/Users/dev/shared-lib", "/Users/dev/docs"];
        run = agent.PromptAsync(UserMessage.FromText("再说一遍"));
        var second = JsonDocument.Parse((await peer.ReadExtraAsync(Timeout))!).RootElement.GetProperty("params").GetProperty("sandboxPolicy");
        Assert.Equal(["/Users/dev/shared-lib", "/Users/dev/docs"], second.GetProperty("writableRoots").EnumerateArray().Select(r => r.GetString()));
        agent.Abort();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Images_AreSentAsDataUrls_OnlyWhenModelSupportsImages(bool supportsImages)
    {
        var (agent, peer, _) = Create();
        agent.Model = agent.Model with { SupportsImages = supportsImages };
        var a = Load("### C-a");
        var firstTurn = a.ToList().FindIndex(l => l.FromClient && l.Json.Contains("\"turn/start\""));
        var image = new ImageContent("iVBORw0KGgo=", "image/png") { Path = "/tmp/a.png" };
        var run = agent.PromptAsync(new UserMessage([image, new TextContent("看图 [Image #1: /tmp/a.png]")]));
        await peer.RunAsync(a.Take(firstTurn));
        var input = JsonDocument.Parse((await peer.ReadExtraAsync(Timeout))!).RootElement.GetProperty("params").GetProperty("input");
        if (supportsImages)
        {
            Assert.Equal("image", input[0].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,iVBORw0KGgo=", input[0].GetProperty("url").GetString());
            Assert.Equal("text", input[1].GetProperty("type").GetString());
        }
        else
        {
            // 不支持图片：只发送文本（路径仍在文本里）
            Assert.Equal("text", Assert.Single(input.EnumerateArray()).GetProperty("type").GetString());
        }
        await peer.RunAsync(a.Skip(firstTurn + 1));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public void ProbeHelpers_ParseVersionAccountAndModels()
    {
        var lines = Load("### C-f");
        JsonElement Result(int id) => JsonDocument.Parse(lines.Single(l => !l.FromClient && l.Json.Contains($"\"id\":{id},\"result\"")).Json)
            .RootElement.GetProperty("result").Clone();
        Assert.Equal("0.162.1", CodexRuntime.ParseVersion(Result(2)));
        Assert.Equal(("ChatGPT (pro)", true), CodexRuntime.DescribeAccount(Result(6)));
        Assert.Equal(((string?)null, false), CodexRuntime.DescribeAccount(JsonBuilder.Parse("""{"account":null,"requiresOpenaiAuth":true}""")));
        Assert.Equal(("Custom provider", true), CodexRuntime.DescribeAccount(JsonBuilder.Parse("""{"account":null,"requiresOpenaiAuth":false}""")));

        var runtime = new CodexRuntime();
        var model = runtime.ToModel(Result(5).GetProperty("data")[0], "gpt-6.1-sol");
        Assert.Equal("GPT-6.1-Sol", model.Name);
        Assert.True(model.SupportsFastMode);
        Assert.True(model.SupportsImages);
        Assert.Equal([ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh], ThinkingLevels.Available(model));
    }

    private static (ExternalAgent Agent, ScriptedPeer Peer, FixtureRuntime Runtime) Create(
        Func<RuntimeApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approve = null)
    {
        var peer = new ScriptedPeer();
        var runtime = new FixtureRuntime(peer);
        var model = RuntimeModels.Create(runtime, "gpt-6.1-sol") with
        {
            Compat = new ModelCompat { ReasoningEfforts = ["low", "medium", "high", "xhigh"] },
        };
        var agent = new ExternalAgent(runtime, model, Cwd) { ApprovalHandler = approve, ThinkingLevel = ThinkingLevel.Medium };
        return (agent, peer, runtime);
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

    internal sealed class FixtureRuntime(ScriptedPeer peer) : IAgentRuntime
    {
        public string Id => "codex";
        public string DisplayName => "Codex";
        public RuntimeDescriptor Descriptor { get; } = new("codex", "Codex", RuntimeStatus.Ready) { Capabilities = new() { SupportsSteering = true } };
        public event Action? DescriptorChanged { add { } remove { } }
        public Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Descriptor);

        public async Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken)
        {
            var session = new CodexSession(peer.ClientInput, peer.ClientOutput, request.Host, CodexRuntime.DeveloperInstructions);
            await session.InitializeAsync(cancellationToken);
            await session.StartThreadAsync(request.Cwd, request.Options, request.ResumeSessionId, cancellationToken);
            return session;
        }
    }

    [Fact]
    public void ReadOnlyMode_StartsThreadWithReadOnlySandbox()
    {
        var session = new CodexSession(new MemoryStream(), new MemoryStream(), new NullRuntimeHost(), null);
        var json = session.ThreadParams("/tmp", new CodexThreadSettings(null, "on-request", FullAccess: true, false, null, ReadOnly: true), null);
        Assert.Equal("read-only", json.GetProperty("sandbox").GetString());
    }

    private sealed class NullRuntimeHost : IRuntimeHost
    {
        public ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalDecision.Deny());

        public void OnNativeSessionId(string nativeSessionId)
        {
        }
    }
}
