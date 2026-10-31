namespace TKCode.Rpc;

/// <summary>UI 侧使用的 <see cref="IAgentHostServer"/> 代理：每个方法对应一次 JSON-RPC 调用。</summary>
public sealed class AgentHostServerProxy(JsonRpcConnection rpc) : IAgentHostServer
{
    private static RpcJsonContext J => RpcJsonContext.Default;

    public Task<HostInfo> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.Initialize, request, J.InitializeRequest, J.HostInfo, cancellationToken);

    public Task<IReadOnlyList<ModelDto>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ListModels, J.IReadOnlyListModelDto, cancellationToken);

    public Task<ProvidersSnapshotDto> ListProvidersAsync(CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ListProviders, J.ProvidersSnapshotDto, cancellationToken);

    public Task<IReadOnlyList<CatalogProviderDto>> ListCatalogProvidersAsync(CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ListCatalogProviders, J.IReadOnlyListCatalogProviderDto, cancellationToken);

    public Task ReloadSettingsAsync(CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ReloadSettings, cancellationToken);

    public Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.TestProvider, request, J.ProviderTestRequest, J.ProviderTestResult, cancellationToken);

    public Task RefreshProviderModelsAsync(string providerId, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.RefreshProviderModels, new ProviderIdParams(providerId), J.ProviderIdParams, cancellationToken);

    public Task<CatalogStatusDto> GetCatalogStatusAsync(CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.CatalogStatus, J.CatalogStatusDto, cancellationToken);

    public Task<CatalogUpdateDto> CheckCatalogAsync(CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.CheckCatalog, J.CatalogUpdateDto, cancellationToken);

    public Task<CatalogStatusDto> ApplyCatalogUpdateAsync(CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ApplyCatalogUpdate, J.CatalogStatusDto, cancellationToken);

    public Task DiscardCatalogUpdateAsync(CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.DiscardCatalogUpdate, cancellationToken);

    public Task<IReadOnlyList<RuntimeDto>> ListRuntimesAsync(CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ListRuntimes, J.IReadOnlyListRuntimeDto, cancellationToken);

    public Task<SessionDto> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.CreateSession, request, J.CreateSessionRequest, J.SessionDto, cancellationToken);

    public Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ListSessions, new ListSessionsParams(cwd), J.ListSessionsParams, J.IReadOnlyListSessionSummaryDto, cancellationToken);

    public Task<SessionPageDto> QuerySessionsAsync(string? cwd, int offset, int limit, string? projectId = null, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.QuerySessions, new QuerySessionsParams(cwd, offset, limit, projectId), J.QuerySessionsParams, J.SessionPageDto, cancellationToken);

    public Task<SessionDto> AddSessionDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.AddDirectory, new SessionDirectoryParams(sessionId, path), J.SessionDirectoryParams, J.SessionDto, cancellationToken);

    public Task<SessionDto> RemoveSessionDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.RemoveDirectory, new SessionDirectoryParams(sessionId, path), J.SessionDirectoryParams, J.SessionDto, cancellationToken);

    public Task RenameSessionAsync(string filePath, string title, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.RenameSession, new RenameSessionParams(filePath, title), J.RenameSessionParams, cancellationToken);

    public Task PinSessionAsync(string filePath, bool pinned, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.PinSession, new PinSessionParams(filePath, pinned), J.PinSessionParams, cancellationToken);

    public Task DeleteSessionAsync(string filePath, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.DeleteSession, new SessionFileParams(filePath), J.SessionFileParams, cancellationToken);

    public Task<OpenSessionResult> ForkSessionAsync(string filePath, int? before = null, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ForkSession, new ForkSessionParams(filePath, before), J.ForkSessionParams, J.OpenSessionResult, cancellationToken);

    public Task<OpenSessionResult> OpenSessionAsync(string filePath, int? turnLimit, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.OpenSession, new OpenSessionParams(filePath, turnLimit), J.OpenSessionParams, J.OpenSessionResult, cancellationToken);

    public Task<HistoryPageDto> GetHistoryAsync(string sessionId, int before, int turnLimit, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.SessionHistory, new HistoryPageParams(sessionId, before, turnLimit), J.HistoryPageParams, J.HistoryPageDto, cancellationToken);

    public Task<OpenSessionResult> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.OpenSession, new OpenSessionParams(filePath), J.OpenSessionParams, J.OpenSessionResult, cancellationToken);

    public Task<PromptResult> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.Prompt, request, J.PromptRequest, J.PromptResult, cancellationToken);

    public Task AbortAsync(string sessionId, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.Abort, new SessionIdParams(sessionId), J.SessionIdParams, cancellationToken);

    public Task<PromptResult> RetryAsync(string sessionId, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.Retry, new SessionIdParams(sessionId), J.SessionIdParams, J.PromptResult, cancellationToken);

    public Task CompactAsync(string sessionId, string? instructions, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.Compact, new CompactParams(sessionId, instructions), J.CompactParams, cancellationToken);

    public Task<ContextUsageDto> GetContextUsageAsync(string sessionId, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ContextUsage, new SessionIdParams(sessionId), J.SessionIdParams, J.ContextUsageDto, cancellationToken);

    public Task<SessionOptionsDto> SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.SetModel, new SetModelParams(sessionId, modelId), J.SetModelParams, J.SessionOptionsDto, cancellationToken);

    public Task<SessionOptionsDto> SetOptionsAsync(string sessionId, SessionOptionsDto options, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.SetOptions, new SetOptionsParams(sessionId, options), J.SetOptionsParams, J.SessionOptionsDto, cancellationToken);

    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.CloseSession, new SessionIdParams(sessionId), J.SessionIdParams, cancellationToken);

    public Task<McpServersDto> ListMcpServersAsync(string? cwd, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpList, new McpCwdParams(cwd), J.McpCwdParams, J.McpServersDto, cancellationToken);

    public Task<McpToolsDto> ListMcpToolsAsync(string key, string? cwd, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpTools, new McpServerParams(key, cwd), J.McpServerParams, J.McpToolsDto, cancellationToken);

    public Task<McpLogDto> GetMcpLogAsync(string key, string? cwd, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpLog, new McpServerParams(key, cwd), J.McpServerParams, J.McpLogDto, cancellationToken);

    public Task<McpTestResult> TestMcpServerAsync(McpTestRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpTest, request, J.McpTestRequest, J.McpTestResult, cancellationToken);

    public Task RestartMcpServerAsync(string key, string? cwd, bool login, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpRestart, new McpRestartParams(key, cwd, login), J.McpRestartParams, cancellationToken);

    public Task LogoutMcpServerAsync(string key, string? cwd, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpLogout, new McpServerParams(key, cwd), J.McpServerParams, cancellationToken);

    public Task TrustMcpServerAsync(string key, string cwd, bool trusted, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpTrust, new McpTrustParams(key, cwd, trusted), J.McpTrustParams, cancellationToken);

    public Task<McpCatalogDto> GetMcpCatalogAsync(string? cwd, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpCatalog, new McpCwdParams(cwd), J.McpCwdParams, J.McpCatalogDto, cancellationToken);

    public Task<FileSearchResultDto> SearchFilesAsync(FileSearchRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.SearchFiles, request, J.FileSearchRequest, J.FileSearchResultDto, cancellationToken);

    public Task<RevertFileResult> RevertFileAsync(RevertFileRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.RevertFile, request, J.RevertFileRequest, J.RevertFileResult, cancellationToken);

    public Task<AgentAssetsDto> ListAssetsAsync(string? cwd, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ListAssets, new AssetsParams(cwd), J.AssetsParams, J.AgentAssetsDto, cancellationToken);

    public Task<SubagentOpenResult> OpenSubagentAsync(string sessionId, string toolCallId, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.OpenSubagent, new SubagentParams(sessionId, toolCallId), J.SubagentParams, J.SubagentOpenResult, cancellationToken);

    public Task CloseSubagentAsync(string sessionId, string toolCallId, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.CloseSubagent, new SubagentParams(sessionId, toolCallId), J.SubagentParams, cancellationToken);
}

/// <summary>Host 侧：把 JSON-RPC 方法分发到 <see cref="IAgentHostServer"/> 实现。</summary>
public static class AgentHostServerDispatcher
{
    public static void Register(JsonRpcConnection rpc, IAgentHostServer server)
    {
        var j = RpcJsonContext.Default;
        rpc.AddRequestHandler(RpcMethods.Initialize, j.InitializeRequest, j.HostInfo, server.InitializeAsync);
        rpc.AddRequestHandler(RpcMethods.ListModels, j.IReadOnlyListModelDto, server.ListModelsAsync);
        rpc.AddRequestHandler(RpcMethods.ListProviders, j.ProvidersSnapshotDto, server.ListProvidersAsync);
        rpc.AddRequestHandler(RpcMethods.ListCatalogProviders, j.IReadOnlyListCatalogProviderDto, server.ListCatalogProvidersAsync);
        rpc.AddRequestHandler(RpcMethods.ReloadSettings, server.ReloadSettingsAsync);
        rpc.AddRequestHandler(RpcMethods.TestProvider, j.ProviderTestRequest, j.ProviderTestResult, server.TestProviderAsync);
        rpc.AddRequestHandler(RpcMethods.RefreshProviderModels, j.ProviderIdParams, (p, ct) => server.RefreshProviderModelsAsync(p.ProviderId, ct));
        rpc.AddRequestHandler(RpcMethods.CatalogStatus, j.CatalogStatusDto, server.GetCatalogStatusAsync);
        rpc.AddRequestHandler(RpcMethods.CheckCatalog, j.CatalogUpdateDto, server.CheckCatalogAsync);
        rpc.AddRequestHandler(RpcMethods.ApplyCatalogUpdate, j.CatalogStatusDto, server.ApplyCatalogUpdateAsync);
        rpc.AddRequestHandler(RpcMethods.DiscardCatalogUpdate, server.DiscardCatalogUpdateAsync);
        rpc.AddRequestHandler(RpcMethods.ListRuntimes, j.IReadOnlyListRuntimeDto, server.ListRuntimesAsync);
        rpc.AddRequestHandler(RpcMethods.CreateSession, j.CreateSessionRequest, j.SessionDto, server.CreateSessionAsync);
        rpc.AddRequestHandler(RpcMethods.ListSessions, j.ListSessionsParams, j.IReadOnlyListSessionSummaryDto,
            (p, ct) => server.ListSessionsAsync(p.Cwd, ct));
        rpc.AddRequestHandler(RpcMethods.OpenSession, j.OpenSessionParams, j.OpenSessionResult, (p, ct) => server.OpenSessionAsync(p.FilePath, p.TurnLimit, ct));
        rpc.AddRequestHandler(RpcMethods.SessionHistory, j.HistoryPageParams, j.HistoryPageDto,
            (p, ct) => server.GetHistoryAsync(p.SessionId, p.Before, p.TurnLimit, ct));
        rpc.AddRequestHandler(RpcMethods.QuerySessions, j.QuerySessionsParams, j.SessionPageDto,
            (p, ct) => server.QuerySessionsAsync(p.Cwd, p.Offset, p.Limit, p.ProjectId, ct));
        rpc.AddRequestHandler(RpcMethods.AddDirectory, j.SessionDirectoryParams, j.SessionDto,
            (p, ct) => server.AddSessionDirectoryAsync(p.SessionId, p.Path, ct));
        rpc.AddRequestHandler(RpcMethods.RemoveDirectory, j.SessionDirectoryParams, j.SessionDto,
            (p, ct) => server.RemoveSessionDirectoryAsync(p.SessionId, p.Path, ct));
        rpc.AddRequestHandler(RpcMethods.RenameSession, j.RenameSessionParams, (p, ct) => server.RenameSessionAsync(p.FilePath, p.Title, ct));
        rpc.AddRequestHandler(RpcMethods.PinSession, j.PinSessionParams, (p, ct) => server.PinSessionAsync(p.FilePath, p.Pinned, ct));
        rpc.AddRequestHandler(RpcMethods.DeleteSession, j.SessionFileParams, (p, ct) => server.DeleteSessionAsync(p.FilePath, ct));
        rpc.AddRequestHandler(RpcMethods.ForkSession, j.ForkSessionParams, j.OpenSessionResult, (p, ct) => server.ForkSessionAsync(p.FilePath, p.Before, ct));
        rpc.AddRequestHandler(RpcMethods.Prompt, j.PromptRequest, j.PromptResult, server.PromptAsync);
        rpc.AddRequestHandler(RpcMethods.Abort, j.SessionIdParams, (p, ct) => server.AbortAsync(p.SessionId, ct));
        rpc.AddRequestHandler(RpcMethods.Retry, j.SessionIdParams, j.PromptResult, (p, ct) => server.RetryAsync(p.SessionId, ct));
        rpc.AddRequestHandler(RpcMethods.Compact, j.CompactParams, (p, ct) => server.CompactAsync(p.SessionId, p.Instructions, ct));
        rpc.AddRequestHandler(RpcMethods.ContextUsage, j.SessionIdParams, j.ContextUsageDto, (p, ct) => server.GetContextUsageAsync(p.SessionId, ct));
        rpc.AddRequestHandler(RpcMethods.SetModel, j.SetModelParams, j.SessionOptionsDto, (p, ct) => server.SetModelAsync(p.SessionId, p.ModelId, ct));
        rpc.AddRequestHandler(RpcMethods.SetOptions, j.SetOptionsParams, j.SessionOptionsDto, (p, ct) => server.SetOptionsAsync(p.SessionId, p.Options, ct));
        rpc.AddRequestHandler(RpcMethods.CloseSession, j.SessionIdParams, (p, ct) => server.CloseSessionAsync(p.SessionId, ct));
        rpc.AddRequestHandler(RpcMethods.McpList, j.McpCwdParams, j.McpServersDto, (p, ct) => server.ListMcpServersAsync(p.Cwd, ct));
        rpc.AddRequestHandler(RpcMethods.McpTools, j.McpServerParams, j.McpToolsDto, (p, ct) => server.ListMcpToolsAsync(p.Key, p.Cwd, ct));
        rpc.AddRequestHandler(RpcMethods.McpLog, j.McpServerParams, j.McpLogDto, (p, ct) => server.GetMcpLogAsync(p.Key, p.Cwd, ct));
        rpc.AddRequestHandler(RpcMethods.McpTest, j.McpTestRequest, j.McpTestResult, server.TestMcpServerAsync);
        rpc.AddRequestHandler(RpcMethods.McpRestart, j.McpRestartParams, (p, ct) => server.RestartMcpServerAsync(p.Key, p.Cwd, p.Login, ct));
        rpc.AddRequestHandler(RpcMethods.McpLogout, j.McpServerParams, (p, ct) => server.LogoutMcpServerAsync(p.Key, p.Cwd, ct));
        rpc.AddRequestHandler(RpcMethods.McpTrust, j.McpTrustParams, (p, ct) => server.TrustMcpServerAsync(p.Key, p.Cwd, p.Trusted, ct));
        rpc.AddRequestHandler(RpcMethods.McpCatalog, j.McpCwdParams, j.McpCatalogDto, (p, ct) => server.GetMcpCatalogAsync(p.Cwd, ct));
        rpc.AddRequestHandler(RpcMethods.SearchFiles, j.FileSearchRequest, j.FileSearchResultDto, server.SearchFilesAsync);
        rpc.AddRequestHandler(RpcMethods.RevertFile, j.RevertFileRequest, j.RevertFileResult, server.RevertFileAsync);
        rpc.AddRequestHandler(RpcMethods.ListAssets, j.AssetsParams, j.AgentAssetsDto, (p, ct) => server.ListAssetsAsync(p.Cwd, ct));
        rpc.AddRequestHandler(RpcMethods.OpenSubagent, j.SubagentParams, j.SubagentOpenResult, (p, ct) => server.OpenSubagentAsync(p.SessionId, p.ToolCallId, ct));
        rpc.AddRequestHandler(RpcMethods.CloseSubagent, j.SubagentParams, (p, ct) => server.CloseSubagentAsync(p.SessionId, p.ToolCallId, ct));
    }
}

/// <summary>Host 侧使用的 <see cref="IAgentHostClient"/> 代理（推送通知、反向请求审批）。</summary>
public sealed class AgentHostClientProxy(JsonRpcConnection rpc) : IAgentHostClient
{
    private static RpcJsonContext J => RpcJsonContext.Default;

    public void OnSessionEvent(SessionEventNotification notification) =>
        rpc.Notify(RpcMethods.SessionEvent, notification, J.SessionEventNotification);

    public void OnSubagentEvent(SubagentEventNotification notification) =>
        rpc.Notify(RpcMethods.SubagentEvent, notification, J.SubagentEventNotification);

    public void OnModelsChanged(ModelsChangedNotification notification) =>
        rpc.Notify(RpcMethods.ModelsChanged, notification, J.ModelsChangedNotification);

    public Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ApproveTool, request, J.ToolApprovalRequest, J.ToolApprovalResponse, cancellationToken);

    public void OnMcpChanged(McpChangedNotification notification) =>
        rpc.Notify(RpcMethods.McpChanged, notification, J.McpChangedNotification);

    public Task<McpElicitResponse> RequestMcpElicitationAsync(McpElicitRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpElicit, request, J.McpElicitRequest, J.McpElicitResponse, cancellationToken);

    public Task<McpOpenUrlResponse> OpenMcpUrlAsync(McpOpenUrlRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.McpOpenUrl, request, J.McpOpenUrlRequest, J.McpOpenUrlResponse, cancellationToken);

    public void OnAssetsChanged(AssetsChangedNotification notification) =>
        rpc.Notify(RpcMethods.AssetsChanged, notification, J.AssetsChangedNotification);

    public Task<UserInputResponse> RequestUserInputAsync(UserInputRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.RequestInput, request, J.UserInputRequest, J.UserInputResponse, cancellationToken);

    public Task<PlanReviewResponse> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken = default) =>
        rpc.InvokeAsync(RpcMethods.ReviewPlan, request, J.PlanReviewRequest, J.PlanReviewResponse, cancellationToken);
}

/// <summary>UI 侧：把 Host 发来的通知与请求分发到 <see cref="IAgentHostClient"/> 实现。</summary>
public static class AgentHostClientDispatcher
{
    public static void Register(JsonRpcConnection rpc, IAgentHostClient client)
    {
        var j = RpcJsonContext.Default;
        rpc.AddNotificationHandler(RpcMethods.SessionEvent, j.SessionEventNotification, client.OnSessionEvent);
        rpc.AddNotificationHandler(RpcMethods.SubagentEvent, j.SubagentEventNotification, client.OnSubagentEvent);
        rpc.AddNotificationHandler(RpcMethods.ModelsChanged, j.ModelsChangedNotification, client.OnModelsChanged);
        rpc.AddRequestHandler(RpcMethods.ApproveTool, j.ToolApprovalRequest, j.ToolApprovalResponse, client.RequestToolApprovalAsync);
        rpc.AddNotificationHandler(RpcMethods.McpChanged, j.McpChangedNotification, client.OnMcpChanged);
        rpc.AddRequestHandler(RpcMethods.McpElicit, j.McpElicitRequest, j.McpElicitResponse, client.RequestMcpElicitationAsync);
        rpc.AddRequestHandler(RpcMethods.McpOpenUrl, j.McpOpenUrlRequest, j.McpOpenUrlResponse, client.OpenMcpUrlAsync);
        rpc.AddNotificationHandler(RpcMethods.AssetsChanged, j.AssetsChangedNotification, client.OnAssetsChanged);
        rpc.AddRequestHandler(RpcMethods.RequestInput, j.UserInputRequest, j.UserInputResponse, client.RequestUserInputAsync);
        rpc.AddRequestHandler(RpcMethods.ReviewPlan, j.PlanReviewRequest, j.PlanReviewResponse, client.ReviewPlanAsync);
    }
}
