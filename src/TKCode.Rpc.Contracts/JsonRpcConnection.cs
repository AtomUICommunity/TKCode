using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace TKCode.Rpc;

/// <summary>对端返回的 JSON-RPC 错误。</summary>
public sealed class RpcRemoteException(int code, string message, string? remoteType = null) : Exception(message)
{
    public int Code { get; } = code;
    public string? RemoteType { get; } = remoteType;
}

/// <summary>请求处理器抛出此异常时不发送任何回复（例如对端已声明该请求作废，之后的回复会被忽略或有害）。</summary>
public sealed class RpcSuppressReplyException() : Exception("The reply to this request is intentionally suppressed.");

/// <summary>连接已断开（对端进程退出、管道关闭）。</summary>
public sealed class RpcConnectionLostException(string message) : Exception(message);

/// <summary>消息分帧方式。</summary>
public enum JsonRpcFraming
{
    /// <summary><c>Content-Length: N\r\n\r\n</c> + JSON（LSP 风格，TKCode 自身的 UI ↔ Host 链路）。</summary>
    ContentLength,

    /// <summary>每行一个 JSON 对象（Codex app-server、ACP 等外部 Agent）。非 JSON 行被忽略。</summary>
    NewlineDelimited,
}

public sealed record JsonRpcConnectionOptions
{
    public JsonRpcFraming Framing { get; init; } = JsonRpcFraming.ContentLength;

    /// <summary>是否在发出的消息中写 <c>"jsonrpc":"2.0"</c>（Codex app-server 的线格式省略该字段）。</summary>
    public bool WriteVersionField { get; init; } = true;

    /// <summary>本端取消调用时是否发送 <c>$/cancelRequest</c>（外部协议通常有自己的取消方式）。</summary>
    public bool SendCancelNotifications { get; init; } = true;

    /// <summary>本端第一个请求的 id（ACP 夹具从 0 开始）。</summary>
    public long FirstRequestId { get; init; } = 1;

    /// <summary>
    /// 对端请求在读循环中内联开始执行（处理器同步部分完成前不读下一条消息），使请求与通知保持到达顺序。
    /// 处理器必须尽快 await，不能同步阻塞。默认 false：请求在线程池上并发执行。
    /// </summary>
    public bool InlineRequestDispatch { get; init; }

    /// <summary>对端用来取消其请求的通知方法名与 id 参数名（ACP：<c>$/cancel_request</c> + <c>requestId</c>）。</summary>
    public string IncomingCancelMethod { get; init; } = "$/cancelRequest";

    public string IncomingCancelIdProperty { get; init; } = "id";
}

/// <summary>
/// 精简的 JSON-RPC 2.0 双向连接（NativeAOT 安全：只使用源生成的 <see cref="JsonTypeInfo{T}"/>，无反射、无动态代码）。
/// <list type="bullet">
/// <item>分帧：默认 <c>Content-Length: N\r\n\r\n</c> + UTF-8 JSON（与 LSP 相同），也支持按行分帧。</item>
/// <item>发送：所有帧经单写者队列按调用顺序写出，通知不会乱序。</item>
/// <item>接收：通知在读循环中按到达顺序同步分发；请求并发执行，长请求不会阻塞后续消息。</item>
/// <item>取消：本端取消调用时向对端发送 <c>$/cancelRequest</c>；对端收到后取消对应处理器，并回复 -32800。</item>
/// </list>
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerError = -32000;
    public const int RequestCancelled = -32800;

    private const string CancelMethod = "$/cancelRequest";

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly JsonRpcConnectionOptions _options;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement?>>> _requestHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<JsonElement?>> _notificationHandlers = new(StringComparer.Ordinal);
    private Func<JsonElement, string, JsonElement?, CancellationToken, Task<JsonElement?>>? _fallbackRequestHandler;
    private Action<string, JsonElement?>? _fallbackNotificationHandler;
    private sealed record PendingCall(TaskCompletionSource<JsonElement> Completion, Action<JsonElement>? OnResponse);

    private readonly ConcurrentDictionary<long, PendingCall> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private int _closed;

    public JsonRpcConnection(Stream input, Stream output, JsonRpcConnectionOptions? options = null)
    {
        _input = input;
        _output = output;
        _options = options ?? new JsonRpcConnectionOptions();
        _nextId = _options.FirstRequestId - 1;
    }

    /// <summary>连接结束（读到 EOF 或出错）时完成。</summary>
    public Task Completion => _completion.Task;

    public bool IsConnected => Volatile.Read(ref _closed) == 0;

    /// <summary>连接断开，参数为原因。</summary>
    public event Action<string>? Disconnected;

    // ---------------- 注册处理器（在 Start 之前调用） ----------------

    public void AddRequestHandler<TParams, TResult>(string method, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo,
        Func<TParams, CancellationToken, Task<TResult>> handler)
    {
        _requestHandlers[method] = async (p, ct) =>
        {
            var result = await handler(ReadParams(p, paramsInfo), ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, resultInfo);
        };
    }

    public void AddRequestHandler<TResult>(string method, JsonTypeInfo<TResult> resultInfo, Func<CancellationToken, Task<TResult>> handler)
    {
        _requestHandlers[method] = async (_, ct) =>
        {
            var result = await handler(ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, resultInfo);
        };
    }

    public void AddRequestHandler<TParams>(string method, JsonTypeInfo<TParams> paramsInfo, Func<TParams, CancellationToken, Task> handler)
    {
        _requestHandlers[method] = async (p, ct) =>
        {
            await handler(ReadParams(p, paramsInfo), ct).ConfigureAwait(false);
            return null;
        };
    }

    public void AddRequestHandler(string method, Func<CancellationToken, Task> handler)
    {
        _requestHandlers[method] = async (_, ct) =>
        {
            await handler(ct).ConfigureAwait(false);
            return null;
        };
    }

    public void AddNotificationHandler<TParams>(string method, JsonTypeInfo<TParams> paramsInfo, Action<TParams> handler) =>
        _notificationHandlers[method] = p => handler(ReadParams(p, paramsInfo));

    /// <summary>未注册方法的请求交给此处理器（外部协议的方法很多，由调用方按名称自行分派）。</summary>
    public void SetFallbackRequestHandler(Func<string, JsonElement?, CancellationToken, Task<JsonElement?>> handler) =>
        _fallbackRequestHandler = (_, method, p, ct) => handler(method, p, ct);

    /// <summary>同上，处理器还会收到对端的请求 id（原样的 JSON 值）。</summary>
    public void SetFallbackRequestHandler(Func<JsonElement, string, JsonElement?, CancellationToken, Task<JsonElement?>> handler) =>
        _fallbackRequestHandler = handler;

    /// <summary>未注册方法的通知交给此处理器；与已注册通知一样在读循环中按到达顺序同步调用。</summary>
    public void SetFallbackNotificationHandler(Action<string, JsonElement?> handler) => _fallbackNotificationHandler = handler;

    // ---------------- 发起调用 ----------------

    /// <summary>
    /// 以原始 JSON 发起调用，返回原始 result（缺省为 Undefined）。
    /// 成功响应的 onResponse 在读循环中、完成调用之前同步执行，必须快速返回，不能阻塞或异步等待。
    /// 回调异常只使本次调用失败。
    /// </summary>
    public Task<JsonElement> InvokeRawAsync(string method, JsonElement? parameters, CancellationToken cancellationToken = default,
        Action<JsonElement>? onResponse = null) =>
        InvokeElementAsync(method, parameters is { } p ? w => p.WriteTo(w) : null, cancellationToken, onResponse);

    /// <summary>以原始 JSON 发送通知。</summary>
    public void NotifyRaw(string method, JsonElement? parameters) =>
        Enqueue(w =>
        {
            WriteVersion(w);
            w.WriteString("method", method);
            if (parameters is { } p)
            {
                w.WritePropertyName("params");
                p.WriteTo(w);
            }
        });

    public Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo,
        JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken = default) =>
        InvokeCoreAsync(method, w => JsonSerializer.Serialize(w, parameters, paramsInfo), resultInfo, cancellationToken);

    public Task<TResult> InvokeAsync<TResult>(string method, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken = default) =>
        InvokeCoreAsync(method, null, resultInfo, cancellationToken);

    public async Task InvokeAsync<TParams>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo,
        CancellationToken cancellationToken = default) =>
        await InvokeCoreAsync<object?>(method, w => JsonSerializer.Serialize(w, parameters, paramsInfo), null, cancellationToken).ConfigureAwait(false);

    public async Task InvokeAsync(string method, CancellationToken cancellationToken = default) =>
        await InvokeCoreAsync<object?>(method, null, null, cancellationToken).ConfigureAwait(false);

    /// <summary>发送通知（不等待对端）；按调用顺序写出。</summary>
    public void Notify<TParams>(string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo) =>
        Enqueue(w =>
        {
            WriteVersion(w);
            w.WriteString("method", method);
            w.WritePropertyName("params");
            JsonSerializer.Serialize(w, parameters, paramsInfo);
        });

    /// <summary>开始读写循环。处理器必须在此之前注册完毕。</summary>
    public void Start()
    {
        _ = Task.Run(WriteLoopAsync);
        _ = Task.Run(ReadLoopAsync);
    }

    private async Task<TResult> InvokeCoreAsync<TResult>(string method, Action<Utf8JsonWriter>? writeParams, JsonTypeInfo<TResult>? resultInfo,
        CancellationToken cancellationToken)
    {
        var result = await InvokeElementAsync(method, writeParams, cancellationToken).ConfigureAwait(false);
        return resultInfo is null || result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? default!
            : JsonSerializer.Deserialize(result, resultInfo)!;
    }

    private async Task<JsonElement> InvokeElementAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken cancellationToken,
        Action<JsonElement>? onResponse = null)
    {
        if (!IsConnected)
        {
            throw new RpcConnectionLostException("The JSON-RPC connection is closed.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingCall(tcs, onResponse);

        Enqueue(w =>
        {
            WriteVersion(w);
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            if (writeParams is not null)
            {
                w.WritePropertyName("params");
                writeParams(w);
            }
        });

        await using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                if (_options.SendCancelNotifications)
                {
                    Enqueue(w =>
                    {
                        WriteVersion(w);
                        w.WriteString("method", CancelMethod);
                        w.WriteStartObject("params");
                        w.WriteNumber("id", id);
                        w.WriteEndObject();
                    });
                }
                pending.Completion.TrySetCanceled(cancellationToken);
            }
        }).ConfigureAwait(false);

        return await tcs.Task.ConfigureAwait(false);
    }

    private void WriteVersion(Utf8JsonWriter w)
    {
        if (_options.WriteVersionField)
        {
            w.WriteString("jsonrpc", "2.0");
        }
    }

    // ---------------- 读写循环 ----------------

    private void Enqueue(Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }
        _outbox.Writer.TryWrite(buffer.WrittenSpan.ToArray());
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var body in _outbox.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (_options.Framing == JsonRpcFraming.ContentLength)
                {
                    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
                    await _output.WriteAsync(header, _lifetime.Token).ConfigureAwait(false);
                    await _output.WriteAsync(body, _lifetime.Token).ConfigureAwait(false);
                }
                else
                {
                    // Utf8JsonWriter 默认不缩进，正文中不会出现换行。
                    await _output.WriteAsync(body, _lifetime.Token).ConfigureAwait(false);
                    await _output.WriteAsync(NewLine, _lifetime.Token).ConfigureAwait(false);
                }
                await _output.FlushAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            Close($"Write failed: {ex.Message}");
        }
    }

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private async Task ReadLoopAsync()
    {
        if (_options.Framing == JsonRpcFraming.NewlineDelimited)
        {
            await ReadLinesLoopAsync().ConfigureAwait(false);
            return;
        }
        var reader = new BufferedStream(_input, 64 * 1024);
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var length = await ReadContentLengthAsync(reader, _lifetime.Token).ConfigureAwait(false);
                if (length < 0)
                {
                    Close("The remote side closed the connection.");
                    return;
                }
                var body = new byte[length];
                await reader.ReadExactlyAsync(body, _lifetime.Token).ConfigureAwait(false);
                Dispatch(body);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or EndOfStreamException)
        {
            Close($"Read failed: {ex.Message}");
        }
    }

    private async Task ReadLinesLoopAsync()
    {
        try
        {
            var lines = new NdjsonLineReader(_input);
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await lines.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                if (line is null)
                {
                    Close("The remote side closed the connection.");
                    return;
                }
                // 外部 CLI 偶尔会把日志写到 stdout：只处理看起来像 JSON 对象的行。
                var span = line.AsSpan().TrimStart(" \t\r"u8);
                if (span.Length > 0 && span[0] == (byte)'{')
                {
                    Dispatch(line, reportParseErrors: false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            Close($"Read failed: {ex.Message}");
        }
    }

    /// <summary>读取头部直到空行；返回 Content-Length，EOF 时返回 -1。</summary>
    private static async Task<int> ReadContentLengthAsync(Stream stream, CancellationToken ct)
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return -1;
            }
            var c = (char)one[0];
            if (c == '\r')
            {
                continue;
            }
            if (c != '\n')
            {
                line.Append(c);
                continue;
            }
            if (line.Length == 0)
            {
                if (length >= 0)
                {
                    return length;
                }
                continue; // 容忍帧之间多余的空行
            }
            var text = line.ToString();
            line.Clear();
            var colon = text.IndexOf(':');
            if (colon > 0 && text.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(text.AsSpan(colon + 1).Trim(), out var value))
            {
                length = value;
            }
        }
    }

    private void Dispatch(byte[] body, bool reportParseErrors = true)
    {
        JsonElement message;
        try
        {
            using var doc = JsonDocument.Parse(body);
            message = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            if (reportParseErrors)
            {
                SendError(null, ParseError, "Parse error");
            }
            return;
        }

        var hasMethod = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        JsonElement? parameters = message.TryGetProperty("params", out var p) ? p : null;

        if (!hasMethod)
        {
            if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var responseId))
            {
                CompleteResponse(responseId, message);
            }
            return;
        }

        var method = methodElement.GetString()!;
        if (!hasId)
        {
            HandleNotification(method, parameters);
            return;
        }
        if (_options.InlineRequestDispatch)
        {
            _ = HandleRequestAsync(id.Clone(), method, parameters);
        }
        else
        {
            _ = Task.Run(() => HandleRequestAsync(id.Clone(), method, parameters));
        }
    }

    private void CompleteResponse(long id, JsonElement message)
    {
        if (!_pending.TryRemove(id, out var pending))
        {
            return;
        }
        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var parsed) ? parsed : InternalError;
            var text = error.TryGetProperty("message", out var m) ? m.GetString() ?? "Error" : "Error";
            var type = error.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("type", out var t)
                ? t.GetString()
                : null;
            pending.Completion.TrySetException(new RpcRemoteException(code, text, type));
            return;
        }
        var result = message.TryGetProperty("result", out var value) ? value.Clone() : default;
        try
        {
            pending.OnResponse?.Invoke(result);
        }
        catch (Exception ex)
        {
            pending.Completion.TrySetException(ex);
            return;
        }
        pending.Completion.TrySetResult(result);
    }

    private void HandleNotification(string method, JsonElement? parameters)
    {
        if (method == _options.IncomingCancelMethod)
        {
            if (parameters is { ValueKind: JsonValueKind.Object } cp && cp.TryGetProperty(_options.IncomingCancelIdProperty, out var cancelId) &&
                _incoming.TryGetValue(cancelId.GetRawText(), out var cts))
            {
                cts.Cancel();
            }
            return;
        }
        Action<JsonElement?>? handler = _notificationHandlers.TryGetValue(method, out var registered)
            ? registered
            : _fallbackNotificationHandler is { } fallback ? p => fallback(method, p) : null;
        if (handler is not null)
        {
            try
            {
                handler(parameters);
            }
            catch (Exception)
            {
                // 通知没有回复通道；处理器自身负责记录错误。
            }
        }
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement? parameters)
    {
        if (!_requestHandlers.TryGetValue(method, out var handler))
        {
            if (_fallbackRequestHandler is not { } fallback)
            {
                SendError(id, MethodNotFound, $"Method not found: {method}");
                return;
            }
            handler = (p, ct) => fallback(id, method, p, ct);
        }
        var key = id.GetRawText();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _incoming[key] = cts;
        try
        {
            var result = await handler(parameters, cts.Token).ConfigureAwait(false);
            Enqueue(w =>
            {
                WriteVersion(w);
                w.WritePropertyName("id");
                id.WriteTo(w);
                w.WritePropertyName("result");
                if (result is { } value)
                {
                    value.WriteTo(w);
                }
                else
                {
                    w.WriteNullValue();
                }
            });
        }
        catch (RpcSuppressReplyException)
        {
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SendError(id, RequestCancelled, "Request cancelled");
        }
        catch (RpcInvalidParamsException ex)
        {
            SendError(id, InvalidParams, ex.Message);
        }
        catch (RpcRemoteException ex)
        {
            // 处理器可以抛出带错误码的异常（例如回退处理器不认识的方法回复 -32601）。
            SendError(id, ex.Code, ex.Message, ex.RemoteType);
        }
        catch (Exception ex)
        {
            SendError(id, ServerError, ex.Message, ex.GetType().Name);
        }
        finally
        {
            _incoming.TryRemove(key, out _);
        }
    }

    private void SendError(JsonElement? id, int code, string message, string? type = null) =>
        Enqueue(w =>
        {
            WriteVersion(w);
            w.WritePropertyName("id");
            if (id is { } value)
            {
                value.WriteTo(w);
            }
            else
            {
                w.WriteNullValue();
            }
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            if (type is not null)
            {
                w.WriteStartObject("data");
                w.WriteString("type", type);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        });

    private static T ReadParams<T>(JsonElement? parameters, JsonTypeInfo<T> info)
    {
        try
        {
            if (parameters is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } value)
            {
                throw new RpcInvalidParamsException("Missing params.");
            }
            return JsonSerializer.Deserialize(value, info) ?? throw new RpcInvalidParamsException("Params must not be null.");
        }
        catch (JsonException ex)
        {
            throw new RpcInvalidParamsException($"Invalid params: {ex.Message}");
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        _lifetime.Cancel();
        _outbox.Writer.TryComplete();
        // 关闭自己的流，让对端读到 EOF 并立即感知断开。
        foreach (var stream in new[] { _output, _input })
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.Completion.TrySetException(new RpcConnectionLostException(reason));
            }
        }
        foreach (var cts in _incoming.Values)
        {
            cts.Cancel();
        }
        _completion.TrySetResult();
        Disconnected?.Invoke(reason);
    }

    public ValueTask DisposeAsync()
    {
        Close("The connection was disposed.");
        return ValueTask.CompletedTask;
    }

    private sealed class RpcInvalidParamsException(string message) : Exception(message);
}
