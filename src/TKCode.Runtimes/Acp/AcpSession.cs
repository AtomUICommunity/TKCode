using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TKCode.Agent.Diff;
using TKCode.Ai.Messages;
using TKCode.Rpc;
using TKCode.Runtimes.Json;

namespace TKCode.Runtimes.Acp;

internal static class AcpDiffLimits
{
    /// <summary>与 TKCode.Agent.Coding.Tools.Files.FileRevert.MaxSnapshotBytes 一致。</summary>
    public const int MaxSnapshotBytes = 512 * 1024;
}

/// <summary>
/// 一个 ACP 会话 = 一个 Agent 进程（规格 docs/protocols/acp-v1.md）。
/// <list type="bullet">
/// <item>入站通知与 Agent 的请求在读循环中按到达顺序写入同一个队列，由单个消费者处理（§2.4）；
/// prompt 响应到达后再向队列追加“轮次结束”，保证它排在之前所有更新之后。</item>
/// <item>轮次只以 <c>session/prompt</c> 的响应结束（§6.6）。</item>
/// <item>审批在消费者之外异步等待用户，不阻塞后续更新。</item>
/// </list>
/// </summary>
internal sealed class AcpSession : IRuntimeSession
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(10);
    private const int MaxEarlyNotifications = 64;

    private readonly JsonRpcConnection _rpc;
    private readonly IAsyncDisposable? _process;
    private readonly Func<string>? _describeExit;
    private readonly AcpAgentDefinition _agent;
    private readonly IRuntimeHost _host;
    private readonly Channel<Func<ValueTask>> _queue = Channel.CreateUnbounded<Func<ValueTask>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly List<JsonElement> _early = [];
    private Task _consumer = Task.CompletedTask;
    private string? _sessionId;
    private Turn? _turn;
    private string _agentTitle;
    private string? _appliedModel;
    private string? _appliedThought;

    public AcpSession(Stream agentStdout, Stream agentStdin, AcpAgentDefinition agent, IRuntimeHost host,
        IAsyncDisposable? process = null, Func<string>? describeExit = null)
    {
        _agent = agent;
        _host = host;
        _process = process;
        _describeExit = describeExit;
        _agentTitle = agent.DisplayName;
        _rpc = new JsonRpcConnection(agentStdout, agentStdin, new JsonRpcConnectionOptions
        {
            Framing = JsonRpcFraming.NewlineDelimited,
            FirstRequestId = 0,
            InlineRequestDispatch = true,
            SendCancelNotifications = false,
            IncomingCancelMethod = "$/cancel_request",
            IncomingCancelIdProperty = "requestId",
        });
        _rpc.SetFallbackNotificationHandler(OnNotification);
        _rpc.SetFallbackRequestHandler(OnRequestAsync);
    }

    public string? NativeSessionId => _sessionId;

    public AcpConfigState Config { get; } = new();

    /// <summary>配置响应与通知在消费者队列中按序应用后触发，供运行时刷新模型列表。</summary>
    public event Action<AcpConfigState>? ConfigChanged;

    public JsonElement AgentCapabilities { get; internal set; }

    /// <summary>客户端版本（clientInfo.version）。</summary>
    public static string ClientVersion { get; set; } =
        (typeof(AcpSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0").Split('+')[0];

    // ---------------- 启动 ----------------

    /// <summary>
    /// initialize → (resume | load | new)。恢复失败时新建会话（Agent 端上下文丢失）。
    /// 收到 -32000 时只对白名单内的认证方法自动调用一次 authenticate（§4.3）。
    /// </summary>
    public async Task StartAsync(string cwd, string? resumeSessionId, CancellationToken ct)
    {
        _rpc.Start();
        _consumer = Task.Run(ConsumeAsync);

        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshake.CancelAfter(HandshakeTimeout);
            var init = await InvokeAsync("initialize", JsonBuilder.Object(w =>
            {
                w.WriteNumber("protocolVersion", 1);
                w.WriteStartObject("clientCapabilities");
                w.WriteStartObject("fs");
                w.WriteBoolean("readTextFile", false);
                w.WriteBoolean("writeTextFile", false);
                w.WriteEndObject();
                w.WriteBoolean("terminal", false);
                // 只声明 form：URL 模式需要的安全打开方式 TKCode 暂不提供（规格 §11）
                w.WriteStartObject("elicitation");
                w.WriteStartObject("form");
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteStartObject("auth");
                w.WriteBoolean("terminal", false);
                w.WriteEndObject();
                w.WriteStartObject("session");
                w.WriteStartObject("configOptions");
                w.WriteStartObject("boolean");
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteStartObject("notices");
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteStartObject("clientInfo");
                w.WriteString("name", "tkcode");
                w.WriteString("title", "TKCode");
                w.WriteString("version", ClientVersion);
                w.WriteEndObject();
            }), handshake.Token);
            if (init.Long("protocolVersion") is not 1)
            {
                throw new InvalidOperationException(
                    $"{_agent.DisplayName} uses ACP v{init.Long("protocolVersion")?.ToString() ?? "?"}; TKCode currently supports v1 only.");
            }
            AgentCapabilities = init.Obj("agentCapabilities") ?? default;
            if (init.Obj("agentInfo") is { } info)
            {
                _agentTitle = info.Str("title") ?? info.Str("name") ?? _agentTitle;
            }
            _authMethods = init.Arr("authMethods").Where(m => m.Str("type") is null && m.Str("id") is not null).Select(m => m.Str("id")!).ToArray();
        }

        var sessionCaps = AgentCapabilities.ValueKind == JsonValueKind.Object ? AgentCapabilities.Obj("sessionCapabilities") : null;
        if (resumeSessionId is not null)
        {
            if (sessionCaps?.HasObject("resume") == true && await TryOpenAsync("session/resume", resumeSessionId, cwd, ct))
            {
                return;
            }
            if (AgentCapabilities.Bool("loadSession") && await TryOpenAsync("session/load", resumeSessionId, cwd, ct))
            {
                return;
            }
        }
        var created = await WithAuthAsync(() => InvokeConfigAsync("session/new", SessionParams(null, cwd), ct), ct);
        var sessionId = created.Str("sessionId") ?? throw new InvalidOperationException("session/new returned no sessionId.");
        Establish(sessionId);
    }

    private string[] _authMethods = [];

    private async Task<bool> TryOpenAsync(string method, string sessionId, string cwd, CancellationToken ct)
    {
        try
        {
            // load 期间 _turn 为 null：重放的消息与工具更新被丢弃，状态类更新照常应用（§5.2）。
            Establish(sessionId);
            await WithAuthAsync(() => InvokeConfigAsync(method, SessionParams(sessionId, cwd), ct), ct);
            return true;
        }
        catch (RpcRemoteException)
        {
            lock (_gate)
            {
                _sessionId = null;
            }
            return false;
        }
    }

    /// <summary>注入的 TKCode MCP 桥接（StartAsync 之前设置）。stdio 是 ACP 的基线能力，不依赖 mcpCapabilities。</summary>
    public McpBridgeLaunch? McpBridge { get; set; }

    /// <summary>
    /// cwd 以外的工作区目录（docs/design/projects.md §6.8，StartAsync 之前设置）。只在 Agent 声明了
    /// <c>sessionCapabilities.additionalDirectories</c> 时随 new / load / resume 发送完整列表（规格 §5.1–§5.3）。
    /// </summary>
    public IReadOnlyList<string> AdditionalDirectories { get; set; } = [];

    /// <summary>Agent 声明了 <c>sessionCapabilities.additionalDirectories</c>。</summary>
    public bool SupportsAdditionalDirectories =>
        AgentCapabilities.ValueKind == JsonValueKind.Object && AgentCapabilities.Obj("sessionCapabilities")?.HasObject("additionalDirectories") == true;

    internal JsonElement SessionParams(string? sessionId, string cwd) => JsonBuilder.Object(w =>
    {
        if (sessionId is not null)
        {
            w.WriteString("sessionId", sessionId);
        }
        w.WriteString("cwd", cwd);
        // 完整列表：去掉重复项与等于 cwd 的项（规格 §5.1）
        var directories = AdditionalDirectories.Where(d => !string.Equals(d, cwd, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (SupportsAdditionalDirectories && directories.Length > 0)
        {
            w.WriteStartArray("additionalDirectories");
            foreach (var directory in directories)
            {
                w.WriteStringValue(directory);
            }
            w.WriteEndArray();
        }
        w.WriteStartArray("mcpServers");
        if (McpBridge is { } bridge)
        {
            w.WriteStartObject();
            w.WriteString("name", McpBridgeLaunch.ServerName);
            bridge.WriteObject(w, envAsArray: true);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// <summary>确定会话 id，并按原顺序回放在此之前到达的同会话通知（§5.1）。</summary>
    private void Establish(string sessionId)
    {
        JsonElement[] early;
        lock (_gate)
        {
            _sessionId = sessionId;
            early = _early.Where(p => p.Str("sessionId") == sessionId).ToArray();
            _early.Clear();
        }
        foreach (var p in early)
        {
            Enqueue(() => ApplyUpdateAsync(p));
        }
        _host.OnNativeSessionId(sessionId);
    }

    private async Task<JsonElement> WithAuthAsync(Func<Task<JsonElement>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (RpcRemoteException ex) when (ex.Code == -32000)
        {
            var silent = _authMethods.Length == 1 && _agent.SilentAuthMethods?.Contains(_authMethods[0]) == true ? _authMethods[0] : null;
            if (silent is null)
            {
                throw new InvalidOperationException(LoginHint(ex.Message));
            }
            try
            {
                await InvokeAsync("authenticate", JsonBuilder.Object(w => w.WriteString("methodId", silent)), ct);
                return await call();
            }
            catch (RpcRemoteException again)
            {
                throw new InvalidOperationException(LoginHint(again.Message));
            }
        }
    }

    private string LoginHint(string message) =>
        $"{_agentTitle} requires login ({message}). Run `{_agent.Executable} login` in a terminal, then try again.";

    // ---------------- 配置 ----------------

    /// <summary>
    /// 把 TKCode 选择的模型与思考档位应用到 Agent（§10.3）。值已是当前值时跳过；
    /// 用户显式选择的模型设置失败时报错，思考档位失败只忽略。
    /// </summary>
    private async Task ApplyOptionsAsync(TurnOptions options, CancellationToken ct)
    {
        var modelId = options.Model.Id;
        if (modelId != AcpRuntime.DefaultModelId && modelId != _appliedModel)
        {
            if (Config.ByCategory("model") is { } modelOption)
            {
                if (modelOption.Values.Any(v => v.Value == modelId) && modelOption.CurrentValue != modelId)
                {
                    await SetConfigAsync(modelOption.Id, modelId, ct);
                }
            }
            else if (Config.LegacyModels.Any(m => m.Value == modelId) && Config.LegacyCurrentModel != modelId)
            {
                try
                {
                    await InvokeAsync("session/set_model", JsonBuilder.Object(w =>
                    {
                        w.WriteString("sessionId", _sessionId);
                        w.WriteString("modelId", modelId);
                    }), ct);
                    Config.SetLegacyModel(modelId);
                }
                catch (RpcRemoteException ex) when (ex.Code == JsonRpcConnection.MethodNotFound)
                {
                    // 不支持在线切换模型：沿用 Agent 的当前模型
                }
            }
            _appliedModel = modelId;
        }

        if (Config.ChooseThoughtValue(options.ThinkingLevel) is { } thought && Config.ByCategory("thought_level") is { } thoughtOption &&
            thought != _appliedThought)
        {
            if (thoughtOption.CurrentValue != thought)
            {
                try
                {
                    await SetConfigAsync(thoughtOption.Id, thought, ct);
                }
                catch (RpcRemoteException)
                {
                    // 辅助选项失败不阻断本轮
                }
            }
            _appliedThought = thought;
        }
    }

    private async Task SetConfigAsync(string configId, string value, CancellationToken ct)
    {
        await InvokeConfigAsync("session/set_config_option", JsonBuilder.Object(w =>
        {
            w.WriteString("sessionId", _sessionId);
            w.WriteString("configId", configId);
            w.WriteString("value", value);
        }), ct);
    }

    private async Task<JsonElement> InvokeConfigAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        Task applied = Task.CompletedTask;
        var result = await _rpc.InvokeRawAsync(method, parameters, ct, onResponse: response =>
        {
            // 必须在读循环收到响应时入队，而不是 await 之后；后续通知不能被旧响应覆盖。
            applied = RunOnQueueAsync(() =>
            {
                ApplyConfigResponse(response);
                return ValueTask.CompletedTask;
            });
        });
        await applied;
        return result;
    }

    private void ApplyConfigResponse(JsonElement result)
    {
        Config.ApplyResponse(result);
        ConfigChanged?.Invoke(Config);
    }

    // ---------------- 轮次 ----------------

    public async Task RunTurnAsync(UserMessage message, TurnOptions options, TurnWriter writer, CancellationToken cancellationToken)
    {
        await ApplyOptionsAsync(options, cancellationToken);
        var turn = new Turn(writer);
        await RunOnQueueAsync(() =>
        {
            _turn = turn;
            return ValueTask.CompletedTask;
        });

        var prompt = _rpc.InvokeRawAsync("session/prompt", JsonBuilder.Object(w =>
        {
            w.WriteString("sessionId", _sessionId);
            w.WritePropertyName("prompt");
            WritePrompt(w, message);
        }));
        JsonElement? result = null;
        Exception? error = null;
        using (cancellationToken.Register(() => Cancel(turn)))
        {
            try
            {
                if (await Task.WhenAny(prompt, turn.CancelDeadline.Task) != prompt)
                {
                    error = new TimeoutException($"{_agentTitle} did not stop within {CancelGrace.TotalSeconds:0} seconds after cancel.");
                }
                else
                {
                    result = await prompt;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }
        await RunOnQueueAsync(() => FinishTurnAsync(turn, result, error));
        if (error is RpcConnectionLostException or TimeoutException)
        {
            // 进程已不可用：交给 ExternalAgent 丢弃会话，下一次提问重启并恢复。
            throw new InvalidOperationException(DescribeFailure(error), error);
        }
    }

    public ValueTask<bool> SteerAsync(UserMessage message, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false); // ACP v1 没有中途插话，按轮次边界排队（§6.3）

    private void WritePrompt(Utf8JsonWriter w, UserMessage message)
    {
        var image = AgentCapabilities.ValueKind == JsonValueKind.Object && (AgentCapabilities.Obj("promptCapabilities")?.Bool("image") ?? false);
        w.WriteStartArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text:
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", text.Text);
                    w.WriteEndObject();
                    break;
                case ImageContent { HasData: true } img when image:
                    w.WriteStartObject();
                    w.WriteString("type", "image");
                    w.WriteString("data", img.Data);
                    w.WriteString("mimeType", img.MimeType);
                    w.WriteEndObject();
                    break;
            }
        }
        w.WriteEndArray();
    }

    /// <summary>
    /// 中断（§9.1）：先发 session/cancel，再把所有挂起的权限请求回复为 cancelled；
    /// 本地把未结束的工具标为中止；之后的更新照常接收，等待 stopReason=cancelled。
    /// </summary>
    private void Cancel(Turn turn)
    {
        TaskCompletionSource<JsonElement>[] pending;
        lock (_gate)
        {
            if (turn.Cancelled)
            {
                return;
            }
            turn.Cancelled = true;
            _rpc.NotifyRaw("session/cancel", JsonBuilder.Object(w => w.WriteString("sessionId", _sessionId)));
            pending = turn.PendingPermissions.ToArray();
            turn.PendingPermissions.Clear();
        }
        foreach (var reply in pending)
        {
            reply.TrySetResult(CancelledOutcome);
        }
        Enqueue(() => AbortOpenToolsAsync(turn, "Operation aborted."));
        _ = Task.Delay(CancelGrace).ContinueWith(_ => turn.CancelDeadline.TrySetResult(), TaskScheduler.Default);
    }

    private async ValueTask FinishTurnAsync(Turn turn, JsonElement? result, Exception? error)
    {
        await AbortOpenToolsAsync(turn, "Operation aborted.");
        _turn = null;

        if (result is { } r && r.Obj("usage") is { } usage)
        {
            var cacheRead = usage.Long("cachedReadTokens") ?? 0;
            var cacheWrite = usage.Long("cachedWriteTokens") ?? 0;
            turn.Writer.SetUsage(new Usage(Math.Max(0, (usage.Long("inputTokens") ?? 0) - cacheRead - cacheWrite), usage.Long("outputTokens") ?? 0,
                cacheRead, cacheWrite, usage.Long("thoughtTokens")));
        }

        if (turn.Cancelled)
        {
            await turn.Writer.CompleteAsync(StopReason.Aborted, "Request aborted");
            return;
        }
        if (error is not null)
        {
            var aborted = error is RpcRemoteException { Code: JsonRpcConnection.RequestCancelled };
            await turn.Writer.CompleteAsync(aborted ? StopReason.Aborted : StopReason.Error, aborted ? "Request aborted" : DescribeFailure(error));
            return;
        }
        var stopReason = result?.Str("stopReason");
        var (reason, message) = stopReason switch
        {
            "end_turn" => (StopReason.Stop, (string?)null),
            "max_tokens" => (StopReason.Length, null),
            "max_turn_requests" => (StopReason.Stop, null),
            "cancelled" => (StopReason.Aborted, "Request aborted"),
            "refusal" => (StopReason.Error, $"{_agentTitle} refused to continue."),
            _ => (StopReason.Error, $"{_agentTitle} ended the turn with an unknown stop reason '{stopReason}'."),
        };
        await turn.Writer.CompleteAsync(reason, message);
    }

    private string DescribeFailure(Exception error) => error switch
    {
        RpcRemoteException { Code: -32000 } ex => LoginHint(ex.Message),
        RpcRemoteException ex => $"{_agentTitle}: {ex.Message} (code {ex.Code})",
        RpcConnectionLostException => _describeExit?.Invoke() ?? $"{_agentTitle} exited unexpectedly.",
        _ => error.Message,
    };

    // ---------------- 入站消息 ----------------

    private void OnNotification(string method, JsonElement? parameters)
    {
        if (method != "session/update" || parameters is not { ValueKind: JsonValueKind.Object } p)
        {
            return; // 未知通知忽略（§12）
        }
        lock (_gate)
        {
            if (_sessionId is null)
            {
                if (_early.Count < MaxEarlyNotifications)
                {
                    _early.Add(p);
                }
                return;
            }
            if (p.Str("sessionId") != _sessionId)
            {
                return;
            }
        }
        Enqueue(() => ApplyUpdateAsync(p));
    }

    private Task<JsonElement?> OnRequestAsync(string method, JsonElement? parameters, CancellationToken ct) => method switch
    {
        "session/request_permission" => RequestPermissionAsync(parameters ?? default, ct),
        "elicitation/create" => ElicitAsync(parameters ?? default, ct),
        _ => throw new RpcRemoteException(JsonRpcConnection.MethodNotFound, "Method not found"),
    };

    /// <summary>
    /// elicitation/create（规格 §11）：form 模式交给 TKCode 的表单卡片（与 MCP elicitation 相同）；
    /// 其他模式没有声明，按规范回复 -32602。Agent 取消请求或轮次中止时回复 cancel。
    /// </summary>
    private async Task<JsonElement?> ElicitAsync(JsonElement p, CancellationToken ct)
    {
        if ((p.Str("mode") ?? "form") != "form")
        {
            throw new RpcRemoteException(JsonRpcConnection.InvalidParams, "elicitation mode not supported");
        }
        var schema = p.Obj("requestedSchema")?.GetRawText();
        var result = await _host.RequestElicitationAsync(new RuntimeElicitationRequest(p.Str("message") ?? "", schema), ct);
        return JsonBuilder.Object(w =>
        {
            var action = result.Action is "accept" or "decline" ? result.Action : "cancel";
            w.WriteString("action", action);
            if (action == "accept" && result.ContentJson is { Length: > 0 } content)
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    w.WritePropertyName("content");
                    doc.RootElement.WriteTo(w);
                }
            }
        });
    }

    private static readonly JsonElement CancelledOutcome = JsonBuilder.Parse("""{"outcome":{"outcome":"cancelled"}}""");

    private static JsonElement Selected(string optionId) => JsonBuilder.Object(w =>
    {
        w.WriteStartObject("outcome");
        w.WriteString("outcome", "selected");
        w.WriteString("optionId", optionId);
        w.WriteEndObject();
    });

    /// <summary>权限请求在读循环中同步入队（保持与工具更新的顺序），回复在用户决定后发出；Agent 取消时回复 cancelled。</summary>
    private async Task<JsonElement?> RequestPermissionAsync(JsonElement parameters, CancellationToken ct)
    {
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => BeginPermissionAsync(parameters, reply));
        await using (ct.Register(() => reply.TrySetResult(CancelledOutcome)))
        {
            return await reply.Task.ConfigureAwait(false);
        }
    }

    private async ValueTask BeginPermissionAsync(JsonElement parameters, TaskCompletionSource<JsonElement> reply)
    {
        var turn = _turn;
        lock (_gate)
        {
            if (turn is null || turn.Cancelled || parameters.Str("sessionId") != _sessionId)
            {
                reply.TrySetResult(CancelledOutcome);
                return;
            }
            turn.PendingPermissions.Add(reply);
        }
        var patch = parameters.Obj("toolCall") ?? default;
        var tool = patch.ValueKind == JsonValueKind.Object && patch.Str("toolCallId") is { } id ? Upsert(turn, id, patch) : null;
        if (tool is not null)
        {
            await DeclareAsync(turn, tool);
        }
        var options = parameters.Arr("options")
            .Select(o => (Id: o.Str("optionId"), Kind: o.Str("kind")))
            .Where(o => o.Id is not null)
            .ToArray();
        _ = DecideAsync(turn, tool, options!, reply);
    }

    private async Task DecideAsync(Turn turn, ToolState? tool, (string Id, string? Kind)[] options, TaskCompletionSource<JsonElement> reply)
    {
        string? Option(string kind) => options.Where(o => o.Kind == kind).Select(o => o.Id).FirstOrDefault();
        var known = new[] { "allow_once", "allow_always", "reject_once", "reject_always" };
        // 规范形状：每种 kind 至多一个且都是已知值；否则是提问类请求，TKCode 暂不支持（不自动回答，选拒绝 / cancelled）。
        var canonical = options.All(o => known.Contains(o.Kind)) && options.GroupBy(o => o.Kind).All(g => g.Count() == 1);
        JsonElement answer;
        if (!canonical)
        {
            answer = Option("reject_once") is { } skip ? Selected(skip) : CancelledOutcome;
        }
        else
        {
            var request = new RuntimeApprovalRequest(
                tool?.Id ?? "",
                tool?.Name ?? tool?.Title ?? "tool",
                tool?.RawInput?.GetRawText() ?? "{}",
                tool?.Kind switch
                {
                    "execute" => ApprovalKind.Command,
                    "edit" or "delete" or "move" => ApprovalKind.FileChange,
                    _ => ApprovalKind.Tool,
                },
                tool?.Title,
                null,
                Option("allow_always") is not null);
            ApprovalDecision decision;
            try
            {
                decision = await _host.RequestApprovalAsync(request, CancellationToken.None);
            }
            catch (Exception)
            {
                decision = ApprovalDecision.Deny();
            }
            answer = decision.Outcome switch
            {
                ApprovalOutcome.AllowOnce when (Option("allow_once") ?? Option("allow_always")) is { } allow => Selected(allow),
                ApprovalOutcome.AllowForSession when (Option("allow_always") ?? Option("allow_once")) is { } always => Selected(always),
                // 用户明确拒绝：reject_once，没有时 reject_always；都没有回复 cancelled（不会被当作批准）
                ApprovalOutcome.Deny when (Option("reject_once") ?? Option("reject_always")) is { } reject => Selected(reject),
                _ => CancelledOutcome,
            };
            if (decision.Outcome == ApprovalOutcome.Deny && tool is not null)
            {
                tool.Rejected = true;
            }
        }
        lock (_gate)
        {
            // 已中断：Cancel 已经先发出 session/cancel 并回复了 cancelled
            if (!turn.PendingPermissions.Remove(reply))
            {
                return;
            }
        }
        reply.TrySetResult(answer);
    }

    // ---------------- 更新处理（消费者线程） ----------------

    private async ValueTask ApplyUpdateAsync(JsonElement parameters)
    {
        if (parameters.Obj("update") is not { } update)
        {
            return;
        }
        var turn = _turn;
        switch (update.Str("sessionUpdate"))
        {
            case "agent_message_chunk" when turn is not null:
                if (update.Obj("content") is { } text && text.Str("type") == "text" && text.Str("text") is { } t)
                {
                    await AppendAsync(turn, BlockKind.Text, t);
                }
                break;
            case "agent_thought_chunk" when turn is not null:
                if (update.Obj("content") is { } thought && thought.Str("type") == "text" && thought.Str("text") is { } th)
                {
                    await AppendAsync(turn, BlockKind.Thinking, th);
                }
                break;
            case "tool_call" or "tool_call_update" when turn is not null:
                if (update.Str("toolCallId") is { } id)
                {
                    await OnToolUpdateAsync(turn, Upsert(turn, id, update));
                }
                break;
            case "config_option_update":
                ApplyConfigResponse(update);
                break;
            case "available_commands_update":
                // 整体替换命令列表，用于输入框的 / 补全（§7.8）
                _host.OnCommands(update.Arr("availableCommands").Select(c => c.Str("name") is { Length: > 0 } name
                        ? new RuntimeCommand(name.TrimStart('/'), c.Str("description") is { Length: > 0 } d ? d : null, c.Obj("input")?.Str("hint"))
                        : null)
                    .OfType<RuntimeCommand>().ToList());
                break;
            // user_message_chunk（回显）、plan、current_mode_update、session_info_update、
            // usage_update、notice 等：状态类更新，界面接入前先忽略；未知变体同样忽略（§7）。
        }
    }

    private async ValueTask AppendAsync(Turn turn, BlockKind kind, string text)
    {
        if (turn.Block is not { } block || block.Kind != kind)
        {
            block = (kind, await turn.Writer.BeginBlockAsync(kind));
            turn.Block = block;
        }
        await turn.Writer.AppendAsync(block.Handle, text);
    }

    /// <summary>tool_call 按 upsert 处理；tool_call_update 逐字段合并，content / locations 整体替换，省略与 null 不变（§7.3）。</summary>
    private static ToolState Upsert(Turn turn, string id, JsonElement patch)
    {
        if (!turn.Tools.TryGetValue(id, out var tool))
        {
            tool = new ToolState(id);
            turn.Tools[id] = tool;
        }
        tool.Title = patch.Str("title") ?? tool.Title;
        tool.Name = patch.Str("name") ?? tool.Name;
        tool.Kind = patch.Str("kind") ?? tool.Kind;
        tool.Status = patch.Str("status") ?? tool.Status;
        if (patch.Prop("rawInput") is { } rawInput)
        {
            tool.RawInput = rawInput.Clone();
        }
        if (patch.Prop("rawOutput") is { } rawOutput)
        {
            tool.RawOutput = rawOutput.Clone();
        }
        if (patch.Prop("content") is { ValueKind: JsonValueKind.Array } content)
        {
            tool.Content = content.Clone();
            var diffs = content.EnumerateArray().Where(c => c.Str("type") == "diff").ToArray();
            if (diffs.Length > 0)
            {
                tool.LastDiffs = diffs.Select(d => d.Clone()).ToArray();
            }
        }
        return tool;
    }

    /// <summary>工具生命周期（§15.3）：离开 pending 时声明并开始执行；completed / failed 结束；已结束的工具只更新数据。</summary>
    private async ValueTask OnToolUpdateAsync(Turn turn, ToolState tool)
    {
        if (tool.Done)
        {
            return;
        }
        switch (tool.Status)
        {
            case "in_progress":
                await DeclareAsync(turn, tool);
                await turn.Writer.ToolStartedAsync(tool.Id, tool.DisplayName, tool.ArgumentsJson);
                break;
            case "completed" or "failed":
                await CompleteToolAsync(turn, tool, tool.ResultText(), tool.Status == "failed");
                break;
            case "cancelled":
                await CompleteToolAsync(turn, tool, "Operation aborted.", true);
                break;
        }
    }

    /// <summary>
    /// 声明工具调用（写入助手消息）。推迟到离开 pending、或权限请求时才声明，
    /// 这样标题与参数已经是最终值（Agent 常先发占位标题，参数随后补齐）。
    /// </summary>
    private static async ValueTask DeclareAsync(Turn turn, ToolState tool)
    {
        if (tool.Declared)
        {
            return;
        }
        tool.Declared = true;
        await turn.Writer.ToolCallAsync(tool.Id, tool.DisplayName, tool.ArgumentsJson);
        turn.Block = null;
    }

    private static async ValueTask CompleteToolAsync(Turn turn, ToolState tool, string text, bool isError)
    {
        await DeclareAsync(turn, tool);
        tool.Done = true;
        await turn.Writer.ToolCompletedAsync(tool.Id, tool.DisplayName, text, isError, tool.ArgumentsJson, tool.Details());
        // 工具结果关闭了当前草稿，之后的内容进入新的助手消息
        turn.Block = null;
    }

    private static async ValueTask AbortOpenToolsAsync(Turn turn, string text)
    {
        foreach (var tool in turn.Tools.Values.Where(t => !t.Done).ToArray())
        {
            await CompleteToolAsync(turn, tool, text, true);
        }
    }

    // ---------------- 队列 ----------------

    private void Enqueue(Func<ValueTask> work) => _queue.Writer.TryWrite(work);

    private Task RunOnQueueAsync(Func<ValueTask> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try
            {
                await work();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    private async Task ConsumeAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception)
            {
                // 单条更新处理失败不影响后续更新（宽松解析，§14）
            }
        }
    }

    private async Task<JsonElement> InvokeAsync(string method, JsonElement parameters, CancellationToken ct) =>
        await _rpc.InvokeRawAsync(method, parameters, ct);

    public async ValueTask DisposeAsync()
    {
        var closeCaps = AgentCapabilities.ValueKind == JsonValueKind.Object && AgentCapabilities.Obj("sessionCapabilities")?.HasObject("close") == true;
        if (closeCaps && _sessionId is not null && _rpc.IsConnected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await InvokeAsync("session/close", JsonBuilder.Object(w => w.WriteString("sessionId", _sessionId)), timeout.Token);
            }
            catch (Exception)
            {
                // 关闭失败不影响结束进程
            }
        }
        await _rpc.DisposeAsync();
        _queue.Writer.TryComplete();
        if (_process is not null)
        {
            await _process.DisposeAsync();
        }
        await _consumer;
    }

    // ---------------- 状态 ----------------

    private sealed class Turn(TurnWriter writer)
    {
        public TurnWriter Writer { get; } = writer;
        public Dictionary<string, ToolState> Tools { get; } = new(StringComparer.Ordinal);
        public (BlockKind Kind, int Handle)? Block { get; set; }
        public bool Cancelled { get; set; }
        public List<TaskCompletionSource<JsonElement>> PendingPermissions { get; } = [];
        public TaskCompletionSource CancelDeadline { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ToolState(string id)
    {
        public string Id { get; } = id;
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Status { get; set; }
        public JsonElement? RawInput { get; set; }
        public JsonElement? RawOutput { get; set; }
        public JsonElement? Content { get; set; }
        public JsonElement[]? LastDiffs { get; set; }
        public bool Declared { get; set; }
        public bool Done { get; set; }
        public bool Rejected { get; set; }

        public string DisplayName => Title ?? Name ?? Id;

        public string ArgumentsJson => RawInput is { ValueKind: JsonValueKind.Object } input ? input.GetRawText() : "{}";

        /// <summary>结果文本：内容中的文本；diff 记为“Edited &lt;path&gt;”；没有内容时用 rawOutput。</summary>
        public string ResultText()
        {
            var builder = new StringBuilder();
            if (Content is { ValueKind: JsonValueKind.Array } content)
            {
                foreach (var item in content.EnumerateArray())
                {
                    var line = item.Str("type") switch
                    {
                        "content" when item.Obj("content") is { } c && c.Str("type") == "text" => c.Str("text"),
                        "diff" => item.Str("oldText") is null && item.Prop("oldText") is null ? $"Created {item.Str("path")}" : $"Edited {item.Str("path")}",
                        "terminal" => "[terminal output unavailable]",
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(line))
                    {
                        builder.Append(builder.Length > 0 ? "\n" : "").Append(line);
                    }
                }
            }
            if (builder.Length == 0 && RawOutput is { } output)
            {
                var raw = output.ValueKind == JsonValueKind.String ? output.GetString()! : output.GetRawText();
                builder.Append(raw.Length > 8000 ? raw[..8000] + "…" : raw);
            }
            if (builder.Length == 0)
            {
                builder.Append(Status == "failed" ? (Rejected ? "The user rejected this action." : "Tool failed.") : "Done.");
            }
            return builder.ToString();
        }

        /// <summary>
        /// 界面用的 diff（保留曾出现过的最后一组 diff，避免被后续只含文本的更新冲掉）。
        /// 结构与内置 edit / write 相同（kind = diff），变更面板才能解析并撤销；一次工具调用通常只改一个文件，多个时只取第一个。
        /// </summary>
        public string? Details()
        {
            if (LastDiffs is not { Length: > 0 } diffs || diffs[0].Str("path") is not { } path)
            {
                return null;
            }
            var oldText = diffs[0].Str("oldText");
            var created = oldText is null;
            // 快照与内置工具一致：LF 文本，超过上限则不保存（不可撤销）
            var before = oldText is null ? null : oldText.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (before is not null && Encoding.UTF8.GetByteCount(before) > AcpDiffLimits.MaxSnapshotBytes)
            {
                before = null;
            }
            var diff = LineDiff.Compute(oldText ?? "", diffs[0].Str("newText") ?? "", path);
            var after = FingerprintOnDisk(path);
            return JsonBuilder.Object(w =>
            {
                w.WriteString("kind", "diff");
                w.WriteString("path", path);
                w.WriteString("diff", diff.UnifiedDiff);
                w.WriteNumber("added", diff.Added);
                w.WriteNumber("removed", diff.Removed);
                if (diff.FirstChangedLine is { } line)
                {
                    w.WriteNumber("firstChangedLine", line);
                }
                w.WriteBoolean("created", created);
                if (before is not null)
                {
                    w.WriteString("before", before);
                }
                if (after is not null)
                {
                    w.WriteString("afterSha256", after);
                }
            }).GetRawText();
        }

        /// <summary>改动完成时磁盘上文件的 SHA-256（与 FileRevert 比较的是同一种指纹）；文件不存在时为 null，此时不可撤销。</summary>
        private static string? FingerprintOnDisk(string path)
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                return null;
            }
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
    }
}
