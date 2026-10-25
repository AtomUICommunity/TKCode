using System.Net;
using System.Text.Json.Nodes;
using TKCode.Ai.Apis;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;
using Xunit;

namespace TKCode.Ai.Tests;

public sealed class OpenAIChatCompletionsApiTests
{
    private static readonly ModelInfo OpenAI = new()
    {
        Id = "gpt-5.5", Name = "GPT-5.5", Provider = "openai", Api = KnownApis.OpenAIChatCompletions,
        BaseUrl = "https://api.openai.com/v1", Reasoning = true,
    };

    private static readonly ModelInfo DeepSeek = new()
    {
        Id = "deepseek-v4-pro", Name = "DeepSeek", Provider = "deepseek", Api = KnownApis.OpenAIChatCompletions,
        BaseUrl = "https://api.deepseek.com", Reasoning = true,
    };

    private static string Chunks(params string[] json) => string.Concat(json.Select(j => $"data: {j}\n\n"));

    private static OpenAIChatCompletionsApi Api(FakeHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task ReasoningTextAndFragmentedToolCall_WithUsageChunk()
    {
        var stream = Chunks(
            """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"想一想"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"好的"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_00_a","type":"function","function":{"name":"read_file","arguments":""}}]}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.cs\"}"}}]}}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"id":"c1","choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":60},"completion_tokens_details":{"reasoning_tokens":5}}}""",
            "[DONE]");
        var (_, message) = await Run.StreamAsync(Api(new FakeHandler((HttpStatusCode.OK, stream))), DeepSeek, Run.Context(UserMessage.FromText("q")));

        Assert.Equal(StopReason.ToolUse, message.StopReason);
        var thinking = Assert.IsType<ThinkingContent>(message.Content[0]);
        Assert.Equal("想一想", thinking.Thinking);
        Assert.Equal("reasoning_content", thinking.Signature);
        Assert.Equal("好的", Assert.IsType<TextContent>(message.Content[1]).Text);
        var call = Assert.IsType<ToolCallContent>(message.Content[2]);
        Assert.Equal(("call_00_a", "read_file", "{\"path\":\"a.cs\"}"), (call.Id, call.Name, call.ArgumentsJson));
        Assert.Equal(new Usage(40, 20, 60, 0, 5), message.Usage);
    }

    [Fact]
    public async Task SwitchingContentKind_EndsThePreviousBlockImmediately()
    {
        // Chat Completions 没有块结束事件：思考在正文 / 工具调用开始时就结束，而不是等到整条消息结束
        var stream = Chunks(
            """{"choices":[{"index":0,"delta":{"reasoning_content":"想一想"}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"好的"}}]}""",
            """{"choices":[{"index":0,"delta":{"reasoning_content":"再想想"}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"agent","arguments":"{}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]");
        var (events, message) = await Run.StreamAsync(Api(new FakeHandler((HttpStatusCode.OK, stream))), DeepSeek, Run.Context(UserMessage.FromText("q")));

        var order = events.Select(e => e switch
        {
            ThinkingEndEvent t => $"thinking-end:{t.ContentIndex}",
            TextEndEvent t => $"text-end:{t.ContentIndex}",
            TextStartEvent t => $"text-start:{t.ContentIndex}",
            ThinkingStartEvent t => $"thinking-start:{t.ContentIndex}",
            ToolCallStartEvent t => $"tool-start:{t.ContentIndex}",
            _ => null,
        }).OfType<string>().ToList();
        Assert.Equal(["thinking-start:0", "thinking-end:0", "text-start:1", "text-end:1", "thinking-start:2", "thinking-end:2", "tool-start:3"], order);
        Assert.Equal(StopReason.ToolUse, message.StopReason);
        Assert.Equal(["想一想", "再想想"], message.Content.OfType<ThinkingContent>().Select(t => t.Thinking));
    }

    [Fact]
    public async Task LengthWithToolCall_IsLength_SoTruncatedCallIsNotExecuted()
    {
        var stream = Chunks(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"length"}]}""",
            "[DONE]");
        var (_, message) = await Run.StreamAsync(Api(new FakeHandler((HttpStatusCode.OK, stream))), OpenAI, Run.Context(UserMessage.FromText("q")));

        Assert.Equal(StopReason.Length, message.StopReason);
        Assert.NotNull(Assert.IsType<ToolCallContent>(message.Content[0]).ArgumentsError);
    }

    [Fact]
    public async Task Refusal_IsShownAsText()
    {
        var stream = Chunks(
            """{"choices":[{"index":0,"delta":{"refusal":"I can't help with that."}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");
        var (_, message) = await Run.StreamAsync(Api(new FakeHandler((HttpStatusCode.OK, stream))), OpenAI, Run.Context(UserMessage.FromText("q")));

        Assert.Equal("I can't help with that.", Assert.IsType<TextContent>(message.Content[0]).Text);
    }

    [Fact]
    public async Task ToolCallAccumulation_LateIdAndName_AndSameIndexWithNewId()
    {
        var stream = Chunks(
            // 参数先到，id / name 晚到
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":\"a\"}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"read_file"}}]}}]}""",
            // 同一 index 出现不同的非空 id：新调用（一次性整段下发）
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_2","function":{"name":"read_file","arguments":"{\"path\":\"b\"}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");
        var (_, message) = await Run.StreamAsync(Api(new FakeHandler((HttpStatusCode.OK, stream))), OpenAI, Run.Context(UserMessage.FromText("q")));

        var calls = message.Content.OfType<ToolCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(("call_1", "{\"path\":\"a\"}"), (calls[0].Id, calls[0].ArgumentsJson));
        Assert.Equal(("call_2", "{\"path\":\"b\"}"), (calls[1].Id, calls[1].ArgumentsJson));
        // 有工具调用时即使 finish_reason 是 stop（Gemini 经 OpenRouter）也算 ToolUse
        Assert.Equal(StopReason.ToolUse, message.StopReason);
    }

    [Fact]
    public async Task ErrorChunkInsideHttp200_EndsWithError()
    {
        var stream = Chunks(
            """{"choices":[{"index":0,"delta":{"content":"par"}}]}""",
            """{"error":{"code":502,"message":"Upstream error"},"choices":[{"index":0,"delta":{},"finish_reason":"error"}]}""");
        var (_, message) = await Run.StreamAsync(Api(new FakeHandler((HttpStatusCode.OK, stream))), OpenAI, Run.Context(UserMessage.FromText("q")));

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("Upstream error", message.ErrorMessage);
    }

    [Fact]
    public async Task OpenAIRequest_UsesDeveloperRole_MaxCompletionTokens_NoneWhenOff_AndNoEmptyTools()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, Chunks("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""", "[DONE]")));
        var history = new AssistantMessage([new ToolCallContent("call_1", "read_file", "{}")])
        {
            Api = OpenAI.Api, Provider = OpenAI.Provider, Model = OpenAI.Id, StopReason = StopReason.ToolUse,
        };
        var context = new LlmContext("sys", [UserMessage.FromText("q"), history,
            new ToolResultMessage("call_1", "read_file", [new TextContent("x")]), UserMessage.FromText("next")], []);
        await Run.StreamAsync(Api(handler), OpenAI, context, new StreamOptions { ApiKey = "k", ThinkingLevel = ThinkingLevel.Off, SessionId = "s" });

        var body = handler.Bodies[0];
        Assert.Equal("developer", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.NotNull(body["max_completion_tokens"]);
        Assert.Null(body["max_tokens"]);
        Assert.Equal("none", body["reasoning_effort"]!.GetValue<string>());
        Assert.Null(body["tools"]);
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("tkcode:s", body["prompt_cache_key"]!.GetValue<string>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task DeepSeekRequest_EchoesReasoningContent_AndSendsThinkingWithEffort()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, Chunks("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""", "[DONE]")));
        var withReasoning = new AssistantMessage([new ThinkingContent("先读文件") { Signature = "reasoning_content" },
            new ToolCallContent("call_1", "read_file", "{\"path\":\"a\"}")])
        {
            Api = DeepSeek.Api, Provider = DeepSeek.Provider, Model = DeepSeek.Id, StopReason = StopReason.ToolUse,
        };
        var foreign = new AssistantMessage([new TextContent("来自其它模型")])
        {
            Api = KnownApis.AnthropicMessages, Provider = "anthropic", Model = "claude-opus-5-5", StopReason = StopReason.Stop,
        };
        var context = Run.Context(UserMessage.FromText("q0"), foreign, UserMessage.FromText("q"), withReasoning,
            new ToolResultMessage("call_1", "read_file", [new TextContent("x")]));
        await Run.StreamAsync(Api(handler), DeepSeek, context, new StreamOptions { ApiKey = "k", ThinkingLevel = ThinkingLevel.Medium });

        var body = handler.Bodies[0];
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.NotNull(body["max_tokens"]);
        Assert.Equal("enabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>()); // medium 不在 DeepSeek 集合中 → 向上取 high
        var assistants = body["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "assistant").ToList();
        Assert.Equal("", assistants[0]!["reasoning_content"]!.GetValue<string>()); // 带工具时所有轮次都要有该字段
        Assert.Equal("先读文件", assistants[1]!["reasoning_content"]!.GetValue<string>());
        Assert.Equal("{\"path\":\"a\"}", assistants[1]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenRouterReasoningDetails_AreMergedAndReplayedVerbatim()
    {
        var openRouter = OpenAI with { Provider = "openrouter", Id = "anthropic/claude-opus-5-5", BaseUrl = "https://openrouter.ai/api/v1" };
        var stream = Chunks(
            """{"choices":[{"index":0,"delta":{"reasoning":"Let","reasoning_details":[{"type":"reasoning.text","text":"Let","index":0,"format":"anthropic-claude-v1"}]}}]}""",
            """{"choices":[{"index":0,"delta":{"reasoning":" me","reasoning_details":[{"type":"reasoning.text","text":" me","signature":"sig","index":0,"format":"anthropic-claude-v1"}]}}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"toolu_1","function":{"name":"read_file","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""",
            "[DONE]");
        var handler = new FakeHandler((HttpStatusCode.OK, stream), (HttpStatusCode.OK, Chunks("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""", "[DONE]")));
        var (_, first) = await Run.StreamAsync(Api(handler), openRouter, Run.Context(UserMessage.FromText("q")));

        Assert.Equal("Let me", first.Content.OfType<ThinkingContent>().Single().Thinking);
        Assert.Null(handler.Bodies[0]["stream_options"]); // OpenRouter 总是返回 usage

        await Run.StreamAsync(Api(handler), openRouter, Run.Context(UserMessage.FromText("q"), first,
            new ToolResultMessage("toolu_1", "read_file", [new TextContent("x")])));
        var assistant = handler.Bodies[1]["messages"]!.AsArray().Single(m => m!["role"]!.GetValue<string>() == "assistant")!;
        var details = assistant["reasoning_details"]!.AsArray();
        Assert.Single(details);
        Assert.Equal("Let me", details[0]!["text"]!.GetValue<string>());
        Assert.Equal("sig", details[0]!["signature"]!.GetValue<string>());
        Assert.Equal("medium", handler.Bodies[0]["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task DashScope_MidConversationSystemBecomesUserReminder()
    {
        var qwen = DeepSeek with { Provider = "dashscope", Id = "qwen3.8-plus", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1" };
        var handler = new FakeHandler((HttpStatusCode.OK, Chunks("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""", "[DONE]")));
        await Run.StreamAsync(Api(handler), qwen, Run.Context(UserMessage.FromText("q"), new SystemMessage("auto-approve on")));

        var last = handler.Bodies[0]["messages"]!.AsArray()[^1]!;
        Assert.Equal("user", last["role"]!.GetValue<string>());
        // 百炼 Qwen 用显式缓存：字符串 content 改写成带 cache_control 的 text part
        Assert.Contains("<system-reminder>", last["content"]![0]!["text"]!.GetValue<string>());
        Assert.NotNull(last["content"]![0]!["cache_control"]);
        Assert.True(handler.Bodies[0]["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Retry_503ThenSuccess_ButQuota429IsNotRetried()
    {
        var ok = Chunks("""{"choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":"stop"}]}""", "[DONE]");
        var retried = new FakeHandler((HttpStatusCode.ServiceUnavailable, """{"error":{"message":"overloaded","type":"server_error"}}"""), (HttpStatusCode.OK, ok));
        var (_, message) = await Run.StreamAsync(Api(retried), OpenAI, Run.Context(UserMessage.FromText("q")));
        Assert.Equal(StopReason.Stop, message.StopReason);
        Assert.Equal(2, retried.Requests.Count);

        var quota = new FakeHandler(((HttpStatusCode)429, """{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}"""));
        var (_, failed) = await Run.StreamAsync(Api(quota), OpenAI, Run.Context(UserMessage.FromText("q")));
        Assert.Equal(StopReason.Error, failed.StopReason);
        Assert.Single(quota.Requests);
    }

    [Fact]
    public async Task StreamWithoutFinishReasonOrDone_IsTruncationError()
    {
        var stream = Chunks("""{"choices":[{"index":0,"delta":{"content":"part"}}]}""");
        var (_, message) = await Run.StreamAsync(Api(new FakeHandler((HttpStatusCode.OK, stream))), OpenAI, Run.Context(UserMessage.FromText("q")));
        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("truncated", message.ErrorMessage);
    }
}
