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
/// OpenAI Chat Completions（POST /chat/completions）及其兼容端点
/// （DeepSeek、Kimi、智谱、通义、OpenRouter、vLLM、Ollama、LM Studio…）。
/// 厂商差异见 <see cref="CompatResolver.ResolveChat"/>。
/// </summary>
public sealed class OpenAIChatCompletionsApi(HttpClient http) : HttpWireApi(http)
{
    private static readonly string[] ReasoningFields = ["reasoning_content", "reasoning", "reasoning_text"];

    /// <summary>OpenRouter reasoning_details 在 ThinkingContent.Signature 中的前缀。</summary>
    internal const string ReasoningDetailsPrefix = "reasoning_details:";

    public override string Api => KnownApis.OpenAIChatCompletions;

    protected override IReadOnlyList<Message> PrepareMessages(ModelInfo model, IReadOnlyList<Message> messages) =>
        MessageTransformer.Prepare(messages, model, MessageTransformer.ChatToolCallId,
            CompatResolver.ResolveChat(model).ReplayReasoningAcrossProviderModels);

    protected override HttpRequestMessage BuildRequest(ModelInfo model, LlmContext context, StreamOptions options)
    {
        var compat = CompatResolver.ResolveChat(model);
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = BuildMessages(model, context, compat),
            ["stream"] = true,
        };
        if (compat.SupportsCacheControl)
        {
            // 1 小时 TTL 只对 Claude 后端有意义；百炼显式缓存只有 5 分钟
            var ttl = model.Id.Contains("claude", StringComparison.OrdinalIgnoreCase) ? options.CacheTtl : PromptCacheTtl.FiveMinutes;
            ApplyCacheControl(body["messages"]!.AsArray(), ttl, options.SkipCacheWrite);
        }
        if (compat.SupportsUsageInStreaming || compat.SupportsIncludeObfuscation)
        {
            var streamOptions = new JsonObject();
            if (compat.SupportsUsageInStreaming)
            {
                streamOptions["include_usage"] = true;
            }
            if (compat.SupportsIncludeObfuscation)
            {
                streamOptions["include_obfuscation"] = false;
            }
            body["stream_options"] = streamOptions;
        }
        if (compat.SupportsPromptCacheKey && !string.IsNullOrEmpty(options.SessionId))
        {
            body["prompt_cache_key"] = PromptCache.Key(options.SessionId);
        }
        if (compat.SupportsStore)
        {
            body["store"] = false;
        }
        body[compat.MaxTokensField] = options.MaxTokens ?? model.MaxTokens;

        if (context.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in context.Tools)
            {
                tools.Add((JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = ParseSchema(tool.ParametersSchemaJson),
                    },
                });
            }
            body["tools"] = tools;
        }
        else if (compat.SendEmptyToolsWithHistory &&
                 context.Messages.OfType<AssistantMessage>().Any(m => m.Content.OfType<ToolCallContent>().Any()))
        {
            // 历史里有工具调用但本次没有工具：部分代理（LiteLLM 等）要求显式给出空数组。
            body["tools"] = new JsonArray();
        }

        ApplyThinking(body, model, options.ThinkingLevel, compat);
        if (options.FastMode && model.SupportsFastMode)
        {
            // 响应里的 service_tier 会回填为 priority（规格 §2）。
            body["service_tier"] = "fast";
        }

        var request = JsonPost(JoinUrl(model.BaseUrl, "https://api.openai.com/v1", "/chat/completions"), body);
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
        if (compat.SessionAffinityHeader is { } affinity && !string.IsNullOrEmpty(options.SessionId))
        {
            request.Headers.TryAddWithoutValidation(affinity, PromptCache.Key(options.SessionId));
        }
        ApplyHeaders(request, model);
        return request;
    }

    /// <summary>
    /// Claude / 百炼 Qwen 的显式缓存断点，布局同 Messages 协议（参考 Claude Code）：系统提示一个，
    /// 最后一条 user / tool 消息一个（分叉请求取最后一条消息之前的那条，只读不写）。
    /// 字符串 content 改写成单个 text part 才能挂 cache_control；助手消息保持字符串，不放断点。
    /// </summary>
    private static void ApplyCacheControl(JsonArray messages, PromptCacheTtl ttl, bool skipCacheWrite)
    {
        if (messages.Count > 0 && messages[0] is JsonObject first && first["role"]?.GetValue<string>() is "system" or "developer")
        {
            MarkCacheable(first, ttl);
        }
        var end = skipCacheWrite ? messages.Count - 1 : messages.Count;
        for (var i = end - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject message && message["role"]?.GetValue<string>() is "user" or "tool" && MarkCacheable(message, ttl))
            {
                return;
            }
        }
    }

    private static bool MarkCacheable(JsonObject message, PromptCacheTtl ttl)
    {
        switch (message["content"])
        {
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > 0:
                message["content"] = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text, ["cache_control"] = PromptCache.Control(ttl) });
                return true;
            case JsonArray { Count: > 0 } parts when parts[^1] is JsonObject last:
                last["cache_control"] = PromptCache.Control(ttl);
                return true;
            default:
                return false;
        }
    }

    /// <summary>按厂商的推理参数格式写入思考配置（规格 §8、§10）。省略字段不等于关闭思考。</summary>
    private static void ApplyThinking(JsonObject body, ModelInfo model, ThinkingLevel level, CompatResolver.ResolvedChatCompat compat)
    {
        if (!model.Reasoning)
        {
            return;
        }
        var off = level == ThinkingLevel.Off;
        var effort = ThinkingLevels.ChooseEffort(level, compat.ReasoningEfforts);
        switch (compat.ThinkingFormat)
        {
            case ChatThinkingFormat.OpenAIEffort:
                body["reasoning_effort"] = effort;
                break;
            case ChatThinkingFormat.ThinkingType:
                body["thinking"] = new JsonObject { ["type"] = off && compat.SupportsDisabledThinking ? "disabled" : "enabled" };
                break;
            case ChatThinkingFormat.ThinkingTypeAndEffort:
                if (off && compat.SupportsDisabledThinking)
                {
                    body["thinking"] = new JsonObject { ["type"] = "disabled" };
                }
                else
                {
                    body["thinking"] = new JsonObject { ["type"] = "enabled" };
                    body["reasoning_effort"] = effort;
                }
                break;
            case ChatThinkingFormat.EnableThinking:
                body["enable_thinking"] = !off;
                break;
            case ChatThinkingFormat.OpenRouter:
                body["reasoning"] = new JsonObject { ["effort"] = effort };
                break;
        }
    }

    private static JsonArray BuildMessages(ModelInfo model, LlmContext context, CompatResolver.ResolvedChatCompat compat)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            messages.Add((JsonNode)new JsonObject
            {
                ["role"] = model.Reasoning && compat.SupportsDeveloperRole ? "developer" : "system",
                ["content"] = context.SystemPrompt,
            });
        }

        var hasTools = context.Tools.Count > 0;
        var pendingImages = new List<ImageContent>();
        foreach (var message in context.Messages)
        {
            if (message is not ToolResultMessage && pendingImages.Count > 0)
            {
                messages.Add((JsonNode)ImagesFromTools(pendingImages));
                pendingImages.Clear();
            }

            switch (message)
            {
                case SystemMessage system when compat.SupportsMidConversationSystem:
                    messages.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = system.Text });
                    break;
                case SystemMessage system:
                    messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = $"<system-reminder>\n{system.Text}\n</system-reminder>" });
                    break;
                case UserMessage user:
                    messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = UserContent(user.Content) });
                    break;
                case AssistantMessage assistant when BuildAssistant(assistant, model, compat, hasTools) is { } node:
                    messages.Add((JsonNode)node);
                    break;
                case ToolResultMessage result:
                    var toolMessage = new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.ToolCallId,
                        ["content"] = TextOf(result.Content) is { Length: > 0 } text ? text : result.IsError ? "Error" : "(no output)",
                    };
                    if (compat.RequiresToolResultName)
                    {
                        toolMessage["name"] = result.ToolName;
                    }
                    messages.Add((JsonNode)toolMessage);
                    // Chat Completions 的 tool 消息只能是文本，图片放到随后的 user 消息里。
                    pendingImages.AddRange(result.Content.OfType<ImageContent>());
                    break;
            }
        }
        if (pendingImages.Count > 0)
        {
            messages.Add((JsonNode)ImagesFromTools(pendingImages));
        }
        return messages;
    }

    private static JsonObject? BuildAssistant(AssistantMessage assistant, ModelInfo model, CompatResolver.ResolvedChatCompat compat, bool hasTools)
    {
        var text = string.Concat(assistant.Content.OfType<TextContent>().Select(t => t.Text));
        var calls = assistant.Content.OfType<ToolCallContent>().ToList();
        var thinking = assistant.Content.OfType<ThinkingContent>().Where(t => !t.Redacted).ToList();
        if (text.Length == 0 && calls.Count == 0 && thinking.Count == 0)
        {
            return null;
        }

        // 助手内容用纯字符串（部分兼容端点会把数组结构原样镜像回来）。
        var node = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text : null };

        var canReplayReasoning = compat.ReplayReasoningAcrossProviderModels
            ? MessageTransformer.IsSameProvider(assistant, model)
            : MessageTransformer.IsSameModel(assistant, model);
        if (canReplayReasoning && thinking.Count > 0)
        {
            // Signature 记录该厂商的推理字段名；OpenRouter 的 reasoning_details 以原始 JSON 保存，原样回传。
            var details = thinking.Select(t => t.Signature).FirstOrDefault(s => s?.StartsWith(ReasoningDetailsPrefix, StringComparison.Ordinal) == true);
            if (details is not null)
            {
                node["reasoning_details"] = JsonNode.Parse(details[ReasoningDetailsPrefix.Length..]);
            }
            var field = thinking.Select(t => t.Signature).FirstOrDefault(s => s is not null && ReasoningFields.Contains(s)) ?? "reasoning_content";
            var reasoningText = string.Concat(thinking.Select(t => t.Thinking));
            if (reasoningText.Length > 0 || details is null)
            {
                node[field] = reasoningText;
            }
        }
        else if (compat.RequiresReasoningContentOnAssistant && hasTools)
        {
            node["reasoning_content"] = "";
        }

        if (calls.Count > 0)
        {
            var array = new JsonArray();
            foreach (var call in calls)
            {
                array.Add((JsonNode)new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson },
                });
            }
            node["tool_calls"] = array;
        }
        if (text.Length == 0 && calls.Count == 0)
        {
            node["content"] = "";
        }
        return node;
    }

    private static JsonNode UserContent(IReadOnlyList<ContentBlock> content)
    {
        if (content.All(c => c is TextContent))
        {
            return JsonValue.Create(TextOf(content))!;
        }
        var parts = new JsonArray();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent { Text.Length: > 0 } text:
                    parts.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ImageContent image:
                    parts.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{image.MimeType};base64,{image.Data}" },
                    });
                    break;
            }
        }
        return parts;
    }

    private static JsonObject ImagesFromTools(List<ImageContent> images) => new()
    {
        ["role"] = "user",
        ["content"] = UserContent([new TextContent("Attached image(s) from tool result:"), .. images]),
    };

    private static string TextOf(IReadOnlyList<ContentBlock> content) =>
        string.Concat(content.OfType<TextContent>().Select(t => t.Text));

    protected override StreamState CreateState(ModelInfo model, AssistantMessageBuilder builder) => new State(builder);

    private sealed class State(AssistantMessageBuilder builder) : StreamState(builder)
    {
        private readonly Dictionary<int, ToolAccumulator> _toolsByIndex = [];
        private readonly List<ToolAccumulator> _tools = [];
        private readonly JsonArray _reasoningDetails = [];
        private int? _textBlock;
        private int? _thinkingBlock;
        private int? _firstThinkingBlock;
        private string? _finishReason;
        private bool _done;

        /// <summary>
        /// 单个工具调用的累加状态。id / name 可能晚于首个片段到达，因此直到拿到 name 才创建内容块，
        /// 在此之前的参数片段先缓存（规格 §11-20）。
        /// </summary>
        private sealed class ToolAccumulator
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public int? Block { get; set; }
            public System.Text.StringBuilder PendingArguments { get; } = new();
        }

        public override bool Handle(string eventType, string data)
        {
            if (data.TrimStart().StartsWith("[DONE]", StringComparison.Ordinal))
            {
                _done = true;
                return true;
            }
            var chunk = ParseJson(data);
            if (Get(chunk, "error") is { ValueKind: JsonValueKind.Object or JsonValueKind.String } error)
            {
                // HTTP 200 的流里也可能出现错误（OpenAI / OpenRouter，规格 §5.6）。
                throw new ProviderException(error.ValueKind == JsonValueKind.String
                    ? error.GetString()!
                    : GetString(error, "message") ?? error.GetRawText(), isRetryable: IsRetryableErrorCode(error));
            }
            if (GetString(chunk, "id") is { } id)
            {
                Builder.ResponseId ??= id;
            }
            if (Get(chunk, "usage") is { ValueKind: JsonValueKind.Object } usage)
            {
                ReadUsage(usage);
            }
            if (Get(chunk, "choices") is not { ValueKind: JsonValueKind.Array } choices)
            {
                return false;
            }
            foreach (var choice in choices.EnumerateArray())
            {
                // 只处理 index 为 0 的候选（规格 §11-18）。
                if (choice.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number && index.GetInt32() != 0)
                {
                    continue;
                }
                if (Get(choice, "usage") is { ValueKind: JsonValueKind.Object } choiceUsage)
                {
                    ReadUsage(choiceUsage);
                }
                if (Get(choice, "delta") is { ValueKind: JsonValueKind.Object } delta)
                {
                    ReadDelta(delta);
                }
                if (GetString(choice, "finish_reason") is { } finish)
                {
                    _finishReason = finish;
                }
            }
            return false;
        }

        private void ReadDelta(JsonElement delta)
        {
            foreach (var field in ReasoningFields)
            {
                if (GetString(delta, field) is { Length: > 0 } reasoning)
                {
                    AppendThinking(reasoning, field);
                    break;
                }
            }
            if (Get(delta, "reasoning_details") is { ValueKind: JsonValueKind.Array } details)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    MergeReasoningDetail(detail);
                }
            }
            if (GetString(delta, "content") is { Length: > 0 } content)
            {
                AppendText(content);
            }
            // refusal 是独立通道，不能丢弃（规格 §11-19）。
            if (GetString(delta, "refusal") is { Length: > 0 } refusal)
            {
                AppendText(refusal);
            }
            if (Get(delta, "tool_calls") is { ValueKind: JsonValueKind.Array } calls)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    ReadToolCall(call);
                }
            }
        }

        private void AppendThinking(string text, string field)
        {
            if (_thinkingBlock is null)
            {
                EndOpenText();
                _thinkingBlock = Builder.BeginThinking();
                _firstThinkingBlock ??= _thinkingBlock;
                Builder.SetSignature(_thinkingBlock.Value, field);
            }
            Builder.AppendDelta(_thinkingBlock.Value, text);
        }

        private void AppendText(string text)
        {
            if (_textBlock is null)
            {
                EndOpenThinking();
                _textBlock = Builder.BeginText();
            }
            Builder.AppendDelta(_textBlock.Value, text);
        }

        /// <summary>
        /// Chat Completions 没有块结束事件：切换到另一类内容时，前一段思考 / 正文即已结束，立即发出结束事件，
        /// 否则界面会把思考一直显示为进行中，直到整条消息结束。
        /// </summary>
        private void EndOpenThinking()
        {
            if (_thinkingBlock is { } block)
            {
                Builder.EndBlock(block);
                _thinkingBlock = null;
            }
        }

        private void EndOpenText()
        {
            if (_textBlock is { } block)
            {
                Builder.EndBlock(block);
                _textBlock = null;
            }
        }

        /// <summary>OpenRouter reasoning_details：同一 index 的片段拼接 text / summary / data，其余字段取最新值。</summary>
        private void MergeReasoningDetail(JsonElement detail)
        {
            if (detail.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var incoming = JsonNode.Parse(detail.GetRawText())!.AsObject();
            JsonObject? existing = null;
            if (incoming["index"] is JsonValue indexValue)
            {
                existing = _reasoningDetails.OfType<JsonObject>().LastOrDefault(d =>
                    d["index"] is JsonValue v && v.ToJsonString() == indexValue.ToJsonString() &&
                    d["type"]?.ToJsonString() == incoming["type"]?.ToJsonString());
            }
            if (existing is null)
            {
                _reasoningDetails.Add((JsonNode)incoming);
                return;
            }
            foreach (var (key, value) in incoming.ToList())
            {
                incoming.Remove(key);
                if (key is "text" or "summary" or "data" && existing[key] is JsonValue old && value is JsonValue add &&
                    old.TryGetValue<string>(out var a) && add.TryGetValue<string>(out var b))
                {
                    existing[key] = a + b;
                }
                else
                {
                    existing[key] = value;
                }
            }
        }

        private void ReadToolCall(JsonElement call)
        {
            var function = Get(call, "function");
            var id = GetString(call, "id");
            var name = GetString(function, "name");
            var hasIndex = call.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number;

            ToolAccumulator? acc = null;
            if (hasIndex && _toolsByIndex.TryGetValue(indexElement.GetInt32(), out var byIndex))
            {
                // 同一 index 上出现不同的非空 id：视为新的调用。
                acc = !string.IsNullOrEmpty(id) && byIndex.Id.Length > 0 && byIndex.Id != id ? null : byIndex;
            }
            if (acc is null && !hasIndex && !string.IsNullOrEmpty(id))
            {
                acc = _tools.LastOrDefault(t => t.Id == id);
            }
            if (acc is null)
            {
                acc = new ToolAccumulator();
                _tools.Add(acc);
                if (hasIndex)
                {
                    _toolsByIndex[indexElement.GetInt32()] = acc;
                }
            }

            if (acc.Block is null)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    acc.Id = id;
                }
                if (!string.IsNullOrEmpty(name))
                {
                    acc.Name += name;
                }
            }
            if (GetString(function, "arguments") is { Length: > 0 } arguments)
            {
                if (acc.Block is { } block)
                {
                    Builder.AppendDelta(block, arguments);
                }
                else
                {
                    acc.PendingArguments.Append(arguments);
                }
            }
            if (acc.Block is null && acc.Name.Length > 0)
            {
                Start(acc);
            }
        }

        private void Start(ToolAccumulator acc)
        {
            if (acc.Id.Length == 0)
            {
                acc.Id = "call_" + Guid.NewGuid().ToString("N")[..24];
            }
            EndOpenThinking();
            EndOpenText();
            acc.Block = Builder.BeginToolCall(acc.Id, acc.Name.Length > 0 ? acc.Name : "unknown");
            if (acc.PendingArguments.Length > 0)
            {
                Builder.AppendDelta(acc.Block.Value, acc.PendingArguments.ToString());
                acc.PendingArguments.Clear();
            }
        }

        private void ReadUsage(JsonElement usage)
        {
            var prompt = GetLong(usage, "prompt_tokens");
            var details = Get(usage, "prompt_tokens_details");
            var cached = GetLong(details, "cached_tokens");
            if (cached == 0)
            {
                cached = GetLong(usage, "prompt_cache_hit_tokens");
            }
            if (cached == 0)
            {
                cached = GetLong(usage, "cached_tokens");
            }
            var cacheWrite = GetLong(details, "cache_write_tokens");
            var output = GetLong(usage, "completion_tokens");
            var reasoning = GetLong(Get(usage, "completion_tokens_details"), "reasoning_tokens");
            Builder.Usage = new Usage(Math.Max(0, prompt - cached - cacheWrite), output, cached, cacheWrite, reasoning > 0 ? reasoning : null);
        }

        public override void Complete()
        {
            if (_finishReason is null && !_done)
            {
                throw new ProviderException("Stream ended before finish_reason (truncated response).", isRetryable: true);
            }
            foreach (var acc in _tools.Where(t => t.Block is null))
            {
                Start(acc);
            }
            if (_reasoningDetails.Count > 0)
            {
                var block = _firstThinkingBlock ?? FirstThinkingOrNew();
                Builder.SetSignature(block, ReasoningDetailsPrefix + _reasoningDetails.ToJsonString());
            }

            // 规格 §5.5 / §11-21：length 优先判断，被截断的工具调用不得执行。
            Builder.StopReason = _finishReason switch
            {
                "length" or "model_length" or "model_context_window_exceeded" => StopReason.Length,
                "content_filter" or "sensitive" => throw new ProviderException($"The response was blocked by the provider's content filter ({_finishReason})."),
                "insufficient_system_resource" or "network_error" or "error" =>
                    throw new ProviderException($"Provider finish_reason: {_finishReason}", isRetryable: true),
                _ => Builder.HasToolCalls ? StopReason.ToolUse : StopReason.Stop,
            };
        }

        private int FirstThinkingOrNew()
        {
            // 只有 reasoning_details 而没有明文推理时，创建一个空的思考块承载回放数据。
            var block = Builder.BeginThinking();
            Builder.EndBlock(block);
            return block;
        }

        private static bool IsRetryableErrorCode(JsonElement error) =>
            error.ValueKind == JsonValueKind.Object &&
            (GetString(error, "type") ?? GetString(error, "code")) is "server_error" or "overloaded_error" or "rate_limit_exceeded";
    }
}
