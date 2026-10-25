namespace TKCode.Ai.Models;

/// <summary>按 BaseUrl 探测厂商并合并显式覆盖，得到每个字段都有值的兼容配置。</summary>
public static class CompatResolver
{
    public sealed record ResolvedChatCompat(
        bool SupportsDeveloperRole,
        string MaxTokensField,
        bool SupportsStore,
        bool SupportsUsageInStreaming,
        ChatThinkingFormat ThinkingFormat,
        IReadOnlyList<string>? ReasoningEfforts,
        bool SupportsDisabledThinking,
        bool ReplayReasoningAcrossProviderModels,
        bool RequiresReasoningContentOnAssistant,
        bool RequiresToolResultName,
        bool SendEmptyToolsWithHistory,
        bool SupportsPromptCacheKey,
        bool SupportsMidConversationSystem,
        bool SupportsCacheControl,
        bool SupportsIncludeObfuscation,
        string? SessionAffinityHeader);

    public sealed record ResolvedAnthropicCompat(
        AnthropicThinkingMode Thinking,
        bool SupportsDisabledThinking,
        bool SupportsEffort,
        bool UseBearerAuth,
        bool SupportsMidConversationSystem,
        bool SupportsEagerToolInputStreaming,
        bool SupportsCacheControl,
        bool TopLevelCacheControl,
        IReadOnlyList<string> Betas);

    public const string OpenCodeSessionHeader = "x-opencode-session";

    /// <summary>Responses 与 Messages 端点的会话亲和头（这两种协议的缓存键走 body，只有 OpenCode Go 额外要求请求头）。</summary>
    public static string? ResolveSessionHeader(ModelInfo model) => IsOpenCode(Host(model)) ? OpenCodeSessionHeader : null;

    private static bool IsOpenCode(string host) =>
        host.Equals("opencode.ai", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".opencode.ai", StringComparison.OrdinalIgnoreCase);

    public static ResolvedChatCompat ResolveChat(ModelInfo model)
    {
        var host = Host(model);
        var isOpenAI = host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase) ||
                       host.EndsWith("openai.azure.com", StringComparison.OrdinalIgnoreCase);
        var isDeepSeek = host.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
        var isKimi = host.Contains("moonshot", StringComparison.OrdinalIgnoreCase) || host.Contains("kimi", StringComparison.OrdinalIgnoreCase);
        var isZai = host.Contains("bigmodel", StringComparison.OrdinalIgnoreCase) || host.Contains("z.ai", StringComparison.OrdinalIgnoreCase);
        var isDashScope = host.Contains("dashscope", StringComparison.OrdinalIgnoreCase);
        var isOpenRouter = host.Contains("openrouter", StringComparison.OrdinalIgnoreCase);
        var isMistral = host.Contains("mistral", StringComparison.OrdinalIgnoreCase);
        var isOpenCode = IsOpenCode(host);
        var isXai = host.Equals("x.ai", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase);
        // Gemini 的 OpenAI 兼容端点对未知字段直接 400（Unknown name ...: Cannot find field）
        var isGemini = host.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase);
        var isAliyun = isDashScope || host.Contains("aliyuncs", StringComparison.OrdinalIgnoreCase);
        var isClaude = model.Id.Contains("claude", StringComparison.OrdinalIgnoreCase);
        var isQwen = model.Id.Contains("qwen", StringComparison.OrdinalIgnoreCase);

        // 默认值依据 docs/protocols/openai-chat-completions.md §10（截至 2026-10 的各厂商文档）。
        var defaultThinking = !model.Reasoning ? ChatThinkingFormat.None
            : isDeepSeek || isZai ? ChatThinkingFormat.ThinkingTypeAndEffort
            : isDashScope ? ChatThinkingFormat.EnableThinking
            : isOpenRouter ? ChatThinkingFormat.OpenRouter
            : ChatThinkingFormat.OpenAIEffort; // Kimi K3 也使用 reasoning_effort
        IReadOnlyList<string>? defaultEfforts =
            // DeepSeek OpenAI 格式的 reasoning_effort 只有 low / high / max（api-docs.deepseek.com/guides/thinking_mode）。
            isDeepSeek ? ["low", "high", "max"]
            : isZai || isKimi ? ["low", "high", "max"]
            : null;

        var c = model.Compat;
        return new ResolvedChatCompat(
            c?.SupportsDeveloperRole ?? isOpenAI,
            c?.MaxTokensField ?? (isOpenAI || isKimi ? "max_completion_tokens" : "max_tokens"),
            c?.SupportsStore ?? isOpenAI,
            c?.SupportsUsageInStreaming ?? !isOpenRouter,
            c?.ThinkingFormat ?? defaultThinking,
            c?.ReasoningEfforts ?? defaultEfforts,
            c?.SupportsDisabledThinking ?? !isZai,
            c?.ReplayReasoningAcrossProviderModels ?? (isDeepSeek || isKimi || isZai || isDashScope),
            c?.RequiresReasoningContentOnAssistant ?? (model.Reasoning && (isDeepSeek || isKimi)),
            c?.RequiresToolResultName ?? isMistral,
            c?.SendEmptyToolsWithHistory ?? false,
            // 缓存键默认对所有端点发送：中转 / 网关据此把同一会话路由到同一上游；不认识的厂商通常忽略未知字段
            c?.SupportsPromptCacheKey ?? !isGemini,
            c?.SupportsMidConversationSystem ?? !isDashScope,
            // Claude（OpenRouter、中转）与百炼 Qwen 的显式缓存要在 content 上加 cache_control，否则不缓存
            c?.SupportsCacheControl ?? (isClaude || (isQwen && isAliyun)),
            isOpenAI,
            // 只认请求头的会话亲和：xAI Chat 用 x-grok-conv-id，OpenRouter 用 x-session-id 做粘性路由，OpenCode Go 用 x-opencode-session
            isXai ? "x-grok-conv-id" : isOpenRouter ? "x-session-id" : isOpenCode ? OpenCodeSessionHeader : null);
    }

    public static ResolvedAnthropicCompat ResolveAnthropic(ModelInfo model)
    {
        var host = Host(model);
        var isAnthropic = host.Length == 0 || host.EndsWith("anthropic.com", StringComparison.OrdinalIgnoreCase);
        var isKimi = host.Contains("moonshot", StringComparison.OrdinalIgnoreCase) || host.Contains("kimi", StringComparison.OrdinalIgnoreCase);
        var bearerHost = isKimi || host.Contains("minimax", StringComparison.OrdinalIgnoreCase);
        var family = isAnthropic ? ClaudeFamily.Of(model.Id) : null;
        var c = model.Compat;
        return new ResolvedAnthropicCompat(
            c?.AnthropicThinking ?? (!model.Reasoning ? AnthropicThinkingMode.None
                : family is { UsesBudget: true } ? AnthropicThinkingMode.Budget
                : isAnthropic ? AnthropicThinkingMode.Adaptive : AnthropicThinkingMode.Budget),
            c?.SupportsDisabledThinking ?? (family?.SupportsDisabled ?? true),
            c?.SupportsEffort ?? (family?.SupportsEffort ?? false),
            c?.AuthHeader is { } auth ? auth.Equals("bearer", StringComparison.OrdinalIgnoreCase) : bearerHost,
            c?.SupportsMidConversationSystem ?? (family?.SupportsMidConversationSystem ?? false),
            c?.SupportsEagerToolInputStreaming ?? isAnthropic,
            // 所有 Messages 端点默认带断点：中转 / 网关后面多半就是 Anthropic，没有断点就完全不缓存；不支持的厂商会忽略
            c?.SupportsCacheControl ?? true,
            // Kimi 只认顶层 cache_control（messages 内的断点被忽略，规格 §12）
            isKimi,
            c?.AnthropicBetas ?? []);
    }

    /// <summary>
    /// Claude 模型族的能力（规格 docs/protocols/anthropic-messages.md §2.4、§2.8、§5.2）。
    /// 未识别的模型按最新模型的保守规则处理。
    /// </summary>
    private sealed record ClaudeFamily(bool UsesBudget, bool SupportsDisabled, bool SupportsEffort, bool SupportsMidConversationSystem)
    {
        public static ClaudeFamily Of(string modelId)
        {
            var id = modelId.ToLowerInvariant();
            bool Has(params string[] parts) => parts.Any(p => id.Contains(p, StringComparison.Ordinal));

            // 始终思考：disabled 与 budget_tokens 都会 400。
            if (Has("opus-5-5", "fable", "mythos", "sonnet-5-5"))
            {
                return new(false, false, true, true);
            }
            if (Has("opus-5", "haiku-5-5"))
            {
                return new(false, true, true, true);
            }
            if (Has("sonnet-5"))
            {
                return new(false, true, true, false);
            }
            if (Has("opus-4-8"))
            {
                return new(false, true, true, true);
            }
            if (Has("opus-4-7", "opus-4-6", "sonnet-4-6"))
            {
                return new(false, true, true, false);
            }
            if (Has("opus-4-5"))
            {
                return new(true, true, true, false);
            }
            if (Has("sonnet-4-5", "haiku-4-5", "claude-3", "opus-4-1", "opus-4-0", "sonnet-4-0", "claude-opus-4", "claude-sonnet-4"))
            {
                return new(true, true, false, false);
            }
            return new(false, false, true, false);
        }
    }

    private static string Host(ModelInfo model) =>
        Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : "";
}
