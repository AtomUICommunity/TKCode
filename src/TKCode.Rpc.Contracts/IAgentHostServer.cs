namespace TKCode.Rpc;

/// <summary>
/// Agent Host 进程对外暴露的方法（UI → Host）。JSON-RPC 2.0，经 stdio 传输。
/// 方法名见 <see cref="RpcMethods"/>；线路实现见 <see cref="AgentHostServerProxy"/> / <see cref="AgentHostServerDispatcher"/>。
/// 新增方法时：接口 + RpcMethods + RpcJsonContext（DTO）+ 代理与分发器各一行。
/// </summary>
public interface IAgentHostServer
{
    Task<HostInfo> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelDto>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>设置页：已配置 / 检测到 Key / 推荐的厂商，以及它们的模型与 Key 状态。</summary>
    Task<ProvidersSnapshotDto> ListProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>内置目录中的全部厂商（“添加厂商”）。</summary>
    Task<IReadOnlyList<CatalogProviderDto>> ListCatalogProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>立即重新读取 settings.json（文件监听之外的兜底，例如界面刚写完文件）。</summary>
    Task ReloadSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>测试厂商连接（优先 GET /models，不支持时发一次最小请求）。</summary>
    Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken = default);

    /// <summary>重新通过 GET /models 获取自定义 / 本地厂商的模型列表，完成后推送 models/changed。</summary>
    Task RefreshProviderModelsAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>模型目录的来源与同步状态。</summary>
    Task<CatalogStatusDto> GetCatalogStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>从 models.dev 下载最新目录并与当前目录比较；结果暂存，不会自动应用。</summary>
    Task<CatalogUpdateDto> CheckCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>应用最近一次检查得到的更新，完成后推送 models/changed。</summary>
    Task<CatalogStatusDto> ApplyCatalogUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>放弃暂存的更新。</summary>
    Task DiscardCatalogUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>外部 Agent 运行时（Claude Agent / Codex / ACP Agent）的发现与探测结果。</summary>
    Task<IReadOnlyList<RuntimeDto>> ListRuntimesAsync(CancellationToken cancellationToken = default);

    Task<SessionDto> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>按最近修改时间倒序列出会话；<paramref name="cwd"/> 为 null 时列出全部项目。</summary>
    Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken = default);

    /// <summary>恢复会话文件；已在 Host 中打开（例如仍在运行）时直接返回该会话，不会重复加载。</summary>
    Task<OpenSessionResult> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>打开会话，只返回最近 <paramref name="turnLimit"/> 轮历史（更早的用 <see cref="GetHistoryAsync"/> 分页获取）。</summary>
    Task<OpenSessionResult> OpenSessionAsync(string filePath, int? turnLimit, CancellationToken cancellationToken = default);

    /// <summary>已打开会话中 <paramref name="before"/> 之前的若干轮历史。</summary>
    Task<HistoryPageDto> GetHistoryAsync(string sessionId, int before, int turnLimit, CancellationToken cancellationToken = default);

    /// <summary>分页列出会话（基于会话索引，只重新解析修改过的文件）。<paramref name="projectId"/> 不为 null 时只列出属于该项目的会话。</summary>
    Task<SessionPageDto> QuerySessionsAsync(string? cwd, int offset, int limit, string? projectId = null, CancellationToken cancellationToken = default);

    /// <summary>重命名；空串恢复为第一条用户消息。</summary>
    Task RenameSessionAsync(string filePath, string title, CancellationToken cancellationToken = default);

    Task PinSessionAsync(string filePath, bool pinned, CancellationToken cancellationToken = default);

    /// <summary>永久删除会话文件；会话正在 Host 中打开时先中止并关闭。</summary>
    Task DeleteSessionAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>只给本会话追加一个文件夹（不写进项目，docs/design/projects.md §6.4）；下一轮开始前生效。返回更新后的会话。</summary>
    Task<SessionDto> AddSessionDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>移除只属于本会话的文件夹；下一轮开始前生效。</summary>
    Task<SessionDto> RemoveSessionDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>在新会话中继续：复制当前分支的历史与参数到一个新会话并打开（仅内置 Agent）。</summary>
    Task<OpenSessionResult> ForkSessionAsync(string filePath, CancellationToken cancellationToken = default);

    Task<PromptResult> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default);

    Task AbortAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>压缩上下文（/compact，仅内置 Agent）：空闲时立即开始，运行中则在下一次模型请求前执行。进度以会话事件推送。</summary>
    Task CompactAsync(string sessionId, string? instructions, CancellationToken cancellationToken = default);

    /// <summary>上下文占用：当前 token 数、按来源分类（仅内置 Agent）与整个会话的提示缓存命中率。</summary>
    Task<ContextUsageDto> GetContextUsageAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>切换模型，返回按新模型修正后的会话参数。</summary>
    Task<SessionOptionsDto> SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken = default);

    /// <summary>修改思考档位 / fast mode / 上下文预算，返回生效后的完整参数（不支持的取值会被就近修正）。</summary>
    Task<SessionOptionsDto> SetOptionsAsync(string sessionId, SessionOptionsDto options, CancellationToken cancellationToken = default);

    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>MCP：全局与 <paramref name="cwd"/> 项目中的服务器及其状态。</summary>
    Task<McpServersDto> ListMcpServersAsync(string? cwd, CancellationToken cancellationToken = default);

    /// <summary>MCP：服务器的工具（没有连接时临时连接，最多等待 startupTimeout）。</summary>
    Task<McpToolsDto> ListMcpToolsAsync(string key, string? cwd, CancellationToken cancellationToken = default);

    /// <summary>MCP：stderr 与连接日志。</summary>
    Task<McpLogDto> GetMcpLogAsync(string key, string? cwd, CancellationToken cancellationToken = default);

    /// <summary>MCP：用未保存的配置临时连接并执行 tools/list，不写文件、不进入连接池。</summary>
    Task<McpTestResult> TestMcpServerAsync(McpTestRequest request, CancellationToken cancellationToken = default);

    /// <summary>MCP：断开并重新连接；<paramref name="login"/> 为 true 时允许打开浏览器进行 OAuth 登录。</summary>
    Task RestartMcpServerAsync(string key, string? cwd, bool login, CancellationToken cancellationToken = default);

    /// <summary>MCP：删除该服务器保存的 OAuth 令牌并断开。</summary>
    Task LogoutMcpServerAsync(string key, string? cwd, CancellationToken cancellationToken = default);

    /// <summary>MCP：信任或忽略项目 .mcp.json 中的服务器。</summary>
    Task TrustMcpServerAsync(string key, string cwd, bool trusted, CancellationToken cancellationToken = default);

    /// <summary>MCP：项目可用服务器的资源与提示词（Composer 的 @ 与 /）。</summary>
    Task<McpCatalogDto> GetMcpCatalogAsync(string? cwd, CancellationToken cancellationToken = default);

    /// <summary>输入框 @ 文件：在目录的文件索引中模糊搜索。</summary>
    Task<FileSearchResultDto> SearchFilesAsync(FileSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 打开子 Agent 的转录：返回已结束消息的历史与尚在进行中的事件（Replay），此后经 <see cref="IAgentHostClient.OnSubagentEvent"/>
    /// 推送该子 Agent 的事件，直到 <see cref="CloseSubagentAsync"/>（docs/design/subagent-view.md §5.2）。
    /// </summary>
    Task<SubagentOpenResult> OpenSubagentAsync(string sessionId, string toolCallId, CancellationToken cancellationToken = default);

    /// <summary>停止推送子 Agent 的事件。</summary>
    Task CloseSubagentAsync(string sessionId, string toolCallId, CancellationToken cancellationToken = default);

    /// <summary>撤销 Agent 的文件改动：文件仍与改动后的内容一致时才恢复。</summary>
    Task<RevertFileResult> RevertFileAsync(RevertFileRequest request, CancellationToken cancellationToken = default);

    /// <summary>cwd 所在项目生效的 command / skill / role（全局 ~/.tkcode 与项目 .agents 合并后）。</summary>
    Task<AgentAssetsDto> ListAssetsAsync(string? cwd, CancellationToken cancellationToken = default);
}

/// <summary>
/// UI 进程实现、供 Host 调用的方法（Host → UI）：会话事件推送与工具审批。
/// </summary>
public interface IAgentHostClient
{
    /// <summary>会话事件通知（JSON-RPC notification，无返回）。</summary>
    void OnSessionEvent(SessionEventNotification notification);

    /// <summary>已打开的子 Agent 转录的事件（notification）。</summary>
    void OnSubagentEvent(SubagentEventNotification notification)
    {
    }

    /// <summary>settings.json 变化后推送最新模型列表（notification）。</summary>
    void OnModelsChanged(ModelsChangedNotification notification);

    /// <summary>需要审批的工具调用（按权限档位与工作区边界判定）执行前向用户请求审批。</summary>
    Task<ToolApprovalResponse> RequestToolApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default);

    /// <summary>MCP 服务器状态或配置变化（notification）。</summary>
    void OnMcpChanged(McpChangedNotification notification);

    /// <summary>MCP 服务器请求用户输入；用户关闭或没有界面时返回 cancel。</summary>
    Task<McpElicitResponse> RequestMcpElicitationAsync(McpElicitRequest request, CancellationToken cancellationToken = default);

    /// <summary>用系统浏览器打开 MCP OAuth 授权页。</summary>
    Task<McpOpenUrlResponse> OpenMcpUrlAsync(McpOpenUrlRequest request, CancellationToken cancellationToken = default);

    /// <summary>command / skill / role 的定义文件变化（notification）。</summary>
    void OnAssetsChanged(AssetsChangedNotification notification);

    /// <summary>
    /// Agent 向用户提问（内置 ask_user、Claude AskUserQuestion、Codex requestUserInput；docs/design/ask-user.md §6）。
    /// 不受权限档位影响，从不自动回答；Host 取消请求时 UI 必须收起问答面板。
    /// </summary>
    Task<UserInputResponse> RequestUserInputAsync(UserInputRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 计划审批（内置 exit_plan_mode、Claude ExitPlanMode；docs/design/plan-mode.md §13.1）。
    /// 不受权限档位影响，从不自动批准；Host 取消请求时 UI 必须收起审批面板。
    /// </summary>
    Task<PlanReviewResponse> ReviewPlanAsync(PlanReviewRequest request, CancellationToken cancellationToken = default);
}

public static class RpcProtocol
{
    public const int Version = 25;
}
