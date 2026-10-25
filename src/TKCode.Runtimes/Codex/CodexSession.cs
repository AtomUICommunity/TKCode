using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TKCode.Agent.Questions;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Rpc;
using TKCode.Runtimes.Json;

namespace TKCode.Runtimes.Codex;

/// <summary>线程级设置：线程启动时发送一次，之后只在变化时作为 turn/start 的覆盖发送（这些覆盖是 sticky 的）。</summary>
/// <param name="ReadOnly">只读沙箱（TKCode 的 Plan 模式，docs/design/composer.md §11），优先于完全授权。</param>
/// <param name="WritableRoots">workspace-write 沙箱下 cwd 以外的可写目录（docs/design/projects.md §6.8）；thread/start 不能携带，随 turn/start 覆盖。</param>
internal sealed record CodexThreadSettings(string? Model, string ApprovalPolicy, bool FullAccess, bool NetworkAccess, string? ServiceTier,
    bool ReadOnly = false, IReadOnlyList<string>? WritableRoots = null)
{
    public IReadOnlyList<string> Roots => WritableRoots ?? [];
}

/// <summary>
/// 一个 Codex 会话 = 一个 <c>codex app-server</c> 进程 + 一个主线程（规格 docs/protocols/codex-app-server.md）。
/// <list type="bullet">
/// <item>按行分帧、无 <c>jsonrpc</c> 字段；通知与服务端请求在读循环中按到达顺序进入同一个队列，由单个消费者处理。</item>
/// <item>轮次只以本线程、本轮的 <c>turn/completed</c> 结束（§5.6）；其他线程（子 Agent）的通知被忽略。</item>
/// <item>审批在消费者之外异步等待用户；轮次结束或 <c>serverRequest/resolved</c> 后不再回复（§6.1）。</item>
/// </list>
/// </summary>
internal sealed class CodexSession : IRuntimeSession
{
    /// <summary>可写目录随每轮的 sandboxPolicy 发送（sticky），目录变化不需要重启 app-server。</summary>
    public bool AppliesDirectoriesPerTurn => true;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InterruptGrace = TimeSpan.FromSeconds(10);

    private readonly JsonRpcConnection _rpc;
    private readonly IAsyncDisposable? _process;
    private readonly Func<string>? _describeExit;
    private readonly IRuntimeHost _host;
    private readonly string? _developerInstructions;
    private readonly Channel<Func<ValueTask>> _queue = Channel.CreateUnbounded<Func<ValueTask>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private Task _consumer = Task.CompletedTask;
    private string? _threadId;
    private CodexThreadSettings? _thread;
    private Turn? _turn;
    private CodexUsage? _baseline;

    /// <summary>子 Agent 线程（parentThreadId 为主线程，docs/design/subagent-view.md §5.6）。</summary>
    private readonly Dictionary<string, ChildThread> _children = new(StringComparer.Ordinal);

    /// <summary>登记之前就到达的子线程通知（thread/started 或 spawnAgent 之前），登记后按顺序补上。</summary>
    private readonly Dictionary<string, List<(string Method, JsonElement Params)>> _orphans = new(StringComparer.Ordinal);
    private const int MaxOrphans = 2000;

    /// <summary>最近一轮的模型与选项（子 Agent 的写入器沿用）。</summary>
    private Turn? _lastTurn;

    public CodexSession(Stream stdout, Stream stdin, IRuntimeHost host, string? developerInstructions,
        IAsyncDisposable? process = null, Func<string>? describeExit = null)
    {
        _host = host;
        _developerInstructions = developerInstructions;
        _process = process;
        _describeExit = describeExit;
        _rpc = new JsonRpcConnection(stdout, stdin, new JsonRpcConnectionOptions
        {
            Framing = JsonRpcFraming.NewlineDelimited,
            WriteVersionField = false,
            SendCancelNotifications = false,
            InlineRequestDispatch = true,
        });
        _rpc.SetFallbackNotificationHandler(OnNotification);
        _rpc.SetFallbackRequestHandler(OnRequestAsync);
    }

    public string? NativeSessionId => _threadId;

    /// <summary>thread/start 响应中实际生效的模型。</summary>
    public string? EffectiveModel => _thread?.Model;

    public static string ClientVersion { get; set; } =
        (typeof(CodexSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0").Split('+')[0];

    // ---------------- 握手 ----------------

    /// <summary>initialize + initialized（§2）。返回 initialize 的结果。</summary>
    public async Task<JsonElement> InitializeAsync(CancellationToken ct)
    {
        _rpc.Start();
        _consumer = Task.Run(ConsumeAsync);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        var result = await _rpc.InvokeRawAsync("initialize", JsonBuilder.Object(w =>
        {
            w.WriteStartObject("clientInfo");
            w.WriteString("name", "tkcode");
            w.WriteString("title", "TKCode");
            w.WriteString("version", ClientVersion);
            w.WriteEndObject();
            w.WriteStartObject("capabilities");
            w.WriteBoolean("experimentalApi", false);
            w.WriteStartArray("optOutNotificationMethods");
            w.WriteStringValue("remoteControl/status/changed");
            w.WriteEndArray();
            w.WriteEndObject();
        }), timeout.Token);
        _rpc.NotifyRaw("initialized", null);
        return result;
    }

    public Task<JsonElement> InvokeAsync(string method, JsonElement? parameters, CancellationToken ct) =>
        _rpc.InvokeRawAsync(method, parameters, ct);

    /// <summary>恢复（thread/resume，失败回退 thread/start）或新建线程（§3）。</summary>
    public async Task StartThreadAsync(string cwd, TurnOptions options, string? resumeThreadId, CancellationToken ct)
    {
        var settings = Settings(options);
        if (resumeThreadId is not null)
        {
            try
            {
                var resumed = await _rpc.InvokeRawAsync("thread/resume", ThreadParams(cwd, settings, resumeThreadId), ct);
                Adopt(resumed, settings, isNew: false);
                return;
            }
            catch (RpcRemoteException)
            {
                // rollout 不存在等：回退为新线程，并上报新的 id（§3.3）
            }
        }
        var started = await _rpc.InvokeRawAsync("thread/start", ThreadParams(cwd, settings, null), ct);
        Adopt(started, settings, isNew: true);
    }

    private void Adopt(JsonElement response, CodexThreadSettings requested, bool isNew)
    {
        var threadId = response.Obj("thread")?.Str("id") ?? throw new InvalidOperationException("Codex returned no thread id.");
        lock (_gate)
        {
            _threadId = threadId;
            // 以服务端实际生效的值为基线（config.toml 可能设置了 service_tier 等）：
            // TKCode 的设置与之不同时，turn/start 会显式覆盖（快速模式关闭时发 null 清除）
            var sandbox = response.Obj("sandbox");
            _thread = requested with
            {
                Model = response.Str("model") ?? requested.Model,
                ServiceTier = response.Str("serviceTier"),
                ApprovalPolicy = response.Str("approvalPolicy") ?? requested.ApprovalPolicy,
                FullAccess = sandbox is { } sb ? sb.Str("type") == "dangerFullAccess" : requested.FullAccess,
                ReadOnly = sandbox is { } ro ? ro.Str("type") == "readOnly" : requested.ReadOnly,
                NetworkAccess = sandbox is { } net ? net.Bool("networkAccess") : requested.NetworkAccess,
                // thread/start 只有 sandbox 字符串：以服务端报告的为准，需要时由第一轮的 sandboxPolicy 覆盖
                WritableRoots = sandbox is { } roots ? roots.Arr("writableRoots").Select(r => r.GetString()).OfType<string>().ToArray() : [],
            };
            // 新线程的累计用量从 0 开始；恢复的线程在本轮第一条用量通知时用 total − last 作为基线（§5.5）
            _baseline = isNew ? CodexUsage.Zero : null;
        }
        _host.OnNativeSessionId(threadId);
    }

    /// <summary>注入的 TKCode MCP 桥接（StartThreadAsync 之前设置）。</summary>
    public McpBridgeLaunch? McpBridge { get; set; }

    internal JsonElement ThreadParams(string cwd, CodexThreadSettings settings, string? resumeThreadId) => JsonBuilder.Object(w =>
    {
        if (resumeThreadId is not null)
        {
            w.WriteString("threadId", resumeThreadId);
            w.WriteBoolean("excludeTurns", true);
        }
        w.WriteString("cwd", cwd);
        if (settings.Model is not null)
        {
            w.WriteString("model", settings.Model);
        }
        w.WriteString("approvalPolicy", settings.ApprovalPolicy);
        w.WriteString("sandbox", settings.ReadOnly ? "read-only" : settings.FullAccess ? "danger-full-access" : "workspace-write");
        if (settings.ServiceTier is not null)
        {
            w.WriteString("serviceTier", settings.ServiceTier);
        }
        if (_developerInstructions is not null)
        {
            w.WriteString("developerInstructions", _developerInstructions);
        }
        w.WriteStartObject("config");
        w.WriteString("model_reasoning_summary", "auto");
        if (McpBridge is { } bridge)
        {
            // TKCode 的 MCP 桥接：按线程覆盖 config.toml 的 mcp_servers.tkcode（点分键，§3）
            w.WriteStartObject($"mcp_servers.{McpBridgeLaunch.ServerName}");
            bridge.WriteObject(w);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    });

    /// <summary>
    /// 权限档位 → Codex 预设（§7.3）：询问 = on-request + workspace-write；自动放行 = never + workspace-write + 联网；
    /// 完全授权 = never + danger-full-access。
    /// </summary>
    private static CodexThreadSettings Settings(TurnOptions options) => new(
        options.Model.Id == CodexRuntime.DefaultModelId ? null : options.Model.Id,
        options.AutoApprove ? "never" : "on-request",
        options.Permission == TKCode.Agent.PermissionMode.FullAccess,
        options.Permission == TKCode.Agent.PermissionMode.AutoApprove,
        options.FastMode && options.Model.SupportsFastMode ? "priority" : null,
        options.Mode.ReadOnly,
        options.AdditionalDirectories);

    // ---------------- 轮次 ----------------

    public async Task RunTurnAsync(UserMessage message, TurnOptions options, TurnWriter writer, CancellationToken cancellationToken)
    {
        var turn = new Turn(writer, options);
        await RunOnQueueAsync(() =>
        {
            _turn = turn;
            _lastTurn = turn;
            return ValueTask.CompletedTask;
        });

        try
        {
            var response = await _rpc.InvokeRawAsync("turn/start", TurnParams(message, options), cancellationToken);
            if (response.Obj("turn")?.Str("id") is { } id)
            {
                await RunOnQueueAsync(() => AdoptTurnIdAsync(turn, id));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // turn/start 尚未返回就被取消：仍需等待 turn/started 之后中断（§4.3）
        }
        catch (RpcRemoteException ex)
        {
            await RunOnQueueAsync(() => FinishAsync(turn, StopReason.Error, $"Codex: {ex.Message}"));
            return;
        }

        using (cancellationToken.Register(() => Enqueue(() => RequestInterruptAsync(turn))))
        {
            var completed = turn.Completed.Task;
            await Task.WhenAny(completed, _rpc.Completion);
            if (!completed.IsCompleted && !_rpc.IsConnected)
            {
                await RunOnQueueAsync(() => FinishAsync(turn, StopReason.Error, _describeExit?.Invoke() ?? "Codex exited unexpectedly."));
                throw new InvalidOperationException(_describeExit?.Invoke() ?? "Codex exited unexpectedly.");
            }
            if (cancellationToken.IsCancellationRequested && !completed.IsCompleted &&
                await Task.WhenAny(completed, Task.Delay(InterruptGrace)) != completed)
            {
                await RunOnQueueAsync(() => FinishAsync(turn, StopReason.Aborted, "Request aborted"));
                // 中断后不结束的进程视为卡死：交给 ExternalAgent 丢弃，下一次提问时 thread/resume。
                throw new TimeoutException($"Codex did not stop within {InterruptGrace.TotalSeconds:0} seconds after interrupt.");
            }
            await completed;
        }
    }

    // UserInput[]：文本，以及图片（base64 data URL）
    private static void WriteInput(System.Text.Json.Utf8JsonWriter w, UserMessage message)
    {
        w.WriteStartArray("input");
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextContent text:
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", text.Text);
                    w.WriteStartArray("text_elements");
                    w.WriteEndArray();
                    w.WriteEndObject();
                    break;
                case ImageContent { HasData: true } image:
                    w.WriteStartObject();
                    w.WriteString("type", "image");
                    w.WriteString("url", $"data:{image.MimeType};base64,{image.Data}");
                    w.WriteEndObject();
                    break;
            }
        }
        w.WriteEndArray();
    }

    private JsonElement TurnParams(UserMessage message, TurnOptions options) => JsonBuilder.Object(w =>
    {
        w.WriteString("threadId", _threadId);
        WriteInput(w, message);

        // sticky 覆盖：只在与线程当前设置不同时发送（§4.1、§7.3）
        var wanted = Settings(options);
        var current = _thread!;
        if (wanted.Model is not null && wanted.Model != current.Model)
        {
            w.WriteString("model", wanted.Model);
        }
        if (wanted.ApprovalPolicy != current.ApprovalPolicy)
        {
            w.WriteString("approvalPolicy", wanted.ApprovalPolicy);
        }
        var rootsChanged = !wanted.FullAccess && !wanted.ReadOnly && !wanted.Roots.SequenceEqual(current.Roots);
        if (wanted.ReadOnly != current.ReadOnly || wanted.FullAccess != current.FullAccess ||
            (!wanted.FullAccess && wanted.NetworkAccess != current.NetworkAccess) || rootsChanged)
        {
            w.WriteStartObject("sandboxPolicy");
            if (wanted.ReadOnly)
            {
                w.WriteString("type", "readOnly");
            }
            else if (wanted.FullAccess)
            {
                w.WriteString("type", "dangerFullAccess");
            }
            else
            {
                w.WriteString("type", "workspaceWrite");
                w.WriteBoolean("networkAccess", wanted.NetworkAccess);
                w.WriteStartArray("writableRoots");
                foreach (var root in wanted.Roots)
                {
                    w.WriteStringValue(root);
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        if (wanted.ServiceTier != current.ServiceTier)
        {
            if (wanted.ServiceTier is null)
            {
                w.WriteNull("serviceTier"); // 显式 null 表示清除
            }
            else
            {
                w.WriteString("serviceTier", wanted.ServiceTier);
            }
        }
        _thread = current with { Model = wanted.Model ?? current.Model, ApprovalPolicy = wanted.ApprovalPolicy, FullAccess = wanted.FullAccess,
            NetworkAccess = wanted.FullAccess ? current.NetworkAccess : wanted.NetworkAccess, ServiceTier = wanted.ServiceTier, ReadOnly = wanted.ReadOnly,
            WritableRoots = wanted.FullAccess || wanted.ReadOnly ? current.WritableRoots : wanted.WritableRoots };

        var efforts = options.Model.Compat?.ReasoningEfforts;
        var off = options.ThinkingLevel == ThinkingLevel.Off;
        if (efforts is { Count: > 0 })
        {
            w.WriteString("effort", off && !efforts.Contains("none") ? efforts[0] : ThinkingLevels.ChooseEffort(options.ThinkingLevel, efforts));
        }
        // 每轮都请求推理摘要，否则多数配置下没有任何思考增量（§4.1）
        w.WriteString("summary", off ? "none" : "auto");
    });

    /// <summary>运行中追加输入（turn/steer，§4.2）；失败时返回 false，由调用方排队到下一轮。</summary>
    public async ValueTask<bool> SteerAsync(UserMessage message, CancellationToken cancellationToken)
    {
        var turnId = _turn?.Id;
        if (turnId is null || _threadId is null)
        {
            return false;
        }
        try
        {
            await _rpc.InvokeRawAsync("turn/steer", JsonBuilder.Object(w =>
            {
                w.WriteString("threadId", _threadId);
                w.WriteString("expectedTurnId", turnId);
                WriteInput(w, message);
            }), cancellationToken);
            return true;
        }
        catch (RpcRemoteException)
        {
            return false;
        }
    }

    private ValueTask AdoptTurnIdAsync(Turn turn, string turnId)
    {
        if (turn.Id is null && !turn.Done)
        {
            turn.Id = turnId;
            if (turn.InterruptPending)
            {
                SendInterrupt(turn);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>中断（§10.1）：已知 turnId 时发 turn/interrupt，否则等 turn/started 后再发；未决审批交给服务端清除。</summary>
    private ValueTask RequestInterruptAsync(Turn turn)
    {
        if (turn.Done || turn.InterruptPending)
        {
            return ValueTask.CompletedTask;
        }
        turn.InterruptPending = true;
        turn.ApprovalScope.Cancel();
        if (turn.Id is not null)
        {
            SendInterrupt(turn);
        }
        return ValueTask.CompletedTask;
    }

    private void SendInterrupt(Turn turn) =>
        _ = _rpc.InvokeRawAsync("turn/interrupt", JsonBuilder.Object(w =>
        {
            w.WriteString("threadId", _threadId);
            w.WriteString("turnId", turn.Id);
        })).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    // ---------------- 入站消息 ----------------

    private void OnNotification(string method, JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } p)
        {
            return;
        }
        Enqueue(() => HandleNotificationAsync(method, p));
    }

    private async ValueTask HandleNotificationAsync(string method, JsonElement p)
    {
        if (method == "thread/started")
        {
            await OnThreadStartedAsync(p.Obj("thread") ?? default);
            return;
        }
        // 子 Agent 线程的通知进入它自己的转录，不影响当前轮次（§1.3）
        if (p.Str("threadId") is { } threadId && threadId != _threadId)
        {
            await OnChildNotificationAsync(threadId, method, p);
            return;
        }
        await HandleTurnNotificationAsync(_turn, method, p);
    }

    /// <summary>一个线程的轮次通知（主线程或子 Agent 线程，后者的 <paramref name="turn"/> 带 <see cref="Turn.Child"/>）。</summary>
    private async ValueTask HandleTurnNotificationAsync(Turn? turn, string method, JsonElement p)
    {
        switch (method)
        {
            case "turn/started" when turn is not null:
                if (p.Obj("turn")?.Str("id") is { } startedId)
                {
                    await AdoptTurnIdAsync(turn, startedId);
                }
                break;
            case "turn/completed" or "turn/failed" or "turn/aborted" when turn is not null:
                await OnTurnCompletedAsync(turn, p.Obj("turn") ?? default, method);
                break;
            case "item/started" when turn is not null && IsCurrent(turn, p):
                await OnItemStartedAsync(turn, p.Obj("item") ?? default);
                break;
            case "item/completed" when turn is not null && IsCurrent(turn, p):
                await OnItemCompletedAsync(turn, p.Obj("item") ?? default);
                break;
            case "item/agentMessage/delta" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } messageId && p.Str("delta") is { } text && turn.Blocks.TryGetValue(messageId, out var textBlock))
                {
                    await turn.Writer.AppendAsync(textBlock.Handle, text);
                }
                break;
            case "item/reasoning/summaryPartAdded" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } partItem && turn.Blocks.TryGetValue(partItem, out var part) && part.HasText)
                {
                    part.PendingSeparator = true;
                }
                break;
            case "item/reasoning/summaryTextDelta" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } reasoningId && p.Str("delta") is { } summary)
                {
                    await AppendReasoningAsync(turn, reasoningId, summary);
                }
                break;
            case "item/commandExecution/outputDelta" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } commandId && p.Str("delta") is { } output && turn.Tools.TryGetValue(commandId, out var command))
                {
                    command.Output.Append(output);
                    await turn.Writer.ToolStartedAsync(command.Id, command.Name, command.ArgumentsJson);
                    await turn.Writer.ToolProgressAsync(command.Id, command.Name, output);
                }
                break;
            case "item/fileChange/patchUpdated" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } patchId && turn.Tools.TryGetValue(patchId, out var patch) && p.Prop("changes") is { } changes)
                {
                    patch.Changes = changes.Clone();
                }
                break;
            case "item/mcpToolCall/progress" when turn is not null && IsCurrent(turn, p):
                if (p.Str("itemId") is { } mcpId && p.Str("message") is { } progress && turn.Tools.TryGetValue(mcpId, out var mcp))
                {
                    await turn.Writer.ToolProgressAsync(mcp.Id, mcp.Name, progress);
                }
                break;
            case "thread/tokenUsage/updated" when turn?.Child is null:
                OnUsage(turn, p.Obj("tokenUsage") ?? default);
                break;
            case "error" when turn is not null && IsCurrent(turn, p):
                if (!p.Bool("willRetry") && p.Obj("error")?.Str("message") is { } error)
                {
                    turn.LastError = error;
                }
                break;
            case "serverRequest/resolved":
                if (p.Prop("requestId") is { } requestId && turn?.Approvals.Remove(requestId.GetRawText(), out var pending) == true)
                {
                    pending.Cancel();
                }
                break;
            // 其他通知（thread/status/changed、turn/plan/updated、turn/diff/updated、warning 等）首版忽略；未知通知一律忽略（§5.1）
        }
    }

    /// <summary>通知属于当前轮次（turnId 尚未确定时视为属于当前轮次）。跨轮次到达的后台命令完成通知被忽略（§5.2 第 4 条）。</summary>
    private static bool IsCurrent(Turn turn, JsonElement p) =>
        !turn.Done && (turn.Id is null || p.Str("turnId") is not { } id || id == turn.Id);

    private async ValueTask OnItemStartedAsync(Turn turn, JsonElement item)
    {
        if (item.Str("id") is not { } id)
        {
            return;
        }
        switch (item.Str("type"))
        {
            case "agentMessage":
                turn.Blocks[id] = new Block(await turn.Writer.BeginBlockAsync(BlockKind.Text));
                break;
            case "reasoning":
                break; // 第一个增量到达时才开块，避免空的思考块（§5.4）
            case "userMessage" or "contextCompaction" or "plan":
                break;
            case "subAgentActivity":
                await OnSubAgentActivityAsync(item);
                break;
            default:
                var tool = ToolOf(item);
                if (tool is null)
                {
                    break;
                }
                turn.Tools[id] = tool;
                await turn.Writer.ToolCallAsync(tool.Id, tool.Name, tool.ArgumentsJson);
                if (turn.Child is null && IsSpawn(item))
                {
                    await OnSpawnAsync(turn, tool, item);
                }
                // 命令与文件修改可能需要审批：审批通过、出现输出或完成时才算开始执行；其他工具立即开始
                if (turn.Options.AutoApprove || tool.Kind is not ("commandExecution" or "fileChange"))
                {
                    await turn.Writer.ToolStartedAsync(tool.Id, tool.Name, tool.ArgumentsJson);
                }
                break;
        }
    }

    private async ValueTask OnItemCompletedAsync(Turn turn, JsonElement item)
    {
        if (item.Str("id") is not { } id)
        {
            return;
        }
        switch (item.Str("type"))
        {
            case "agentMessage":
                if (turn.Blocks.Remove(id, out var text))
                {
                    await turn.Writer.EndBlockAsync(text.Handle, item.Str("text"));
                }
                else if (item.Str("text") is { Length: > 0 } whole)
                {
                    await turn.Writer.TextAsync(whole);
                }
                break;
            case "reasoning":
                var summary = string.Join("\n\n", item.Arr("summary").Select(s => s.GetString()).OfType<string>());
                if (turn.Blocks.Remove(id, out var reasoning))
                {
                    await turn.Writer.EndBlockAsync(reasoning.Handle, summary.Length > 0 ? summary : null);
                }
                else if (summary.Length > 0)
                {
                    await turn.Writer.ThinkingAsync(summary);
                }
                break;
            case "userMessage" when turn.Child is not null:
                // 子 Agent 收到的任务（父 Agent 的 prompt 或 sendInput）：主线程的用户消息由 TKCode 自己写入
                var prompt = string.Join("\n", item.Arr("content").Where(c => c.Str("type") == "text").Select(c => c.Str("text")).OfType<string>());
                if (prompt.Length > 0)
                {
                    await turn.Writer.UserMessageAsync(UserMessage.FromText(prompt));
                }
                break;
            case "userMessage" or "contextCompaction" or "plan":
                break;
            case "subAgentActivity":
                await OnSubAgentActivityAsync(item);
                break;
            default:
                if (!turn.Tools.TryGetValue(id, out var tool))
                {
                    tool = ToolOf(item);
                    if (tool is null)
                    {
                        break;
                    }
                    turn.Tools[id] = tool;
                }
                if (turn.Child is null && IsSpawn(item))
                {
                    await OnSpawnAsync(turn, tool, item);
                }
                await CompleteToolAsync(turn, tool, item);
                break;
        }
    }

    private async ValueTask AppendReasoningAsync(Turn turn, string itemId, string delta)
    {
        if (!turn.Blocks.TryGetValue(itemId, out var block))
        {
            block = new Block(await turn.Writer.BeginBlockAsync(BlockKind.Thinking));
            turn.Blocks[itemId] = block;
        }
        if (block.PendingSeparator)
        {
            // 摘要分段之间没有分隔符，补空行（§5.2 第 6 条）
            await turn.Writer.AppendAsync(block.Handle, "\n\n");
            block.PendingSeparator = false;
        }
        await turn.Writer.AppendAsync(block.Handle, delta);
        block.HasText = true;
    }

    private static ToolState? ToolOf(JsonElement item)
    {
        var id = item.Str("id")!;
        switch (item.Str("type"))
        {
            case "commandExecution":
                return new ToolState(id, "commandExecution", "shell", JsonBuilder.Object(w =>
                {
                    w.WriteString("command", item.Str("command"));
                    w.WriteString("cwd", item.Str("cwd"));
                    if (item.Prop("commandActions") is { } actions)
                    {
                        w.WritePropertyName("commandActions");
                        actions.WriteTo(w);
                    }
                }).GetRawText()) { Title = item.Str("command") };
            case "fileChange":
                var changes = item.Prop("changes") ?? JsonBuilder.Parse("[]");
                return new ToolState(id, "fileChange", "apply_patch", JsonBuilder.Object(w =>
                {
                    w.WritePropertyName("changes");
                    changes.WriteTo(w);
                }).GetRawText()) { Changes = changes.Clone() };
            case "mcpToolCall":
                var arguments = item.Prop("arguments");
                return new ToolState(id, "mcpToolCall", $"mcp__{item.Str("server")}__{item.Str("tool")}",
                    arguments is { ValueKind: JsonValueKind.Object } a ? a.GetRawText() : "{}");
            case "webSearch":
                return new ToolState(id, "webSearch", "web_search", JsonBuilder.Object(w => w.WriteString("query", item.Str("query"))).GetRawText());
            case "imageView":
                return new ToolState(id, "imageView", "view_image", JsonBuilder.Object(w => w.WriteString("path", item.Str("path"))).GetRawText());
            case "collabAgentToolCall":
                // 子 Agent 协作：spawnAgent 显示为子 Agent 卡片；参数取 prompt / model / 接收方（§5.6）
                return new ToolState(id, "collabAgentToolCall", IsSpawn(item) ? SpawnToolName : item.Str("tool") ?? "collabAgentToolCall",
                    JsonBuilder.Object(w =>
                    {
                        if (item.Str("prompt") is { } prompt)
                        {
                            w.WriteString("prompt", prompt);
                        }
                        if (item.Str("model") is { } model)
                        {
                            w.WriteString("model", model);
                        }
                        w.WriteStartArray("receiverThreadIds");
                        foreach (var receiver in item.Arr("receiverThreadIds"))
                        {
                            w.WriteStringValue(receiver.GetString());
                        }
                        w.WriteEndArray();
                    }).GetRawText());
            case "dynamicToolCall":
                var args = item.Prop("arguments");
                return new ToolState(id, item.Str("type")!, item.Str("tool") ?? item.Str("type")!,
                    args is { ValueKind: JsonValueKind.Object } d ? d.GetRawText() : "{}");
            case "imageGeneration":
                return new ToolState(id, "imageGeneration", "image_generation", "{}");
            default:
                return null; // 未知 type 忽略（§12 第 19 条）
        }
    }

    private static async ValueTask CompleteToolAsync(Turn turn, ToolState tool, JsonElement item)
    {
        if (tool.Done)
        {
            return;
        }
        tool.Done = true;
        var status = item.Str("status");
        string text;
        var isError = status is not ("completed" or null);
        string? details = null;
        switch (tool.Kind)
        {
            case "commandExecution":
                var exitCode = item.Long("exitCode");
                isError |= exitCode is not (null or 0);
                text = status == "declined"
                    ? "The user declined to run this command."
                    : item.Str("aggregatedOutput") ?? tool.Output.ToString();
                if (exitCode is { } code && code != 0 && status != "declined")
                {
                    text = (text.Length > 0 ? text.TrimEnd() + "\n" : "") + $"Exit code {code}";
                }
                break;
            case "fileChange":
                var changes = item.Prop("changes") ?? tool.Changes;
                details = changes is { } c ? JsonBuilder.Object(w =>
                {
                    w.WritePropertyName("changes");
                    c.WriteTo(w);
                }).GetRawText() : null;
                var paths = changes?.ValueKind == JsonValueKind.Array
                    ? changes.Value.EnumerateArray().Select(x => x.Str("path")).OfType<string>().ToArray()
                    : [];
                text = status switch
                {
                    "declined" => "The user declined this change.",
                    "failed" => "Failed to apply the change.",
                    _ => paths.Length == 0 ? "No files changed." : "Modified " + string.Join(", ", paths),
                };
                break;
            case "mcpToolCall":
                text = item.Obj("error")?.Str("message")
                       ?? string.Join("\n", (item.Obj("result") ?? default).Arr("content").Where(x => x.Str("type") == "text")
                           .Select(x => x.Str("text")).OfType<string>());
                break;
            case "webSearch":
                text = item.Str("query") is { } query ? $"Searched: {query}" : "Searched the web.";
                break;
            case "imageGeneration":
                text = item.Str("savedPath") is { } saved ? $"Saved image to {saved}" : "Image generated.";
                break;
            default:
                text = isError ? "Tool failed." : "Done.";
                break;
        }
        details ??= tool.Details;
        await turn.Writer.ToolCompletedAsync(tool.Id, tool.Name, text, isError, tool.ArgumentsJson, details);
        // 工具结果关闭了草稿：其后的文本 / 思考块已失效，重新开始
        turn.Blocks.Clear();
    }

    private async ValueTask OnTurnCompletedAsync(Turn turn, JsonElement completed, string method)
    {
        if (turn.Done || (turn.Id is not null && completed.Str("id") is { } id && id != turn.Id))
        {
            return; // 旧的或重复的 completed（§12 第 14 条）
        }
        var status = method switch
        {
            "turn/failed" => "failed",
            "turn/aborted" => "interrupted",
            _ => completed.Str("status"),
        };
        var (reason, message) = status switch
        {
            "completed" => (StopReason.Stop, (string?)null),
            "interrupted" => (StopReason.Aborted, "Request aborted"),
            _ => (StopReason.Error, completed.Obj("error")?.Str("message") ?? turn.LastError ?? "Codex turn failed"),
        };
        await FinishAsync(turn, reason, message);
    }

    private async ValueTask FinishAsync(Turn turn, StopReason reason, string? message)
    {
        if (turn.Done)
        {
            return;
        }
        turn.Done = true;
        turn.ApprovalScope.Cancel();
        foreach (var tool in turn.Tools.Values.Where(t => !t.Done))
        {
            tool.Done = true;
            await turn.Writer.ToolCompletedAsync(tool.Id, tool.Name, "Operation aborted.", true, tool.ArgumentsJson);
        }
        if (turn.Usage is { } usage)
        {
            turn.Writer.SetUsage(usage);
        }
        if (turn.Child is { } child)
        {
            child.Turn = null;
            await child.Transcript!.EndRunAsync(reason, message);
            turn.Completed.TrySetResult();
            return;
        }
        await turn.Writer.CompleteAsync(reason, message);
        _turn = null;
        turn.Completed.TrySetResult();
    }

    /// <summary>整轮用量 = 轮末 total − 轮初 total；Input 扣除缓存（§5.5）。</summary>
    private void OnUsage(Turn? turn, JsonElement tokenUsage)
    {
        if (CodexUsage.Parse(tokenUsage.Obj("total")) is not { } total)
        {
            return;
        }
        var baseline = _baseline ?? (CodexUsage.Parse(tokenUsage.Obj("last")) is { } last ? total - last : total);
        _baseline ??= baseline;
        if (turn is null)
        {
            _baseline = total;
            return;
        }
        turn.StartTotal ??= baseline;
        turn.Usage = (total - turn.StartTotal.Value).ToUsage();
        _baseline = total;
    }

    // ---------------- 子 Agent 线程（docs/design/subagent-view.md §5.6） ----------------

    private const string SpawnToolName = "spawn_agent";

    private static bool IsSpawn(JsonElement item) => item.Str("type") == "collabAgentToolCall" && item.Str("tool") == "spawnAgent";

    /// <summary>thread/started：parentThreadId 为主线程的是子 Agent，登记它并补上先到的通知。</summary>
    private async ValueTask OnThreadStartedAsync(JsonElement thread)
    {
        if (thread.Str("id") is not { } id || _threadId is null || thread.Str("parentThreadId") != _threadId)
        {
            return;
        }
        // review / compact 等内部子线程不是子 Agent（source.subAgent 为字符串；thread_spawn 才是）
        if (thread.Obj("source")?.Prop("subAgent") is { ValueKind: JsonValueKind.String })
        {
            return;
        }
        var child = ChildOf(id);
        child.Nickname = thread.Str("agentNickname") ?? child.Nickname;
        child.Role = thread.Str("agentRole") ?? child.Role;
        child.Model = thread.Str("model") ?? child.Model;
        await RegisterAsync(child);
    }

    /// <summary>主线程中的 spawnAgent：关联到它派生的子线程，并推送 SubagentDetails（卡片的名称与模型）。</summary>
    private async ValueTask OnSpawnAsync(Turn turn, ToolState tool, JsonElement item)
    {
        if (item.Arr("receiverThreadIds").Select(r => r.GetString()).FirstOrDefault(r => r is { Length: > 0 }) is not { } threadId)
        {
            return;
        }
        var child = ChildOf(threadId);
        child.Model ??= item.Str("model");
        await RegisterAsync(child);
        child.Transcript?.Link(tool.Id);
        var status = item.Str("status") switch
        {
            "failed" => "failed",
            "interrupted" => "stopped",
            _ => "running",
        };
        tool.Details = SubagentDetailsJson.Create(threadId, child.Name, child.Role ?? "agent", item.Str("prompt") ?? "", status, false,
            model: child.Model ?? turn.Writer.Model.Name);
        if (!tool.Done)
        {
            await turn.Writer.ToolProgressAsync(tool.Id, tool.Name, "", tool.Details);
        }
    }

    /// <summary>subAgentActivity（主线程）：started 时带着子 Agent 解析后的模型。</summary>
    private async ValueTask OnSubAgentActivityAsync(JsonElement item)
    {
        if (item.Str("agentThreadId") is not { } threadId)
        {
            return;
        }
        var child = ChildOf(threadId);
        if (item.Str("model") is { } model)
        {
            child.Model = model;
        }
        await RegisterAsync(child);
    }

    private ChildThread ChildOf(string threadId)
    {
        if (!_children.TryGetValue(threadId, out var child))
        {
            child = _children[threadId] = new ChildThread(threadId);
        }
        return child;
    }

    /// <summary>打开（或更新）子线程的转录，补上登记之前到达的通知。</summary>
    private async ValueTask RegisterAsync(ChildThread child)
    {
        if (child.Transcript is { } existing)
        {
            existing.Describe(child.Name, child.Model);
        }
        else
        {
            child.Transcript = _host.OpenSubagent(child.ThreadId, child.Name, child.Model);
        }
        if (child.Transcript is not null && _orphans.Remove(child.ThreadId, out var pending))
        {
            foreach (var (method, p) in pending)
            {
                await OnChildNotificationAsync(child.ThreadId, method, p);
            }
        }
    }

    /// <summary>子线程的通知：轮次与条目复用主线程的映射，写入子 Agent 的转录。</summary>
    private async ValueTask OnChildNotificationAsync(string threadId, string method, JsonElement p)
    {
        if (!_children.TryGetValue(threadId, out var child) || child.Transcript is null)
        {
            // 可能是尚未登记的子 Agent（thread/started 稍后到达）；不是子 Agent 的线程最终被丢弃
            if (!_orphans.TryGetValue(threadId, out var list))
            {
                list = _orphans[threadId] = [];
            }
            if (list.Count < MaxOrphans)
            {
                list.Add((method, p.Clone()));
            }
            return;
        }
        var turn = child.Turn;
        if (turn is null && method is "turn/started" or "item/started" or "item/completed" or "item/agentMessage/delta"
                or "item/reasoning/summaryTextDelta" && _lastTurn is { } last)
        {
            turn = child.Turn = new Turn(await child.Transcript.BeginRunAsync(last.Writer.Model), last.Options) { Child = child };
        }
        await HandleTurnNotificationAsync(turn, method, p);
    }

    // ---------------- 服务端请求 ----------------

    private Task<JsonElement?> OnRequestAsync(JsonElement id, string method, JsonElement? parameters, CancellationToken ct)
    {
        var p = parameters ?? default;
        return method switch
        {
            "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/permissions/requestApproval"
                => ApproveAsync(id, method, p, ct),
            "item/tool/requestUserInput" => RequestUserInputAsync(id, p, ct),
            "mcpServer/elicitation/request" => Task.FromResult<JsonElement?>(JsonBuilder.Parse("""{"action":"decline"}""")),
            "item/tool/call" => Task.FromResult<JsonElement?>(JsonBuilder.Parse(
                """{"success":false,"contentItems":[{"type":"inputText","text":"TKCode does not provide dynamic tools"}]}""")),
            _ => throw new RpcRemoteException(JsonRpcConnection.MethodNotFound, $"unsupported method: {method}"),
        };
    }

    /// <summary>
    /// 审批（§6.8）：在队列中按顺序登记（以便从 item 中取补丁），再异步等待用户。
    /// 轮次结束、被中断或收到 serverRequest/resolved 时不回复（服务端已清除该请求，§6.1）。
    /// </summary>
    private async Task<JsonElement?> ApproveAsync(JsonElement id, string method, JsonElement p, CancellationToken ct)
    {
        var prepared = new TaskCompletionSource<(Turn Turn, RuntimeApprovalRequest Request, CancellationTokenSource Scope)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            // 子 Agent 线程的请求用它自己的轮次，标题前缀为子 Agent 名（§5.6）
            var child = p.Str("threadId") is { } threadId && threadId != _threadId ? _children.GetValueOrDefault(threadId) : null;
            var turn = child is not null ? child.Turn : _turn;
            if (turn is null || turn.Done || turn.InterruptPending)
            {
                prepared.TrySetResult(null);
                return ValueTask.CompletedTask;
            }
            var scope = CancellationTokenSource.CreateLinkedTokenSource(turn.ApprovalScope.Token);
            turn.Approvals[id.GetRawText()] = scope;
            var request = BuildApproval(turn, method, p);
            if (child is not null)
            {
                request = request with { Title = $"[{child.Name}] {request.Title ?? request.ToolName}" };
            }
            prepared.TrySetResult((turn, request, scope));
            return ValueTask.CompletedTask;
        });
        if (await prepared.Task is not { } ready)
        {
            throw new RpcSuppressReplyException();
        }
        var (turnRef, request, approvalScope) = ready;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(approvalScope.Token, ct);
        ApprovalDecision decision;
        try
        {
            decision = await _host.RequestApprovalAsync(request, linked.Token);
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            throw new RpcSuppressReplyException();
        }
        if (decision.Outcome == ApprovalOutcome.Cancel)
        {
            // 轮次被中断时宿主也会返回 Cancel：先让队列处理已排入的中断，再判断请求是否已作废
            await RunOnQueueAsync(() => ValueTask.CompletedTask);
        }
        if (approvalScope.IsCancellationRequested)
        {
            throw new RpcSuppressReplyException();
        }
        turnRef.Approvals.Remove(id.GetRawText());
        if (decision.IsAllowed && method != "item/permissions/requestApproval")
        {
            Enqueue(() => turnRef.Tools.TryGetValue(request.ToolCallId, out var tool) && !tool.Done
                ? turnRef.Writer.ToolStartedAsync(tool.Id, tool.Name, tool.ArgumentsJson)
                : ValueTask.CompletedTask);
        }
        return method == "item/permissions/requestApproval"
            ? PermissionsReply(p, decision)
            : JsonBuilder.Object(w => w.WriteString("decision", decision.Outcome switch
            {
                ApprovalOutcome.AllowOnce => "accept",
                ApprovalOutcome.AllowForSession => "acceptForSession",
                ApprovalOutcome.Cancel => "cancel",
                _ => "decline",
            }));
    }

    /// <summary>
    /// requestUserInput（§6.5，docs/design/ask-user.md §8）：交给 TKCode 的问答面板。这个请求没有对应的 ThreadItem，
    /// 转录里合成一个 request_user_input 工具调用显示问答。回答按问题 id：选项标签，自由输入写成 "user_note: …"
    /// （与 Codex TUI 一致）；拒绝回答时每个问题都是空列表。轮次结束、被中断或收到 serverRequest/resolved 时不回复。
    /// </summary>
    private async Task<JsonElement?> RequestUserInputAsync(JsonElement id, JsonElement p, CancellationToken ct)
    {
        var questions = ParseQuestions(p);
        var itemId = p.Str("itemId") is { Length: > 0 } item ? item : "request_user_input_" + id.GetRawText();
        var arguments = JsonBuilder.Object(w =>
        {
            w.WritePropertyName("questions");
            (p.Prop("questions") ?? JsonBuilder.Parse("[]")).WriteTo(w);
        }).GetRawText();
        var prepared = new TaskCompletionSource<(Turn Turn, CancellationTokenSource Scope)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            var turn = _turn;
            if (turn is null || turn.Done || turn.InterruptPending)
            {
                prepared.TrySetResult(null);
                return;
            }
            var scope = CancellationTokenSource.CreateLinkedTokenSource(turn.ApprovalScope.Token);
            turn.Approvals[id.GetRawText()] = scope;
            await turn.Writer.ToolCallAsync(itemId, QuestionToolName, arguments);
            await turn.Writer.ToolStartedAsync(itemId, QuestionToolName, arguments);
            prepared.TrySetResult((turn, scope));
        });
        if (await prepared.Task is not { } ready)
        {
            throw new RpcSuppressReplyException();
        }
        var (turnRef, scope) = ready;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, ct);
        UserInputResult result;
        try
        {
            result = await _host.RequestInputAsync(new RuntimeInputRequest(itemId, questions), linked.Token);
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            result = UserInputResult.Aborted;
        }
        if (result.Outcome == UserInputOutcome.Aborted)
        {
            await RunOnQueueAsync(() => ValueTask.CompletedTask);
        }
        var withdrawn = scope.IsCancellationRequested || linked.IsCancellationRequested;
        if (withdrawn && result.Outcome != UserInputOutcome.Aborted)
        {
            result = UserInputResult.Aborted;
        }
        turnRef.Approvals.Remove(id.GetRawText());
        var reply = JsonBuilder.Object(w =>
        {
            w.WriteStartObject("answers");
            foreach (var question in questions)
            {
                w.WriteStartObject(question.Id);
                w.WriteStartArray("answers");
                if (result.Outcome == UserInputOutcome.Answered && result.AnswerFor(question) is { } answer)
                {
                    foreach (var label in answer.Selected)
                    {
                        w.WriteStringValue(label);
                    }
                    if (answer.HasText)
                    {
                        w.WriteStringValue("user_note: " + answer.Text!.Trim());
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        });
        var text = result.Outcome switch
        {
            UserInputOutcome.Answered => reply.GetRawText(),
            UserInputOutcome.Declined => UserAnswers.DeclinedMessage,
            _ => "Operation aborted.",
        };
        var details = UserAnswers.DetailsJson(questions, result);
        // 轮次已经结束时写入器不再接受内容
        Enqueue(() => turnRef.Done ? ValueTask.CompletedTask
            : turnRef.Writer.ToolCompletedAsync(itemId, QuestionToolName, text, result.Outcome == UserInputOutcome.Aborted, arguments, details));
        if (withdrawn)
        {
            throw new RpcSuppressReplyException();
        }
        return reply;
    }

    private const string QuestionToolName = "request_user_input";

    /// <summary>requestUserInput 的 questions：{id, header, question, isOther, isSecret, options: [{label, description}] | null}。</summary>
    internal static IReadOnlyList<UserQuestion> ParseQuestions(JsonElement p)
    {
        var questions = new List<UserQuestion>();
        foreach (var q in p.Arr("questions"))
        {
            if (q.Str("id") is not { Length: > 0 } qid)
            {
                continue;
            }
            var text = q.Str("question") ?? "";
            var options = q.Arr("options")
                .Select(o => o.Str("label") is { Length: > 0 } label ? new UserQuestionOption(label, o.Str("description")) : null)
                .OfType<UserQuestionOption>()
                .ToList();
            questions.Add(new UserQuestion(qid, q.Str("header") ?? qid, text, options, MultiSelect: false,
                AllowOther: q.Bool("isOther") || options.Count == 0, IsSecret: q.Bool("isSecret")));
        }
        return questions;
    }

    private static JsonElement PermissionsReply(JsonElement p, ApprovalDecision decision) => JsonBuilder.Object(w =>
    {
        if (decision.IsAllowed && p.Obj("permissions") is { } permissions)
        {
            w.WritePropertyName("permissions");
            permissions.WriteTo(w);
            w.WriteString("scope", decision.Outcome == ApprovalOutcome.AllowForSession ? "session" : "turn");
        }
        else
        {
            w.WriteStartObject("permissions");
            w.WriteEndObject();
        }
    });

    private static RuntimeApprovalRequest BuildApproval(Turn turn, string method, JsonElement p)
    {
        var itemId = p.Str("itemId") ?? "";
        turn.Tools.TryGetValue(itemId, out var tool);
        switch (method)
        {
            case "item/commandExecution/requestApproval":
                var host = p.Obj("networkApprovalContext")?.Str("host");
                var title = host is not null ? $"Network access: {host}"
                    : p.Str("kind") == "writeStdin" ? "Write to terminal"
                    : p.Str("command") ?? tool?.Title;
                var args = JsonBuilder.Object(w =>
                {
                    w.WriteString("command", p.Str("command"));
                    w.WriteString("cwd", p.Str("cwd"));
                    if (p.Prop("commandActions") is { } actions)
                    {
                        w.WritePropertyName("commandActions");
                        actions.WriteTo(w);
                    }
                }).GetRawText();
                return new RuntimeApprovalRequest(itemId, "shell", args, ApprovalKind.Command, title, p.Str("reason"));
            case "item/fileChange/requestApproval":
                // params 中没有 changes：补丁取自同一 itemId 的 fileChange item（§6.3）
                var changes = tool?.Changes;
                var paths = changes?.ValueKind == JsonValueKind.Array
                    ? changes.Value.EnumerateArray().Select(c => c.Str("path")).OfType<string>().ToArray()
                    : [];
                return new RuntimeApprovalRequest(itemId, "apply_patch", tool?.ArgumentsJson ?? "{}", ApprovalKind.FileChange,
                    paths.Length > 0 ? string.Join(", ", paths) : "Apply patch", p.Str("reason"));
            default:
                return new RuntimeApprovalRequest(itemId, "request_permissions", p.Obj("permissions")?.GetRawText() ?? "{}", ApprovalKind.Tool,
                    "Additional permissions", p.Str("reason"));
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
                // 单条消息处理失败不影响后续（宽松解析，§12 第 24 条）
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_turn is { Done: false } turn)
        {
            Enqueue(() => RequestInterruptAsync(turn));
            await Task.WhenAny(turn.Completed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
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

    private sealed class Turn(TurnWriter writer, TurnOptions options)
    {
        public TurnWriter Writer { get; } = writer;
        public TurnOptions Options { get; } = options;
        public string? Id { get; set; }
        public bool Done { get; set; }
        public bool InterruptPending { get; set; }
        public string? LastError { get; set; }
        public CodexUsage? StartTotal { get; set; }
        public Usage? Usage { get; set; }
        public Dictionary<string, Block> Blocks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ToolState> Tools { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CancellationTokenSource> Approvals { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource ApprovalScope { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>子 Agent 线程的轮次（写入它的转录）；主线程为 null。</summary>
        public ChildThread? Child { get; init; }
    }

    /// <summary>一个子 Agent 线程：名称取 agentNickname → agentRole，转录在登记时打开。</summary>
    private sealed class ChildThread(string threadId)
    {
        public string ThreadId { get; } = threadId;
        public string? Nickname { get; set; }
        public string? Role { get; set; }
        public string? Model { get; set; }
        public string Name => Nickname ?? Role ?? "agent";
        public RuntimeSubagent? Transcript { get; set; }
        public Turn? Turn { get; set; }
    }

    private sealed class Block(int handle)
    {
        public int Handle { get; } = handle;
        public bool HasText { get; set; }
        public bool PendingSeparator { get; set; }
    }

    private sealed class ToolState(string id, string kind, string name, string argumentsJson)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string Name { get; } = name;
        public string ArgumentsJson { get; } = argumentsJson;
        public string? Title { get; init; }
        public JsonElement? Changes { get; set; }

        /// <summary>完成时附上的 Details（spawnAgent 的 SubagentDetails）。</summary>
        public string? Details { get; set; }
        public StringBuilder Output { get; } = new();
        public bool Done { get; set; }
    }
}

/// <summary>Codex 的 TokenUsageBreakdown（inputTokens 含缓存，outputTokens 含推理）。</summary>
internal readonly record struct CodexUsage(long Input, long CachedInput, long CacheWrite, long Output, long Reasoning)
{
    public static CodexUsage Zero { get; } = new(0, 0, 0, 0, 0);

    public static CodexUsage? Parse(JsonElement? e) => e is { } v
        ? new CodexUsage(v.Long("inputTokens") ?? 0, v.Long("cachedInputTokens") ?? 0, v.Long("cacheWriteInputTokens") ?? 0,
            v.Long("outputTokens") ?? 0, v.Long("reasoningOutputTokens") ?? 0)
        : null;

    public static CodexUsage operator -(CodexUsage a, CodexUsage b) =>
        new(a.Input - b.Input, a.CachedInput - b.CachedInput, a.CacheWrite - b.CacheWrite, a.Output - b.Output, a.Reasoning - b.Reasoning);

    public Usage ToUsage() => new(Math.Max(0, Input - CachedInput - CacheWrite), Math.Max(0, Output), Math.Max(0, CachedInput),
        Math.Max(0, CacheWrite), Math.Max(0, Reasoning));
}
