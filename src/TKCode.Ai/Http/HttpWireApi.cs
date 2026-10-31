using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;

namespace TKCode.Ai.Http;

/// <summary>
/// 基于 HttpClient + SSE 的适配器基类。负责：
/// 发送请求（首字节前对 408/409/429/5xx 重试并遵循 retry-after）、空闲超时、逐个分发 SSE 事件、
/// 把所有失败编码为 ErrorEvent（中止 → Aborted，其它 → Error）。子类只关心请求构建与事件映射。
/// </summary>
public abstract class HttpWireApi : IWireApi
{
    private const int MaxRetries = 2;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(120);

    /// <summary>额度 / 账单类错误：即使是 429 也不重试（规格 §7）。</summary>
    private static readonly string[] NonRetryableCodes =
    [
        "insufficient_quota", "credit_balance_exhausted", "billing_hard_limit_reached", "billing_error",
        "organization_spend_limit_exceeded", "project_spend_limit_exceeded", "organization_usage_limit_exceeded",
    ];

    protected HttpWireApi(HttpClient http) => Http = http;

    protected HttpClient Http { get; }

    /// <summary>两个 SSE 事件之间允许的最长间隔。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public abstract string Api { get; }

    public AssistantMessageStream Stream(ModelInfo model, LlmContext context, StreamOptions options, CancellationToken cancellationToken)
    {
        var stream = new AssistantMessageStream();
        _ = Task.Run(() => RunAsync(model, context, options, stream, cancellationToken), CancellationToken.None);
        return stream;
    }

    /// <summary>构建请求（URL、头、JSON 体）。上下文已经过 <see cref="Transform.MessageTransformer"/> 处理。</summary>
    protected abstract HttpRequestMessage BuildRequest(ModelInfo model, LlmContext context, StreamOptions options);

    /// <summary>为一次调用创建解析状态（子类自定义）。</summary>
    protected abstract StreamState CreateState(ModelInfo model, AssistantMessageBuilder builder);

    /// <summary>预处理历史：跨模型交接、工具调用 id 规范化等。</summary>
    protected abstract IReadOnlyList<Message> PrepareMessages(ModelInfo model, IReadOnlyList<Message> messages);

    private async Task RunAsync(ModelInfo model, LlmContext context, StreamOptions options, AssistantMessageStream stream, CancellationToken ct)
    {
        var builder = new AssistantMessageBuilder(model, stream);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var prepared = context with { Messages = PrepareMessages(model, context.Messages) };
            using var response = await SendWithRetryAsync(() => BuildRequest(model, prepared, options), stream.Push, ct);
            if (response.Content.Headers.ContentType?.MediaType is { } mediaType &&
                mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                // 2xx 但不是 SSE（例如代理返回的 JSON 错误）：按错误体解析，而不是报成"流被截断"。
                var text = await response.Content.ReadAsStringAsync(ct);
                throw new ProviderException(ExtractErrorMessage(text) ?? $"Unexpected content type '{mediaType}'.");
            }
            builder.Start();

            var state = CreateState(model, builder);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            idle.CancelAfter(IdleTimeout);
            await foreach (var item in SseParser.Create(body).EnumerateAsync(idle.Token))
            {
                idle.CancelAfter(IdleTimeout);
                if (state.Handle(item.EventType, item.Data))
                {
                    break;
                }
            }
            ct.ThrowIfCancellationRequested();
            state.Complete();
            builder.Finish();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && idle.IsCancellationRequested)
        {
            builder.Fail(new ProviderException($"No data received for {IdleTimeout.TotalSeconds:0}s (idle timeout).", isRetryable: true), aborted: false);
        }
        catch (Exception ex)
        {
            builder.Fail(ex, ct.IsCancellationRequested);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, Action<LlmEvent> report, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = factory();
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // 连接错误与连接超时（不是用户取消）：在首字节前重试。
                if (attempt >= MaxRetries)
                {
                    throw new ProviderException($"Connection failed: {ex.Message}", isRetryable: true);
                }
                var backoff = Backoff(attempt);
                report(new RetryEvent(attempt + 1, MaxRetries, backoff, $"Connection failed: {ex.Message}"));
                await Task.Delay(backoff, ct);
                continue;
            }
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var retryable = IsRetryable(response, body);
            var error = new ProviderException($"{(int)response.StatusCode} {ExtractErrorMessage(body) ?? response.ReasonPhrase}",
                response.StatusCode, retryable);
            var delay = RetryDelay(response, attempt);
            response.Dispose();
            if (!retryable || attempt >= MaxRetries || delay > MaxRetryDelay)
            {
                throw error;
            }
            report(new RetryEvent(attempt + 1, MaxRetries, delay, error.Message));
            await Task.Delay(delay, ct);
        }
    }

    private static bool IsRetryable(HttpResponseMessage response, string body)
    {
        if (response.Headers.TryGetValues("x-should-retry", out var values))
        {
            return string.Equals(values.FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
        }
        var code = (int)response.StatusCode;
        if (code == 429 && ExtractErrorCodes(body).Any(c => NonRetryableCodes.Contains(c, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }
        return code is 408 or 409 or 429 || code >= 500;
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var ms) &&
            double.TryParse(ms.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var msValue))
        {
            return Clamp(TimeSpan.FromMilliseconds(msValue));
        }
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta)
            {
                return Clamp(delta);
            }
            if (retryAfter.Date is { } date)
            {
                return Clamp(date - DateTimeOffset.UtcNow);
            }
        }
        return Backoff(attempt);
    }

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    /// <summary>指数退避：0.5s 起，最多 8s，加约 25% 抖动（与官方 SDK 一致）。</summary>
    private static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(0.5 * Math.Pow(2, attempt), 8);
        return TimeSpan.FromSeconds(seconds * (1 - Random.Shared.NextDouble() * 0.25));
    }

    private static IEnumerable<string> ExtractErrorCodes(string body)
    {
        JsonNode? error;
        try
        {
            error = JsonNode.Parse(body)?["error"];
        }
        catch (JsonException)
        {
            yield break;
        }
        if (error is not JsonObject obj)
        {
            yield break;
        }
        foreach (var key in new[] { "code", "type" })
        {
            if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                yield return text;
            }
        }
    }

    /// <summary>从 {"error":{"message":…}} / {"error":"…"} / {"message":…} 中取出错误信息。</summary>
    public static string? ExtractErrorMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var error = node?["error"];
            return error switch
            {
                JsonObject o => o["message"]?.GetValue<string>() ?? o.ToJsonString(),
                JsonValue v => v.ToString(),
                _ => node?["message"]?.GetValue<string>(),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return body.Length > 500 ? body[..500] : body;
        }
    }

    protected static HttpRequestMessage JsonPost(string url, JsonNode body)
    {
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    protected static void ApplyHeaders(HttpRequestMessage request, ModelInfo model)
    {
        if (model.Headers is null)
        {
            return;
        }
        foreach (var (key, value) in model.Headers)
        {
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    protected static string JoinUrl(string? baseUrl, string fallbackBase, string path) =>
        (string.IsNullOrWhiteSpace(baseUrl) ? fallbackBase : baseUrl).TrimEnd('/') + path;

    protected static JsonNode ParseSchema(string json) => JsonNode.Parse(json) ?? new JsonObject();
}

/// <summary>一次流式调用的解析状态。</summary>
public abstract class StreamState(AssistantMessageBuilder builder)
{
    protected AssistantMessageBuilder Builder { get; } = builder;

    /// <summary>处理一个 SSE 事件；返回 true 表示流已结束。</summary>
    public abstract bool Handle(string eventType, string data);

    /// <summary>流读完后的校验（例如缺少终止事件），失败时抛异常。</summary>
    public virtual void Complete()
    {
    }

    protected static JsonElement ParseJson(string data)
    {
        using var doc = JsonDocument.Parse(data);
        return doc.RootElement.Clone();
    }

    protected static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    protected static long GetLong(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;

    protected static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) ? p : default;
}
