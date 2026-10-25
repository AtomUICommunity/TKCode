using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using TKCode.Ai.Http;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;
using TKCode.Ai.Transform;

namespace TKCode.Ai.Apis;

/// <summary>
/// Anthropic Messages API（POST /v1/messages），原生 HTTP + SSE 实现。
/// 规格：docs/protocols/anthropic-messages.md。模型族差异见 <see cref="CompatResolver.ResolveAnthropic"/>。
/// </summary>
public sealed class AnthropicMessagesApi(HttpClient http) : HttpWireApi(http)
{
    public const string ApiVersion = "2023-06-01";
    private const string OAuthBeta = "oauth-2025-04-20";
    private const string FastModeBeta = "fast-mode-2026-02-01";

    public override string Api => KnownApis.AnthropicMessages;

    protected override IReadOnlyList<Message> PrepareMessages(ModelInfo model, IReadOnlyList<Message> messages) =>
        MessageTransformer.Prepare(messages, model, MessageTransformer.AnthropicToolCallId);

    protected override HttpRequestMessage BuildRequest(ModelInfo model, LlmContext context, StreamOptions options)
    {
        var compat = CompatResolver.ResolveAnthropic(model);
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["max_tokens"] = options.MaxTokens ?? model.MaxTokens,
            ["stream"] = true,
        };

        // 缓存断点（规格 §8.3，参考 Claude Code）：系统提示一个、消息末尾一个，TTL 一致
        PromptCacheTtl? cacheTtl = compat.SupportsCacheControl ? options.CacheTtl : null;
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            var block = new JsonObject { ["type"] = "text", ["text"] = context.SystemPrompt };
            if (cacheTtl is { } ttl)
            {
                // 渲染顺序是 tools → system，这个断点同时缓存工具定义
                block["cache_control"] = PromptCache.Control(ttl);
            }
            body["system"] = new JsonArray((JsonNode)block);
        }
        if (cacheTtl is { } topTtl && compat.TopLevelCacheControl && !options.SkipCacheWrite)
        {
            // 只认顶层自动缓存的端点（Kimi）：断点随对话尾部移动
            body["cache_control"] = PromptCache.Control(topTtl);
        }

        body["messages"] = BuildMessages(model, context.Messages, compat, cacheTtl, options.SkipCacheWrite);
        if (!string.IsNullOrEmpty(options.SessionId))
        {
            body["metadata"] = new JsonObject { ["user_id"] = PromptCache.AnthropicUserId(options.SessionId) };
        }

        if (context.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in context.Tools)
            {
                var node = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = ParseSchema(tool.ParametersSchemaJson),
                };
                if (compat.SupportsEagerToolInputStreaming)
                {
                    node["eager_input_streaming"] = true;
                }
                tools.Add((JsonNode)node);
            }
            body["tools"] = tools;
            // 只使用 auto：最新模型上 any / tool 会 400（规格 §2.7）。
            body["tool_choice"] = new JsonObject { ["type"] = "auto" };
        }

        ApplyThinking(body, model, options, compat);
        var fast = options.FastMode && model.SupportsFastMode;
        if (fast)
        {
            // fast mode 是 research preview：顶层 speed 字段 + beta 头（规格 §2.1、§11）。
            body["speed"] = "fast";
        }

        var request = JsonPost(JoinUrl(model.BaseUrl, "https://api.anthropic.com", "/v1/messages"), body);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

        var betas = new List<string>(compat.Betas);
        if (fast)
        {
            betas.Add(FastModeBeta);
        }
        var apiKey = options.ApiKey ?? "";
        var isOAuth = apiKey.StartsWith("sk-ant-oat", StringComparison.Ordinal);
        if (isOAuth || compat.UseBearerAuth)
        {
            // x-api-key 与 Authorization 只能二选一（规格 §1.2）。
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (isOAuth)
            {
                betas.Add(OAuthBeta);
            }
        }
        else if (apiKey.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }
        if (betas.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", betas.Distinct()));
        }
        if (CompatResolver.ResolveSessionHeader(model) is { } affinity && !string.IsNullOrEmpty(options.SessionId))
        {
            request.Headers.TryAddWithoutValidation(affinity, PromptCache.Key(options.SessionId));
        }
        ApplyHeaders(request, model);
        return request;
    }

    private static void ApplyThinking(JsonObject body, ModelInfo model, StreamOptions options, CompatResolver.ResolvedAnthropicCompat compat)
    {
        if (compat.Thinking == AnthropicThinkingMode.None)
        {
            return;
        }
        var level = options.ThinkingLevel;
        if (compat.Thinking == AnthropicThinkingMode.Budget)
        {
            if (level == ThinkingLevel.Off)
            {
                return;
            }
            var maxTokens = body["max_tokens"]!.GetValue<int>();
            // budget_tokens ≥ 1024 且 < max_tokens（规格 §5.1）。
            var budget = Math.Min(ThinkingLevels.ToBudgetTokens(level), maxTokens - 1024);
            if (budget >= 1024)
            {
                body["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
            }
            return;
        }

        // Adaptive：思考深度只由 effort 控制。
        if (level == ThinkingLevel.Off)
        {
            if (compat.SupportsDisabledThinking)
            {
                // disabled 只能与 effort ≤ high 组合；不发送 effort 即使用模型默认值。
                body["thinking"] = new JsonObject { ["type"] = "disabled" };
                return;
            }
            // 始终思考的模型（Opus 5.5、Fable、Sonnet 5.5…）：以最低 effort 代替关闭。
            body["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
            if (compat.SupportsEffort)
            {
                body["output_config"] = new JsonObject
                {
                    ["effort"] = ThinkingLevels.ChooseEffort(ThinkingLevel.Off, model.Compat?.ReasoningEfforts ?? ThinkingLevels.AnthropicEfforts),
                };
            }
            return;
        }

        body["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
        if (compat.SupportsEffort)
        {
            body["output_config"] = new JsonObject
            {
                ["effort"] = ThinkingLevels.ChooseEffort(level, model.Compat?.ReasoningEfforts ?? ThinkingLevels.AnthropicEfforts),
            };
        }
    }

    /// <summary>
    /// 转换消息：第一条必须是 user；相邻同角色合并（tool_result 排在最前）；
    /// mid-conversation system 消息只在支持且位置合法时发送，否则降级为 &lt;system-reminder&gt; 文本。
    /// <paramref name="cacheTtl"/> 不为 null 时在消息里放一个缓存断点（见方法末尾）。
    /// </summary>
    private static JsonArray BuildMessages(ModelInfo model, IReadOnlyList<Message> messages, CompatResolver.ResolvedAnthropicCompat compat,
        PromptCacheTtl? cacheTtl, bool skipCacheWrite)
    {
        var turns = new List<(string Role, List<JsonNode> Blocks)>();
        JsonObject? sharedPrefixEnd = null;

        void Add(string role, IEnumerable<JsonNode> blocks)
        {
            var list = blocks.ToList();
            if (list.Count == 0)
            {
                return;
            }
            if (turns.Count > 0 && turns[^1].Role == role && role != "system")
            {
                turns[^1].Blocks.AddRange(list);
            }
            else
            {
                turns.Add((role, list));
            }
        }

        for (var i = 0; i < messages.Count; i++)
        {
            if (i == messages.Count - 1)
            {
                // 最后一条消息之前的末块（合并进同一 turn 之前取，按引用记录，不受后面排序影响）
                sharedPrefixEnd = LastCacheable(turns);
            }
            switch (messages[i])
            {
                case UserMessage user:
                    Add("user", UserBlocks(user.Content));
                    break;
                case ToolResultMessage result:
                    Add("user", [ToolResult(result)]);
                    break;
                case AssistantMessage assistant:
                    Add("assistant", AssistantBlocks(assistant, MessageTransformer.IsSameModel(assistant, model)));
                    break;
                case SystemMessage system:
                    var next = i + 1 < messages.Count ? messages[i + 1] : null;
                    var placementOk = turns.Count > 0 && turns[^1].Role == "user" && next is null or AssistantMessage;
                    if (compat.SupportsMidConversationSystem && placementOk)
                    {
                        Add("system", [TextBlock(system.Text)]);
                    }
                    else
                    {
                        Add("user", [TextBlock($"<system-reminder>\n{system.Text}\n</system-reminder>")]);
                    }
                    break;
            }
        }

        foreach (var (role, blocks) in turns)
        {
            if (role == "user")
            {
                // tool_result 必须排在 content 最前面（规格 §4-4）。
                blocks.Sort((a, b) => IsToolResult(b).CompareTo(IsToolResult(a)));
            }
        }

        // 同 Claude Code：消息里只放一个断点，在最后一条消息的末块。每次请求在新的末尾写入，
        // 并从断点往前（最多 20 个位置）找到上一次请求写下的条目读取。分叉请求（压缩）放在倒数第二条消息上：
        // 那里是与主循环共享前缀的末尾，只读不为自己独有的尾部写入新条目。
        if (cacheTtl is { } ttl && (skipCacheWrite ? sharedPrefixEnd : LastCacheable(turns)) is { } marker)
        {
            marker["cache_control"] = PromptCache.Control(ttl);
        }

        var array = new JsonArray();
        foreach (var (role, blocks) in turns)
        {
            var content = new JsonArray();
            foreach (var block in blocks)
            {
                content.Add(block);
            }
            array.Add((JsonNode)new JsonObject { ["role"] = role, ["content"] = content });
        }
        return array;
    }

    private static bool IsToolResult(JsonNode node) => node["type"]?.GetValue<string>() == "tool_result";

    /// <summary>可以放断点的最后一个块：跳过 system turn 与 thinking 块（放在它们上面会 400，规格 §8.1）。</summary>
    private static JsonObject? LastCacheable(List<(string Role, List<JsonNode> Blocks)> turns)
    {
        for (var t = turns.Count - 1; t >= 0; t--)
        {
            if (turns[t].Role == "system")
            {
                continue;
            }
            var blocks = turns[t].Blocks;
            for (var b = blocks.Count - 1; b >= 0; b--)
            {
                if (blocks[b] is JsonObject block && block["type"]?.GetValue<string>() is not ("thinking" or "redacted_thinking"))
                {
                    return block;
                }
            }
        }
        return null;
    }

    private static JsonNode TextBlock(string text) => new JsonObject { ["type"] = "text", ["text"] = text };

    private static JsonNode ImageBlock(ImageContent image) => new JsonObject
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MimeType, ["data"] = image.Data },
    };

    private static IEnumerable<JsonNode> UserBlocks(IReadOnlyList<ContentBlock> content)
    {
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent { Text.Length: > 0 } text:
                    yield return TextBlock(text.Text);
                    break;
                case ImageContent image:
                    yield return ImageBlock(image);
                    break;
            }
        }
    }

    private static IEnumerable<JsonNode> AssistantBlocks(AssistantMessage assistant, bool sameModel)
    {
        foreach (var block in assistant.Content)
        {
            switch (block)
            {
                case ThinkingContent { Redacted: true, Signature: { } data } when sameModel:
                    yield return new JsonObject { ["type"] = "redacted_thinking", ["data"] = data };
                    break;
                case ThinkingContent { Signature: { Length: > 0 } signature } thinking when sameModel:
                    // 逐字节原样回放，包括空的 thinking 文本（规格 §5.4）。
                    yield return new JsonObject { ["type"] = "thinking", ["thinking"] = thinking.Thinking, ["signature"] = signature };
                    break;
                case ThinkingContent thinking when thinking.Thinking.Length > 0 && !thinking.Redacted:
                    // 没有签名（中断或兼容端点未返回）无法作为 thinking 回放，转为文本。
                    yield return TextBlock(thinking.Thinking);
                    break;
                case TextContent { Text.Length: > 0 } text:
                    yield return TextBlock(text.Text);
                    break;
                case ToolCallContent call:
                    yield return new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson),
                    };
                    break;
            }
        }
    }

    private static JsonNode ToolResult(ToolResultMessage result)
    {
        var content = new JsonArray();
        var text = string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));
        if (result.IsError && !text.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            // 部分兼容端点忽略 is_error，把错误也写进文本（规格 §12）。
            text = "Error: " + text;
        }
        content.Add(TextBlock(text.Length > 0 ? text : "(no output)"));
        foreach (var image in result.Content.OfType<ImageContent>())
        {
            content.Add(ImageBlock(image));
        }
        var node = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = result.ToolCallId, ["content"] = content };
        if (result.IsError)
        {
            node["is_error"] = true;
        }
        return node;
    }

    protected override StreamState CreateState(ModelInfo model, AssistantMessageBuilder builder) => new State(builder);

    private sealed class State(AssistantMessageBuilder builder) : StreamState(builder)
    {
        private readonly Dictionary<int, int> _blocks = [];
        private long _input;
        private long _output;
        private long _cacheRead;
        private long _cacheWrite;
        private long? _thinkingTokens;
        private string? _stopReason;
        private bool _stopped;

        public override bool Handle(string eventType, string data)
        {
            var e = ParseJson(data);
            switch (GetString(e, "type") ?? eventType)
            {
                case "message_start":
                    var message = Get(e, "message");
                    Builder.ResponseId = GetString(message, "id");
                    ReadUsage(Get(message, "usage"));
                    break;
                case "content_block_start":
                    OnBlockStart(e);
                    break;
                case "content_block_delta":
                    OnBlockDelta(e);
                    break;
                case "content_block_stop":
                    if (Block(e) is { } stopped)
                    {
                        Builder.EndBlock(stopped);
                    }
                    break;
                case "message_delta":
                    if (GetString(Get(e, "delta"), "stop_reason") is { } stop)
                    {
                        _stopReason = stop;
                    }
                    ReadUsage(Get(e, "usage"));
                    break;
                case "message_stop":
                    _stopped = true;
                    return true;
                case "error":
                    var error = Get(e, "error");
                    var type = GetString(error, "type");
                    throw new ProviderException($"{GetString(error, "message") ?? "Stream error"}{(type is null ? "" : $" ({type})")}",
                        isRetryable: type is "overloaded_error" or "api_error" or "timeout_error");
            }
            // ping 与未知事件忽略。
            return false;
        }

        private int? Block(JsonElement e) =>
            Get(e, "index") is { ValueKind: JsonValueKind.Number } index && _blocks.TryGetValue(index.GetInt32(), out var block) ? block : null;

        private void OnBlockStart(JsonElement e)
        {
            if (Get(e, "index") is not { ValueKind: JsonValueKind.Number } indexElement)
            {
                return;
            }
            var block = Get(e, "content_block");
            int? created = GetString(block, "type") switch
            {
                "text" => Builder.BeginText(),
                "thinking" => Builder.BeginThinking(),
                "redacted_thinking" => Builder.BeginThinking(),
                "tool_use" => Builder.BeginToolCall(GetString(block, "id") ?? "", GetString(block, "name") ?? ""),
                _ => null,
            };
            if (created is not { } index)
            {
                return;
            }
            _blocks[indexElement.GetInt32()] = index;
            switch (GetString(block, "type"))
            {
                case "redacted_thinking":
                    Builder.SetRedacted(index, GetString(block, "data") ?? "");
                    break;
                case "text" when GetString(block, "text") is { Length: > 0 } initial:
                    Builder.AppendDelta(index, initial);
                    break;
            }
        }

        private void OnBlockDelta(JsonElement e)
        {
            if (Block(e) is not { } index)
            {
                return;
            }
            var delta = Get(e, "delta");
            switch (GetString(delta, "type"))
            {
                case "text_delta" when GetString(delta, "text") is { Length: > 0 } text:
                    Builder.AppendDelta(index, text);
                    break;
                case "thinking_delta" when GetString(delta, "thinking") is { Length: > 0 } thinking:
                    Builder.AppendDelta(index, thinking);
                    break;
                case "input_json_delta" when GetString(delta, "partial_json") is { Length: > 0 } json:
                    Builder.AppendDelta(index, json);
                    break;
                case "signature_delta" when GetString(delta, "signature") is { } signature:
                    // 赋值而非追加（规格 §6.3）。
                    Builder.SetSignature(index, signature);
                    break;
            }
        }

        /// <summary>message_start 给出输入侧最终值；message_delta 的数字是累计值，覆盖而不是相加（规格 §6.4）。</summary>
        private void ReadUsage(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            if (usage.TryGetProperty("input_tokens", out var input) && input.ValueKind == JsonValueKind.Number)
            {
                _input = input.GetInt64();
            }
            if (usage.TryGetProperty("output_tokens", out var output) && output.ValueKind == JsonValueKind.Number)
            {
                _output = output.GetInt64();
            }
            if (usage.TryGetProperty("cache_read_input_tokens", out var read) && read.ValueKind == JsonValueKind.Number)
            {
                _cacheRead = read.GetInt64();
            }
            if (usage.TryGetProperty("cache_creation_input_tokens", out var write) && write.ValueKind == JsonValueKind.Number)
            {
                _cacheWrite = write.GetInt64();
            }
            if (GetLong(Get(usage, "output_tokens_details"), "thinking_tokens") is > 0 and var thinking)
            {
                _thinkingTokens = thinking;
            }
            Builder.Usage = new Usage(_input, _output, _cacheRead, _cacheWrite, _thinkingTokens);
        }

        public override void Complete()
        {
            if (!_stopped)
            {
                throw new ProviderException("Stream ended before message_stop.", isRetryable: true);
            }
            // 输出撞上模型窗口：按截断处理，同时标记超窗，Agent 在下一次请求前压缩（docs/design/compaction.md §9）
            Builder.ContextOverflow = _stopReason == "model_context_window_exceeded";
            Builder.StopReason = _stopReason switch
            {
                "tool_use" => StopReason.ToolUse,
                "end_turn" or "stop_sequence" or "pause_turn" or null => Builder.HasToolCalls ? StopReason.ToolUse : StopReason.Stop,
                "max_tokens" or "model_context_window_exceeded" => StopReason.Length,
                // refusal：HTTP 200 但本轮作废，工具调用不得执行（规格 §7）。
                "refusal" => throw new ProviderException("The model declined to respond (refusal)."),
                _ => throw new ProviderException($"Unknown stop_reason: {_stopReason}"),
            };
        }
    }
}
