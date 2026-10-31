using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;

namespace TKCode.Ai.Fake;

/// <summary>
/// 脚本化的模拟协议：按真实节奏流式输出思考、文本与工具调用，
/// 用于在不接真实模型的情况下驱动 UI 与 Agent 循环。
/// </summary>
public sealed class FakeWireApi : IWireApi
{
    private readonly TimeSpan _chunkDelay;

    public FakeWireApi(TimeSpan? chunkDelay = null) => _chunkDelay = chunkDelay ?? TimeSpan.FromMilliseconds(18);

    public string Api => KnownApis.Fake;

    public static IReadOnlyList<ModelInfo> Models { get; } =
    [
        new() { Id = "fake-sonnet", Name = "Fake Sonnet", Provider = "fake", Api = KnownApis.Fake, Reasoning = true },
        new() { Id = "fake-mini", Name = "Fake Mini", Provider = "fake", Api = KnownApis.Fake },
    ];

    public AssistantMessageStream Stream(ModelInfo model, LlmContext context, StreamOptions options, CancellationToken cancellationToken)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(() => ProduceAsync(model, context, stream, cancellationToken), CancellationToken.None);
        return stream;
    }

    private async Task ProduceAsync(ModelInfo model, LlmContext context, AssistantMessageStream stream, CancellationToken ct)
    {
        var builder = new AssistantMessageBuilder(model, stream);
        try
        {
            // 测试用：「fake:fail-once」第一次请求先报一次自动重试，再以 503 失败；按现有历史重试（出错的回复之后）时正常回复
            if (FailsOnce(context))
            {
                stream.Push(new RetryEvent(1, 2, TimeSpan.FromSeconds(2), "503 Service Unavailable (fake)"));
                await Task.Delay(_chunkDelay, ct);
                throw new Http.ProviderException("503 Service Unavailable (fake)", System.Net.HttpStatusCode.ServiceUnavailable, isRetryable: true);
            }
            builder.Start();
            var last = context.Messages.LastOrDefault(m => m is not AssistantMessage { StopReason: StopReason.Error or StopReason.Aborted });
            var hasListTool = context.Tools.Any(t => t.Name == "ls");

            if (model.Reasoning)
            {
                var thinking = builder.BeginThinking();
                await EmitAsync(builder, thinking, last is ToolResultMessage
                    ? "工具结果已返回，整理一下要点再回复用户。"
                    : "先理解用户的问题；如果需要看工作区的文件，就调用 ls 工具。", ct);
                builder.SetSignature(thinking, "fake-signature");
                builder.EndBlock(thinking);
            }

            if (context.Tools.Count == 0 && context.SystemPrompt?.StartsWith("Generate a short title for this conversation.", StringComparison.Ordinal) == true)
            {
                var title = string.Concat((last as UserMessage)?.Content.OfType<TextContent>().Select(t => t.Text) ?? [])
                    .Trim().Split('\n')[0];
                var block = builder.BeginText();
                await EmitAsync(builder, block, title[..Math.Min(title.Length, 24)], ct);
                builder.EndBlock(block);
            }
            else if (last is UserMessage summaryUser && IsSummaryRequest(summaryUser))
            {
                // 压缩上下文的摘要请求（docs/design/compaction.md §7.1）：给一段简短的结构化摘要
                var text = builder.BeginText();
                await EmitAsync(builder, text, FakeSummary(context), ct);
                builder.EndBlock(text);
            }
            else if (last is UserMessage mcpUser && McpCall(mcpUser, context) is { } mcp)
            {
                // 开发 / 测试用：“mcp:<工具名> <JSON 参数>” 直接调用该 MCP 工具
                var call = builder.BeginToolCall($"call_{Guid.NewGuid():N}"[..20], mcp.Name);
                await EmitAsync(builder, call, mcp.Arguments, ct);
                builder.EndBlock(call);
            }
            else if (last is UserMessage taskUser && context.Tools.Any(t => t.Name == "task_create") && WantsTask(taskUser))
            {
                var text = builder.BeginText();
                await EmitAsync(builder, text, "我先把这件事记进任务清单。", ct);
                builder.EndBlock(text);

                var call = builder.BeginToolCall($"call_{Guid.NewGuid():N}"[..20], "task_create");
                await EmitAsync(builder, call, """{"subject":"Fake task","description":"Created by the fake provider","activeForm":"Doing the fake task"}""", ct);
                builder.EndBlock(call);
            }
            else if (last is UserMessage user && hasListTool && WantsFiles(user))
            {
                var text = builder.BeginText();
                await EmitAsync(builder, text, "我先看一下当前工作区的目录结构。", ct);
                builder.EndBlock(text);

                var call = builder.BeginToolCall($"call_{Guid.NewGuid():N}"[..20], "ls");
                await EmitAsync(builder, call, """{"path":"."}""", ct);
                builder.EndBlock(call);
            }
            else
            {
                var text = builder.BeginText();
                await EmitAsync(builder, text, ComposeReply(last), ct);
                builder.EndBlock(text);
            }

            var input = context.Messages.Sum(m => EstimateTokens(m));
            builder.Usage = new Usage(input, 120, 0, 0, model.Reasoning ? 40 : null);
            builder.Finish();
        }
        catch (Exception ex)
        {
            builder.Fail(ex, ct.IsCancellationRequested);
        }
    }

    /// <summary>最后一条用户消息是「fake:fail-once」，且它之后还没有出错的回复。</summary>
    private static bool FailsOnce(LlmContext context)
    {
        var index = -1;
        for (var i = context.Messages.Count - 1; i >= 0; i--)
        {
            if (context.Messages[i] is UserMessage)
            {
                index = i;
                break;
            }
        }
        return index >= 0 &&
               string.Concat(((UserMessage)context.Messages[index]).Content.OfType<TextContent>().Select(t => t.Text)).Trim() == "fake:fail-once" &&
               !context.Messages.Skip(index + 1).Any(m => m is AssistantMessage { StopReason: StopReason.Error });
    }

    private async Task EmitAsync(AssistantMessageBuilder builder, int index, string text, CancellationToken ct)
    {
        foreach (var chunk in Chunk(text))
        {
            ct.ThrowIfCancellationRequested();
            builder.AppendDelta(index, chunk);
            await Task.Delay(_chunkDelay, ct);
        }
    }

    private static IEnumerable<string> Chunk(string text)
    {
        for (var i = 0; i < text.Length; i += 3)
        {
            yield return text.Substring(i, Math.Min(3, text.Length - i));
        }
    }

    private static (string Name, string Arguments)? McpCall(UserMessage user, LlmContext context)
    {
        var text = string.Concat(user.Content.OfType<TextContent>().Select(t => t.Text)).Trim();
        if (!text.StartsWith("mcp:", StringComparison.Ordinal))
        {
            return null;
        }
        var rest = text[4..].Trim();
        var space = rest.IndexOf(' ');
        var name = space < 0 ? rest : rest[..space];
        var args = space < 0 ? "{}" : rest[(space + 1)..].Trim();
        return context.Tools.Any(t => t.Name == name) ? (name, args) : null;
    }

    private static bool IsSummaryRequest(UserMessage user) =>
        user.Content.OfType<TextContent>().Any(t => t.Text.Contains("Your task is to create a detailed summary of the conversation so far", StringComparison.Ordinal));

    private static string FakeSummary(LlmContext context)
    {
        var asks = context.Messages.OfType<UserMessage>().Where(u => u.Source is null)
            .Select(u => string.Concat(u.Content.OfType<TextContent>().Select(t => t.Text)).Trim())
            .Where(t => t.Length > 0 && !t.Contains("Your task is to create a detailed summary", StringComparison.Ordinal))
            .Select(t => t.Length > 80 ? t[..80] + "…" : t)
            .ToArray();
        return $"""
            <analysis>Fake Provider：不做真正的分析。</analysis>
            <summary>
            1. Primary Request and Intent: {(asks.Length == 0 ? "（无）" : asks[^1])}
            6. All user messages:
            {string.Join("\n", asks.Select(a => $"   - {a}"))}
            8. Current Work: 这是 Fake Provider 生成的模拟摘要。
            </summary>
            """;
    }

    private static bool WantsTask(UserMessage user)
    {
        var text = string.Concat(user.Content.OfType<TextContent>().Select(t => t.Text));
        return text.Contains("任务", StringComparison.Ordinal) || text.Contains("task", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WantsFiles(UserMessage user)
    {
        var text = string.Concat(user.Content.OfType<TextContent>().Select(t => t.Text));
        return text.Contains("文件", StringComparison.Ordinal) || text.Contains("目录", StringComparison.Ordinal) ||
               text.Contains("file", StringComparison.OrdinalIgnoreCase) || text.Contains("ls", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComposeReply(Message? last) => last switch
    {
        ToolResultMessage result => $"""
            工具 `{result.ToolName}` 已执行完成。这是 **Fake Provider** 的模拟回复：

            - 工具调用、结果回传与下一轮推理都走的是真实的 Agent 循环
            - 接入真实协议适配器后，这里会是模型的实际输出
            """,
        UserMessage user => $"""
            收到：「{string.Concat(user.Content.OfType<TextContent>().Select(t => t.Text)).Trim()}」

            当前连接的是 **Fake Provider**，用于验证界面与 Agent 运行时：

            1. 流式文本与思考块
            2. 工具调用卡片（试试输入「看看目录里有哪些文件」或「建个任务」）
            3. 中止、排队与会话持久化
            """,
        _ => "你好，我是 TKCode。",
    };

    private static long EstimateTokens(Message message) => message switch
    {
        UserMessage u => u.Content.OfType<TextContent>().Sum(t => t.Text.Length) / 4 + 4,
        AssistantMessage a => a.Content.OfType<TextContent>().Sum(t => t.Text.Length) / 4 + 4,
        ToolResultMessage r => r.Content.OfType<TextContent>().Sum(t => t.Text.Length) / 4 + 4,
        _ => 4,
    };
}
