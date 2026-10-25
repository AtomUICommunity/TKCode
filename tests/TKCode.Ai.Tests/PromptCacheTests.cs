using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TKCode.Ai.Apis;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using Xunit;

namespace TKCode.Ai.Tests;

/// <summary>缓存标识：Anthropic 断点（参考 Claude Code）、TTL、分叉请求只读，以及 Chat / Responses 的缓存键与亲和头。</summary>
public sealed class PromptCacheTests
{
    private static readonly ModelInfo Opus = new()
    {
        Id = "claude-opus-5-5", Name = "Claude Opus 5.5", Provider = "relay", Api = KnownApis.AnthropicMessages, Reasoning = true,
        BaseUrl = "https://relay.example.com",
    };

    private static readonly string TextStream = Sse.Of(
        ("message_start", """{"type":"message_start","message":{"id":"m","usage":{"input_tokens":5,"output_tokens":1}}}"""),
        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
        ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
        ("message_stop", """{"type":"message_stop"}"""));

    private static readonly string ChatStream = string.Concat(
        """data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""", "\n\n", "data: [DONE]\n\n");

    private static AssistantMessage ToolCall(ModelInfo model, string id) =>
        new([new ThinkingContent("plan") { Signature = "SIG" }, new ToolCallContent(id, "read_file", """{"path":"a"}""")])
        {
            Api = model.Api, Provider = model.Provider, Model = model.Id, StopReason = StopReason.ToolUse,
        };

    private static AssistantMessage Reply(ModelInfo model, string text) =>
        new([new TextContent(text)]) { Api = model.Api, Provider = model.Provider, Model = model.Id, StopReason = StopReason.Stop };

    private static List<JsonObject> CacheControls(JsonNode? node)
    {
        var found = new List<JsonObject>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key == "cache_control")
                        {
                            found.Add(o);
                        }
                        else
                        {
                            Walk(value);
                        }
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a)
                    {
                        Walk(item);
                    }
                    break;
            }
        }
        Walk(node);
        return found;
    }

    private static async Task<JsonNode> AnthropicBody(ModelInfo model, LlmContext context, StreamOptions options)
    {
        var handler = new FakeHandler((HttpStatusCode.OK, TextStream));
        await Run.StreamAsync(new AnthropicMessagesApi(new HttpClient(handler)), model, context, options);
        return handler.Bodies[0];
    }

    [Fact]
    public async Task Anthropic_AnyEndpoint_PutsBreakpointsOnSystemAndLastMessage_LikeClaudeCode()
    {
        var context = Run.Context(UserMessage.FromText("读 a"), ToolCall(Opus, "toolu_1"),
            new ToolResultMessage("toolu_1", "read_file", [new TextContent("content of a")]));
        var body = await AnthropicBody(Opus, context, new StreamOptions { ApiKey = "k", SessionId = Guid.CreateVersion7().ToString("N") });

        // 中转站同样带断点；不用顶层自动缓存（旧 Bedrock 集成会 400）
        Assert.Null(body["cache_control"]);
        var markers = CacheControls(body);
        Assert.Equal(2, markers.Count);
        Assert.Same(body["system"]![0], markers[0]);
        var lastTurn = body["messages"]!.AsArray()[^1]!["content"]!.AsArray();
        Assert.Same(lastTurn[^1], markers[1]);
        Assert.Equal("tool_result", markers[1]["type"]!.GetValue<string>());
        // 默认 5 分钟：不发 ttl 字段
        Assert.All(markers, m => Assert.Equal("""{"type":"ephemeral"}""", m["cache_control"]!.ToJsonString()));
        // metadata.user_id 沿用 Claude Code 的形状，中转站据 session 做粘性路由
        Assert.Matches(new Regex("^user_[0-9a-f]{64}_account__session_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"),
            body["metadata"]!["user_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_OneHourTtl_AppliesToEveryBreakpoint()
    {
        var body = await AnthropicBody(Opus, Run.Context(UserMessage.FromText("hi")),
            new StreamOptions { ApiKey = "k", CacheTtl = PromptCacheTtl.OneHour });

        var markers = CacheControls(body);
        Assert.Equal(2, markers.Count);
        Assert.All(markers, m => Assert.Equal("1h", m["cache_control"]!["ttl"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Anthropic_SkipsSystemTurnsAndThinkingWhenPlacingTheMarker()
    {
        // Opus 5.5 支持对话中途的 system 消息：断点不能放在它上面，退到前面的 user 块
        var body = await AnthropicBody(Opus with { BaseUrl = null }, Run.Context(UserMessage.FromText("hi"), new SystemMessage("mode changed")),
            new StreamOptions { ApiKey = "k" });

        var messages = body["messages"]!.AsArray();
        Assert.Equal("system", messages[^1]!["role"]!.GetValue<string>());
        var marker = Assert.Single(CacheControls(body["messages"]));
        Assert.Equal("hi", marker["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_ForkRequest_ReadsAtSharedPrefixEnd_WithoutWritingItsOwnTail()
    {
        // 压缩：历史以助手回复结尾，追加摘要指令 → 断点在助手回复上
        var afterReply = Run.Context(UserMessage.FromText("q"), Reply(Opus, "answer"), UserMessage.FromText("summarize"));
        var body = await AnthropicBody(Opus, afterReply, new StreamOptions { ApiKey = "k", SkipCacheWrite = true });
        var marker = Assert.Single(CacheControls(body["messages"]));
        Assert.Equal("answer", marker["text"]!.GetValue<string>());

        // 历史以工具结果结尾：指令合并进同一个 user turn，断点留在工具结果上（主循环上一次的写入点）
        var afterTool = Run.Context(UserMessage.FromText("q"), ToolCall(Opus, "toolu_1"),
            new ToolResultMessage("toolu_1", "read_file", [new TextContent("x")]), UserMessage.FromText("summarize"));
        body = await AnthropicBody(Opus, afterTool, new StreamOptions { ApiKey = "k", SkipCacheWrite = true });
        var lastTurn = body["messages"]!.AsArray()[^1]!["content"]!.AsArray();
        Assert.Equal(["tool_result", "text"], lastTurn.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Same(lastTurn[0], Assert.Single(CacheControls(body["messages"])));
    }

    [Fact]
    public async Task Anthropic_CacheControlCanBeTurnedOffPerProvider()
    {
        var body = await AnthropicBody(Opus with { Compat = new ModelCompat { SupportsCacheControl = false } },
            Run.Context(UserMessage.FromText("hi")), new StreamOptions { ApiKey = "k" });
        Assert.Empty(CacheControls(body));
    }

    private static async Task<(JsonNode Body, HttpRequestMessage Request)> ChatRequest(ModelInfo model, LlmContext context, StreamOptions options)
    {
        var handler = new FakeHandler((HttpStatusCode.OK, ChatStream));
        await Run.StreamAsync(new OpenAIChatCompletionsApi(new HttpClient(handler)), model, context, options);
        return (handler.Bodies[0], handler.Requests[0]);
    }

    private static ModelInfo Chat(string id, string baseUrl) => new()
    {
        Id = id, Name = id, Provider = "p", Api = KnownApis.OpenAIChatCompletions, BaseUrl = baseUrl,
    };

    [Theory]
    [InlineData("https://api.deepseek.com")]
    [InlineData("https://relay.example.com/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    public async Task Chat_PromptCacheKey_IsSentToEveryEndpoint(string baseUrl)
    {
        var (body, _) = await ChatRequest(Chat("some-model", baseUrl), Run.Context(UserMessage.FromText("q")), new StreamOptions { SessionId = "s1" });
        Assert.Equal("tkcode:s1", body["prompt_cache_key"]!.GetValue<string>());
        // include_obfuscation 是 OpenAI 专有的流选项，不跟着缓存键发给别家
        Assert.Null(body["stream_options"]?["include_obfuscation"]);
        Assert.Empty(CacheControls(body));
    }

    [Fact]
    public async Task Chat_GeminiRejectsUnknownFields_SoNoPromptCacheKey()
    {
        var (body, _) = await ChatRequest(Chat("gemini-3-pro", "https://generativelanguage.googleapis.com/v1beta/openai"),
            Run.Context(UserMessage.FromText("q")), new StreamOptions { SessionId = "s1" });
        Assert.Null(body["prompt_cache_key"]);
    }

    [Theory]
    [InlineData("https://api.x.ai/v1", "x-grok-conv-id")]
    [InlineData("https://openrouter.ai/api/v1", "x-session-id")]
    [InlineData("https://opencode.ai/inference/go/openai/v1", "x-opencode-session")]
    public async Task Chat_SessionAffinityHeaders(string baseUrl, string header)
    {
        var (_, request) = await ChatRequest(Chat("m", baseUrl), Run.Context(UserMessage.FromText("q")), new StreamOptions { SessionId = "s1" });
        Assert.Equal("tkcode:s1", request.Headers.GetValues(header).Single());
    }

    [Fact]
    public async Task Chat_SessionAffinityHeader_IsNotSentToOtherProviders()
    {
        var (_, request) = await ChatRequest(Chat("m", "https://relay.example.com/v1"), Run.Context(UserMessage.FromText("q")), new StreamOptions { SessionId = "s1" });
        Assert.False(request.Headers.Contains("x-opencode-session"));
    }

    [Fact]
    public async Task OpenCodeGo_Responses_And_Messages_CarryTheSessionHeader_AndItIsStablePerSession()
    {
        var responses = new FakeHandler((HttpStatusCode.OK, "data: [DONE]\n\n"));
        var model = new ModelInfo { Id = "gpt-6-luna", Name = "m", Provider = "p", Api = KnownApis.OpenAIResponses, BaseUrl = "https://opencode.ai/inference/go/openai/v1" };
        var api = new OpenAIResponsesApi(new HttpClient(responses));
        await Run.StreamAsync(api, model, Run.Context(UserMessage.FromText("q")), new StreamOptions { SessionId = "s1" });
        await Run.StreamAsync(api, model, Run.Context(UserMessage.FromText("q2")), new StreamOptions { SessionId = "s1" });
        await Run.StreamAsync(api, model, Run.Context(UserMessage.FromText("q")), new StreamOptions { SessionId = "s2" });
        var values = responses.Requests.Select(r => r.Headers.GetValues("x-opencode-session").Single()).ToList();
        Assert.Equal(["tkcode:s1", "tkcode:s1", "tkcode:s2"], values);

        var messages = new FakeHandler((HttpStatusCode.OK, "data: [DONE]\n\n"));
        var claude = Opus with { BaseUrl = "https://opencode.ai/inference/go/anthropic" };
        await Run.StreamAsync(new AnthropicMessagesApi(new HttpClient(messages)), claude, Run.Context(UserMessage.FromText("q")), new StreamOptions { ApiKey = "k", SessionId = "s1" });
        Assert.Equal("tkcode:s1", messages.Requests[0].Headers.GetValues("x-opencode-session").Single());

        var other = new FakeHandler((HttpStatusCode.OK, "data: [DONE]\n\n"));
        await Run.StreamAsync(new AnthropicMessagesApi(new HttpClient(other)), Opus, Run.Context(UserMessage.FromText("q")), new StreamOptions { ApiKey = "k", SessionId = "s1" });
        Assert.False(other.Requests[0].Headers.Contains("x-opencode-session"));
    }

    [Fact]
    public async Task Chat_ClaudeModel_GetsBreakpointsOnSystemAndLastToolOrUserMessage()
    {
        var claude = Chat("anthropic/claude-opus-5-5", "https://openrouter.ai/api/v1");
        var context = Run.Context(UserMessage.FromText("q"), ToolCall(claude, "call_1"),
            new ToolResultMessage("call_1", "read_file", [new TextContent("x")]));
        var (body, _) = await ChatRequest(claude, context, new StreamOptions { SessionId = "s1", CacheTtl = PromptCacheTtl.OneHour });

        var messages = body["messages"]!.AsArray();
        var markers = CacheControls(body);
        Assert.Equal(2, markers.Count);
        Assert.Same(messages[0]!["content"]![0], markers[0]);
        Assert.Equal("tool", messages[^1]!["role"]!.GetValue<string>());
        Assert.Same(messages[^1]!["content"]![0], markers[1]);
        Assert.All(markers, m => Assert.Equal("1h", m["cache_control"]!["ttl"]!.GetValue<string>()));
        // 助手消息保持字符串，不放断点
        Assert.Null(messages[2]!["content"]);
    }

    [Fact]
    public async Task Chat_DashScopeQwen_UsesFiveMinuteBreakpoints_AndForkMarksTheMessageBeforeTheLast()
    {
        var qwen = Chat("qwen3-coder-plus", "https://dashscope.aliyuncs.com/compatible-mode/v1");
        var context = Run.Context(UserMessage.FromText("q"), Reply(qwen, "answer"), UserMessage.FromText("summarize"));
        var (body, _) = await ChatRequest(qwen, context, new StreamOptions { CacheTtl = PromptCacheTtl.OneHour, SkipCacheWrite = true });

        var messages = body["messages"]!.AsArray();
        var markers = CacheControls(body);
        Assert.Equal(2, markers.Count);
        Assert.Equal("q", markers[1]["text"]!.GetValue<string>());
        Assert.Equal("summarize", messages[^1]!["content"]!.GetValue<string>());
        // 百炼显式缓存只有 5 分钟
        Assert.All(markers, m => Assert.Null(m["cache_control"]!["ttl"]));
    }
}
