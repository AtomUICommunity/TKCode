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
/// OpenAI Responses API（POST /responses），无状态用法：store:false，每轮完整回放 input。
/// 规格：docs/protocols/openai-responses.md。
/// <para>
/// 回放策略：每个 output item 以 <c>response.output_item.done</c> 中的原始 JSON 原样保存
/// （reasoning → ThinkingContent.Signature；message → TextContent.ProviderState；function_call → ToolCallContent.ProviderState），
/// 同模型回放时逐字发回，保证 id / phase / encrypted_content / arguments 不变。
/// </para>
/// </summary>
public sealed class OpenAIResponsesApi(HttpClient http) : HttpWireApi(http)
{
    public override string Api => KnownApis.OpenAIResponses;

    protected override IReadOnlyList<Message> PrepareMessages(ModelInfo model, IReadOnlyList<Message> messages) =>
        MessageTransformer.Prepare(messages, model, MessageTransformer.ResponsesToolCallId);

    protected override HttpRequestMessage BuildRequest(ModelInfo model, LlmContext context, StreamOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["input"] = BuildInput(model, context),
            ["stream"] = true,
            ["store"] = false,
            ["max_output_tokens"] = Math.Max(16, options.MaxTokens ?? model.MaxTokens),
        };

        if (context.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in context.Tools)
            {
                tools.Add((JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ParseSchema(tool.ParametersSchemaJson),
                    // 显式非 strict：不设置时服务端会静默尝试 strict 规范化（规格 §2.3）。
                    ["strict"] = false,
                });
            }
            body["tools"] = tools;
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = true;
        }

        if (model.Reasoning)
        {
            // 选择模型支持的 effort；Off 在不支持 none 的模型上取最低档（例如 GPT-6.1 Sol）。
            var effort = ThinkingLevels.ChooseEffort(options.ThinkingLevel, model.Compat?.ReasoningEfforts);
            var reasoning = new JsonObject { ["effort"] = effort };
            // 只为 GPT 请求推理摘要；其它 Responses 服务可能拒绝 summary 字段。
            var modelId = model.Id.AsSpan(model.Id.LastIndexOf('/') + 1);
            if (modelId.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
            {
                reasoning["summary"] = "auto";
            }
            body["reasoning"] = reasoning;
            body["include"] = new JsonArray((JsonNode)"reasoning.encrypted_content");
        }
        if (options.FastMode && model.SupportsFastMode)
        {
            // 响应里的 service_tier 会回填为 priority（规格 §2）。
            body["service_tier"] = "fast";
        }
        if (!string.IsNullOrEmpty(options.SessionId))
        {
            body["prompt_cache_key"] = PromptCache.Key(options.SessionId);
        }

        var request = JsonPost(JoinUrl(model.BaseUrl, "https://api.openai.com/v1", "/responses"), body);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
        if (CompatResolver.ResolveSessionHeader(model) is { } affinity && !string.IsNullOrEmpty(options.SessionId))
        {
            request.Headers.TryAddWithoutValidation(affinity, PromptCache.Key(options.SessionId));
        }
        ApplyHeaders(request, model);
        return request;
    }

    private static JsonArray BuildInput(ModelInfo model, LlmContext context)
    {
        var input = new JsonArray();
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            input.Add((JsonNode)Message("developer", [InputText(context.SystemPrompt)]));
        }

        foreach (var message in context.Messages)
        {
            switch (message)
            {
                case SystemMessage system:
                    input.Add((JsonNode)Message("developer", [InputText(system.Text)]));
                    break;
                case UserMessage user:
                    input.Add((JsonNode)Message("user", UserParts(user.Content)));
                    break;
                case AssistantMessage assistant:
                    AddAssistant(input, assistant, MessageTransformer.IsSameModel(assistant, model));
                    break;
                case ToolResultMessage result:
                    input.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = result.ToolCallId,
                        ["output"] = ToolOutput(result),
                    });
                    break;
            }
        }
        return input;
    }

    private static void AddAssistant(JsonArray input, AssistantMessage assistant, bool sameModel)
    {
        var blocks = assistant.Content;
        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case ThinkingContent { Signature: { } raw } when sameModel && IsRawItem(raw, "reasoning"):
                    // reasoning 必须紧跟其后继 item，末尾孤立的 reasoning 不能回放（规格 §3.6-7）。
                    if (i + 1 < blocks.Count)
                    {
                        input.Add(JsonNode.Parse(raw));
                    }
                    break;
                case ThinkingContent:
                    // 无原始 item 的思考（其它模型的思考已在 transform 中转成文本）。
                    break;
                case TextContent { ProviderState: { } state } when sameModel && state.ValueKind == JsonValueKind.Object:
                    input.Add(JsonNode.Parse(state.GetRawText()));
                    break;
                case TextContent text when text.Text.Length > 0:
                    // 不带 id 的 assistant 文本（EasyInputMessage 形式）。
                    input.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = text.Text });
                    break;
                case ToolCallContent { ProviderState: { } state } when sameModel && state.ValueKind == JsonValueKind.Object:
                    input.Add(JsonNode.Parse(state.GetRawText()));
                    break;
                case ToolCallContent call:
                    input.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.Id,
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson,
                    });
                    break;
            }
        }
    }

    private static bool IsRawItem(string raw, string type)
    {
        if (!raw.StartsWith('{'))
        {
            return false;
        }
        try
        {
            return JsonNode.Parse(raw)?["type"]?.GetValue<string>() == type;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonObject Message(string role, IEnumerable<JsonNode> parts)
    {
        var content = new JsonArray();
        foreach (var part in parts)
        {
            content.Add(part);
        }
        return new JsonObject { ["type"] = "message", ["role"] = role, ["content"] = content };
    }

    private static JsonNode InputText(string text) => new JsonObject { ["type"] = "input_text", ["text"] = text };

    private static JsonNode InputImage(ImageContent image) => new JsonObject
    {
        ["type"] = "input_image",
        ["image_url"] = $"data:{image.MimeType};base64,{image.Data}",
        ["detail"] = "auto",
    };

    private static IEnumerable<JsonNode> UserParts(IReadOnlyList<ContentBlock> content)
    {
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent { Text.Length: > 0 } text:
                    yield return InputText(text.Text);
                    break;
                case ImageContent image:
                    yield return InputImage(image);
                    break;
            }
        }
    }

    private static JsonNode ToolOutput(ToolResultMessage result)
    {
        var text = string.Concat(result.Content.OfType<TextContent>().Select(t => t.Text));
        if (text.Length == 0)
        {
            text = result.IsError ? "error" : "(no output)";
        }
        if (!result.Content.OfType<ImageContent>().Any())
        {
            return JsonValue.Create(text)!;
        }
        var parts = new JsonArray { InputText(text) };
        foreach (var image in result.Content.OfType<ImageContent>())
        {
            parts.Add(InputImage(image));
        }
        return parts;
    }

    protected override StreamState CreateState(ModelInfo model, AssistantMessageBuilder builder) => new State(builder);

    private sealed class State(AssistantMessageBuilder builder) : StreamState(builder)
    {
        private readonly Dictionary<int, ItemState> _items = [];
        private bool _terminal;

        private sealed record ItemState(string Type, int Block)
        {
            public bool Done { get; set; }
            public bool ArgumentsStreamed { get; set; }
        }

        public override bool Handle(string eventType, string data)
        {
            if (data.TrimStart().StartsWith("[DONE]", StringComparison.Ordinal))
            {
                return true;
            }
            var e = ParseJson(data);
            if (Get(e, "error") is { ValueKind: JsonValueKind.Object } nested)
            {
                throw new ProviderException(GetString(nested, "message") ?? nested.GetRawText());
            }

            var type = GetString(e, "type") ?? eventType;
            switch (type)
            {
                case "response.created":
                case "response.in_progress":
                    Builder.ResponseId ??= GetString(Get(e, "response"), "id");
                    break;
                case "response.output_item.added":
                    OnItemAdded(e);
                    break;
                case "response.reasoning_summary_part.added":
                    if (Item(e) is { } summaryItem && Get(e, "summary_index").ValueKind == JsonValueKind.Number &&
                        Get(e, "summary_index").GetInt32() > 0)
                    {
                        Builder.AppendDelta(summaryItem.Block, "\n\n");
                    }
                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                case "response.output_text.delta":
                case "response.refusal.delta":
                    if (Item(e) is { } textItem && GetString(e, "delta") is { Length: > 0 } delta)
                    {
                        Builder.AppendDelta(textItem.Block, delta);
                    }
                    break;
                case "response.function_call_arguments.delta":
                    if (Item(e) is { } callItem && GetString(e, "delta") is { Length: > 0 } fragment)
                    {
                        callItem.ArgumentsStreamed = true;
                        Builder.AppendDelta(callItem.Block, fragment);
                    }
                    break;
                case "response.output_item.done":
                    OnItemDone(e);
                    break;
                case "response.completed":
                case "response.incomplete":
                    OnTerminal(Get(e, "response"));
                    return true;
                case "response.failed":
                    var failed = Get(e, "response");
                    var error = Get(failed, "error");
                    throw new ProviderException(GetString(error, "message") ?? "Response failed" +
                        (GetString(error, "code") is { } code ? $" ({code})" : ""));
                case "error":
                    throw new ProviderException((GetString(e, "message") ?? "Stream error") +
                        (GetString(e, "code") is { } errorCode ? $" ({errorCode})" : ""));
            }
            // 其它事件（content_part.*、*.done、内置工具进度、未知类型）忽略。
            return false;
        }

        private ItemState? Item(JsonElement e) =>
            Get(e, "output_index") is { ValueKind: JsonValueKind.Number } index && _items.TryGetValue(index.GetInt32(), out var item) ? item : null;

        private void OnItemAdded(JsonElement e)
        {
            if (Get(e, "output_index") is not { ValueKind: JsonValueKind.Number } indexElement ||
                Get(e, "item") is not { ValueKind: JsonValueKind.Object } item)
            {
                return;
            }
            var index = indexElement.GetInt32();
            var type = GetString(item, "type");
            int? block = type switch
            {
                "reasoning" => Builder.BeginThinking(),
                "message" => Builder.BeginText(),
                "function_call" => Builder.BeginToolCall(GetString(item, "call_id") ?? GetString(item, "id") ?? "", GetString(item, "name") ?? ""),
                _ => null,
            };
            if (block is { } b)
            {
                _items[index] = new ItemState(type!, b);
            }
        }

        private void OnItemDone(JsonElement e)
        {
            if (Item(e) is not { } state || Get(e, "item") is not { ValueKind: JsonValueKind.Object } item)
            {
                return;
            }
            switch (state.Type)
            {
                case "reasoning":
                    Builder.SetSignature(state.Block, item.GetRawText());
                    break;
                case "message":
                    Builder.SetProviderState(state.Block, item.Clone());
                    if (Builder.GetBuffer(state.Block).Length == 0)
                    {
                        // 没有收到 delta（部分兼容实现）：从最终 item 补齐文本。
                        foreach (var part in Get(item, "content").EnumerateArrayOrEmpty())
                        {
                            if ((GetString(part, "text") ?? GetString(part, "refusal")) is { Length: > 0 } text)
                            {
                                Builder.AppendDelta(state.Block, text);
                            }
                        }
                    }
                    break;
                case "function_call":
                    Builder.SetProviderState(state.Block, item.Clone());
                    if (!state.ArgumentsStreamed && GetString(item, "arguments") is { Length: > 0 } arguments)
                    {
                        Builder.AppendDelta(state.Block, arguments);
                    }
                    break;
            }
            state.Done = true;
            Builder.EndBlock(state.Block);
        }

        private void OnTerminal(JsonElement response)
        {
            _terminal = true;
            ReadUsage(Get(response, "usage"));
            var status = GetString(response, "status");
            var reason = GetString(Get(response, "incomplete_details"), "reason");
            Builder.StopReason = status switch
            {
                "completed" => Builder.HasToolCalls ? StopReason.ToolUse : StopReason.Stop,
                "incomplete" when reason is "max_output_tokens" or "max_messages" => StopReason.Length,
                "incomplete" => throw new ProviderException($"Response incomplete: {reason ?? "unknown"}"),
                _ => throw new ProviderException($"Unexpected response status: {status}"),
            };
        }

        private void ReadUsage(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var input = GetLong(usage, "input_tokens");
            var details = Get(usage, "input_tokens_details");
            var cached = GetLong(details, "cached_tokens");
            var cacheWrite = GetLong(details, "cache_write_tokens");
            var reasoning = GetLong(Get(usage, "output_tokens_details"), "reasoning_tokens");
            Builder.Usage = new Usage(Math.Max(0, input - cached - cacheWrite), GetLong(usage, "output_tokens"), cached, cacheWrite,
                reasoning > 0 ? reasoning : null);
        }

        public override void Complete()
        {
            if (!_terminal)
            {
                throw new ProviderException("Stream ended without a terminal response event.", isRetryable: true);
            }
            if (Builder.StopReason == StopReason.ToolUse && _items.Values.Any(i => i.Type == "function_call" && !i.Done))
            {
                throw new ProviderException("Function call did not complete (missing output_item.done).");
            }
        }
    }
}

internal static class JsonElementExtensions
{
    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
}
