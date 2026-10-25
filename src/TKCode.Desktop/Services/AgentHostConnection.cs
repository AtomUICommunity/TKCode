using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using TKCode.Rpc;

namespace TKCode.Desktop.Services;

/// <summary>
/// 管理 Agent Host 子进程：按需启动、建立 stdio JSON-RPC、把 Host 推送的会话事件派发到 UI 线程。
/// Host 崩溃不会影响 UI；下一次调用会重新拉起进程。
/// </summary>
public sealed class AgentHostConnection(ILogger<AgentHostConnection> logger) : IAgentHostClient, IDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private JsonRpcConnection? _rpc;
    private IAgentHostServer? _server;

    /// <summary>会话事件（已切换到 UI 线程）。</summary>
    public event Action<SessionEventNotification>? SessionEvent;

    /// <summary>Host 断开（进程退出或崩溃）。</summary>
    public event Action<string>? Disconnected;

    /// <summary>
    /// 工具审批处理器（UI 卡片，UI 线程上调用）。未设置时默认拒绝。
    /// 令牌在 Host 撤回请求（$/cancelRequest）时触发，处理器据此收起卡片。
    /// </summary>
    public Func<ToolApprovalRequest, CancellationToken, Task<ToolApprovalResponse>>? ApprovalHandler { get; set; }

    public async Task<IAgentHostServer> GetServerAsync(CancellationToken ct = default)
    {
        if (_server is not null && _rpc is { IsConnected: true })
        {
            return _server;
        }
        await _startLock.WaitAsync(ct);
        try
        {
            if (_server is not null && _rpc is { IsConnected: true })
            {
                return _server;
            }
            StartProcess();
            var info = await _server!.InitializeAsync(new InitializeRequest(RpcProtocol.Version, "TKCode.Desktop"), ct);
            logger.LogInformation("Agent host started: pid {Pid}, version {Version}", info.ProcessId, info.HostVersion);
            return _server;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private void StartProcess()
    {
        // TKCODE_AGENTHOST_DIR 用于测试（直接使用 Host 项目的构建输出）。
        var dir = Environment.GetEnvironmentVariable("TKCODE_AGENTHOST_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "agenthost");
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "TKCode.AgentHost.exe" : "TKCode.AgentHost");
        var start = File.Exists(apphost)
            ? new ProcessStartInfo(apphost)
            : new ProcessStartInfo("dotnet", $"\"{Path.Combine(dir, "TKCode.AgentHost.dll")}\"");
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = dir;

        var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start agent host.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logger.LogDebug("[host] {Line}", e.Data);
            }
        };
        process.BeginErrorReadLine();

        var rpc = RpcSerialization.CreateConnection(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        AgentHostClientDispatcher.Register(rpc, this);
        _server = new AgentHostServerProxy(rpc);
        rpc.Disconnected += reason =>
        {
            logger.LogWarning("Agent host disconnected: {Reason}", reason);
            Dispatcher.UIThread.Post(() => Disconnected?.Invoke(reason));
        };
        rpc.Start();
        _process = process;
        _rpc = rpc;
    }

    /// <summary>
    /// 会话事件先进入队列，界面线程每次取出当前积压的全部事件依次处理：高频增量（流式文本、工具参数）
    /// 不再每个都单独排队到界面线程，事件顺序保持不变。
    /// </summary>
    void IAgentHostClient.OnSessionEvent(SessionEventNotification notification) => EnqueueEvent(notification);

    /// <summary>已打开的子 Agent 转录的事件（已切换到 UI 线程，与会话事件同一队列保持顺序）。</summary>
    public event Action<SubagentEventNotification>? SubagentEvent;

    void IAgentHostClient.OnSubagentEvent(SubagentEventNotification notification) => EnqueueEvent(notification);

    private void EnqueueEvent(object notification)
    {
        _events.Enqueue(notification);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(DrainEvents);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<object> _events = new();
    private int _drainScheduled;

    private void DrainEvents()
    {
        Volatile.Write(ref _drainScheduled, 0);
        while (_events.TryDequeue(out var notification))
        {
            switch (notification)
            {
                case SessionEventNotification session:
                    SessionEvent?.Invoke(session);
                    break;
                case SubagentEventNotification subagent:
                    SubagentEvent?.Invoke(subagent);
                    break;
            }
        }
    }

    /// <summary>settings.json 变化后 Host 推送的最新模型列表（UI 线程上触发）。</summary>
    public event Action<ModelsChangedNotification>? ModelsChanged;

    void IAgentHostClient.OnModelsChanged(ModelsChangedNotification notification) =>
        Dispatcher.UIThread.Post(() => ModelsChanged?.Invoke(notification));

    /// <summary>MCP 服务器状态或配置变化（UI 线程上触发）。</summary>
    public event Action<McpChangedNotification>? McpChanged;

    void IAgentHostClient.OnMcpChanged(McpChangedNotification notification) =>
        Dispatcher.UIThread.Post(() => McpChanged?.Invoke(notification));

    /// <summary>command / skill / role 的定义文件变化（UI 线程上触发）。</summary>
    public event Action<AssetsChangedNotification>? AssetsChanged;

    void IAgentHostClient.OnAssetsChanged(AssetsChangedNotification notification) =>
        Dispatcher.UIThread.Post(() => AssetsChanged?.Invoke(notification));

    /// <summary>MCP elicitation 的处理器（UI 线程上调用）；没有设置时一律 cancel。</summary>
    public Func<McpElicitRequest, CancellationToken, Task<McpElicitResponse>>? ElicitationHandler { get; set; }

    async Task<McpElicitResponse> IAgentHostClient.RequestMcpElicitationAsync(McpElicitRequest request, CancellationToken cancellationToken)
    {
        var handler = ElicitationHandler;
        if (handler is null)
        {
            return new McpElicitResponse("cancel");
        }
        return await Dispatcher.UIThread.InvokeAsync(() => handler(request, cancellationToken)).WaitAsync(cancellationToken);
    }

    /// <summary>Agent 提问的处理器（问答面板，UI 线程上调用）；没有设置时视为拒绝回答。</summary>
    public Func<UserInputRequest, CancellationToken, Task<UserInputResponse>>? UserInputHandler { get; set; }

    async Task<UserInputResponse> IAgentHostClient.RequestUserInputAsync(UserInputRequest request, CancellationToken cancellationToken)
    {
        var handler = UserInputHandler;
        if (handler is null)
        {
            return UserInputResponse.Decline;
        }
        return await Dispatcher.UIThread.InvokeAsync(() => handler(request, cancellationToken)).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// 计划审批的处理器（审批面板，UI 线程上调用；docs/design/plan-mode.md §13.1）；没有设置时视为暂不批准。
    /// 令牌在 Host 撤回请求（$/cancelRequest）时触发，处理器据此收起面板。
    /// </summary>
    public Func<PlanReviewRequest, CancellationToken, Task<PlanReviewResponse>>? PlanReviewHandler { get; set; }

    async Task<PlanReviewResponse> IAgentHostClient.ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken)
    {
        var handler = PlanReviewHandler;
        if (handler is null)
        {
            return PlanReviewResponse.Dismiss;
        }
        return await Dispatcher.UIThread.InvokeAsync(() => handler(request, cancellationToken)).WaitAsync(cancellationToken);
    }

    /// <summary>MCP OAuth：用系统默认浏览器打开授权页（Host 在本机回环地址等待回调）。</summary>
    Task<McpOpenUrlResponse> IAgentHostClient.OpenMcpUrlAsync(McpOpenUrlRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return Task.FromResult(new McpOpenUrlResponse(false));
        }
        try
        {
            var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { uri.AbsoluteUri } }
                : OperatingSystem.IsWindows() ? new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open") { ArgumentList = { uri.AbsoluteUri } };
            using var process = Process.Start(start);
            return Task.FromResult(new McpOpenUrlResponse(process is not null));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Opening the MCP login page failed");
            return Task.FromResult(new McpOpenUrlResponse(false));
        }
    }

    async Task<ToolApprovalResponse> IAgentHostClient.RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var handler = ApprovalHandler;
        if (handler is null)
        {
            return new ToolApprovalResponse(false, "No approval handler.");
        }
        return await Dispatcher.UIThread.InvokeAsync(() => handler(request, cancellationToken));
    }

    public void Dispose()
    {
        try
        {
            _rpc?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (_process is { HasExited: false } p)
            {
                p.StandardInput.Close();
                if (!p.WaitForExit(2000))
                {
                    p.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error while stopping agent host");
        }
    }
}
