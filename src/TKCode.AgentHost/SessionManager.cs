using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TKCode.Agent;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Assets;
using TKCode.Agent.Coding.Memory;
using TKCode.Agent.Coding.Sessions;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Plan;
using TKCode.Agent.Coding.Tools.Questions;
using TKCode.Agent.Plans;
using TKCode.Agent.Questions;
using TKCode.Agent.Coding.Tools.Web;
using TKCode.Agent.Coding.Workspace;
using TKCode.AgentHost.Peers;
using TKCode.Config;
using TKCode.Mcp;
using TKCode.Ai;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;
using TKCode.Rpc;
using TKCode.Runtimes;

namespace TKCode.AgentHost;

/// <summary>
/// 管理 Host 内的多个会话，把 Agent 事件翻译成只含增量的 RPC 事件推送给 UI，
/// 并为非只读工具向 UI 请求审批。
/// 空闲的会话会被释放（只留下文件路径与不落盘的状态），再次访问时从会话文件透明地重新打开。
/// </summary>
public sealed class SessionManager : IAsyncDisposable, IUserInputService, IPlanModeService
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly ConcurrentDictionary<string, Evicted> _evicted = new();
    private readonly Lock _gate = new();
    private readonly Timer _sweeper;
    private int _collectPending;
    private readonly WireApiRegistry apis;
    private readonly ModelCatalog catalog;
    private readonly RuntimeRegistry runtimes;
    private readonly ILogger<SessionManager> logger;
    private readonly SessionTitleGenerator _titles;
    private readonly WebFetcher _webFetcher = new();
    private (string? Signature, IWebSearchProvider? Provider) _webSearch;
    private IAgentHostClient? _client;

    public SessionManager(WireApiRegistry apis, ModelCatalog catalog, RuntimeRegistry runtimes, PeerDirectory peers, McpManager mcp,
        ILogger<SessionManager> logger, AgentAssetStore? assets = null)
    {
        Mcp = mcp;
        _assets = assets;
        // OAuth 的浏览器与 elicitation 弹窗经 UI 完成（没有 UI 时：浏览器用系统默认程序打开，elicitation 一律 cancel）
        mcp.Host.OAuth ??= new McpOAuth(new McpAuthStore(), OpenMcpUrlAsync);
        mcp.Host.Elicit ??= ElicitAsync;
        Bridge = new McpBridgeServer(this, mcp, logger);
        this.apis = apis;
        this.catalog = catalog;
        this.runtimes = runtimes;
        this.logger = logger;
        _titles = new SessionTitleGenerator(apis, runtimes, catalog.GetApiKey, CacheTtl);
        Subagents = new SubagentHub(() => _client, logger);
        Coordinator = new AgentCoordinator(this, peers, logger);
        _sweeper = new Timer(_ => _ = SweepAsync(), null, SweepInterval, SweepInterval);
    }

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>不在运行的会话空闲多久后释放。</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>MCP 连接池。</summary>
    public McpManager Mcp { get; }

    private AgentAssetStore? _assets;

    /// <summary>command / skill / role（全局与各项目 .agents）。测试中没有注入时按需创建。</summary>
    public AgentAssetStore Assets => _assets ??= new AgentAssetStore();

    /// <summary>外部运行时的 MCP 桥接。</summary>
    public McpBridgeServer Bridge { get; }

    /// <summary>外部运行时的会话：注入 MCP 桥接，以及每轮重新计算的附加目录（docs/design/projects.md §6.8）。</summary>
    private ExternalAgent WithBridge(ExternalAgent agent, SessionStore store)
    {
        agent.McpBridge = () => Bridge.LaunchFor(store.SessionId, store.Cwd);
        agent.AdditionalDirectories = () => DirectoriesFor(store.Cwd, store.ProjectId, store.SessionDirectories).All;
        return agent;
    }

    /// <summary>子 Agent 与跨会话消息。</summary>
    public AgentCoordinator Coordinator { get; }

    /// <summary>子 Agent 的转录（三种运行时共用）。</summary>
    public SubagentHub Subagents { get; }

    public WireApiRegistry Apis => apis;

    public string? GetApiKey(string provider) => catalog.GetApiKey(provider);

    /// <summary>子 Agent 可用的模型：只接受内置协议的模型（外部运行时的模型不能作为子 Agent）。</summary>
    public ModelInfo? FindBuiltinModel(string? reference) =>
        catalog.Find(reference) is { } model && runtimes.Find(model.Provider) is null ? model : null;

    /// <summary>子 Agent 与标题共用的快速模型；未配置或不可用时跟随调用方的会话模型。</summary>
    public ModelInfo FastModelFor(ModelInfo sessionModel) => FindBuiltinModel(catalog.FastModelRef) ?? sessionModel;

    /// <summary>内置工具使用的宿主能力：网页抓取、按 settings 配置的网页搜索、子 Agent 协调。</summary>
    public ToolServices ServicesFor(string cwd) => new()
    {
        WebFetcher = _webFetcher,
        WebSearch = CurrentWebSearch(),
        Agents = Coordinator,
        UserInput = this,
        PlanMode = this,
        // MCP 工具与 skills（docs/design/composer.md §10.1）
        DynamicTools = context => new CompositeToolSource([
            Mcp.CreateSource(context),
            new SkillToolSource(Assets, context, () => SettingsFile.Load().Settings?.Skills?.AutoDiscover != false),
        ]),
    };

    private IWebSearchProvider? CurrentWebSearch()
    {
        var settings = SettingsFile.Load().Settings?.WebSearch;
        if (settings?.Provider is not { Length: > 0 } provider)
        {
            return null;
        }
        var key = Secrets.Resolve(settings.ApiKey).Value;
        var signature = $"{provider}|{key}|{settings.BaseUrl}";
        lock (_sessions)
        {
            if (_webSearch.Signature == signature)
            {
                return _webSearch.Provider;
            }
            IWebSearchProvider? created = null;
            try
            {
                created = WebSearchProviders.Create(provider, key, settings.BaseUrl);
            }
            catch (WebSearchException ex)
            {
                logger.LogWarning("web_search is disabled: {Reason}", ex.Message);
            }
            _webSearch = (signature, created);
            return created;
        }
    }

    /// <summary>项目表（docs/design/projects.md §5）：Desktop 写，Host 按需读取。</summary>
    public ProjectCatalog Projects { get; } = new();

    /// <summary>
    /// 会话工作区的附加目录（docs/design/projects.md §6.2）：所属项目的其他文件夹（每次重读项目表，跳过不存在的）、
    /// 只给本会话追加的文件夹、settings 的全局目录；以及其中只属于本会话的那些。
    /// </summary>
    public (IReadOnlyList<string> All, IReadOnlyList<string> SessionOnly) DirectoriesFor(string cwd, string? projectId,
        IReadOnlyList<string> sessionDirectories)
    {
        var all = new List<string>();
        var project = Projects.Resolve(projectId, cwd);
        if (project is not null)
        {
            all.AddRange(project.Folders.Where(f => !PathRules.SamePath(f, cwd) && Directory.Exists(f)));
        }
        all.AddRange(sessionDirectories);
        all.AddRange(catalog.AdditionalDirectories);
        IReadOnlyList<string> sessionOnly = project is null ? sessionDirectories : sessionDirectories.Where(d => !project.HasFolder(d)).ToArray();
        return (all, sessionOnly);
    }

    private BuiltinOptions OptionsFor(SessionStore store) => OptionsFor(store.Cwd, store.ProjectId, store.SessionDirectories);

    private BuiltinOptions OptionsFor(string cwd, string? projectId, IReadOnlyList<string> sessionDirectories)
    {
        var settings = SettingsFile.Load().Settings;
        var workspace = new WorkspaceRoots(cwd);
        var (all, sessionOnly) = DirectoriesFor(cwd, projectId, sessionDirectories);
        workspace.Sync(all, sessionOnly);
        return new BuiltinOptions
        {
            Workspace = workspace,
            // 每批用户输入之前重新计算：项目的文件夹与本会话追加的文件夹可能已经变化（§6.3）
            AdditionalDirectories = s => DirectoriesFor(s.Cwd, s.ProjectId, s.SessionDirectories),
            Services = ServicesFor(cwd),
            AutoCompact = settings?.AutoCompact ?? true,
            CacheTtl = CacheTtl,
            ShellProfile = ShellProfile,
            MemoryDirectory = settings?.AutoMemory ?? true ? ProjectMemory.DirectoryFor(cwd) : null,
        };
    }

    /// <summary>全局缓存有效期：读 <see cref="ModelCatalog"/> 最近一次成功解析的设置，修改 settings.json 后对进行中的会话也立即生效。</summary>
    public Func<PromptCacheTtl> CacheTtl => () => catalog.CacheTtl;

    /// <summary>bash 工具的 shell：同样读最近一次成功解析的设置，修改后对进行中的会话也立即生效。</summary>
    public Func<string?> ShellProfile => () => catalog.ShellProfile;

    /// <summary>会话在 send_message / list_agents 中的名字：项目目录名加会话 id 的末 4 位。</summary>
    public string NameOf(AgentSession session) =>
        $"{AgentCoordinator.Slug(Path.GetFileName(session.Cwd.TrimEnd(Path.DirectorySeparatorChar)))}-{session.Id[^4..]}";

    /// <summary>打开的顶层会话（不含子 Agent）。</summary>
    public IReadOnlyList<AgentSession> TopLevel() => _sessions.Values.Select(e => e.Session).ToArray();

    /// <summary>已加载的会话；已释放的会话不会因此重新打开。</summary>
    public AgentSession? TryGet(string sessionId) => _sessions.TryGetValue(sessionId, out var entry) ? Touch(entry).Session : null;

    public void AttachClient(IAgentHostClient client)
    {
        _client = client;
        catalog.Changed += OnCatalogChanged;
        runtimes.Changed += OnRuntimesChanged;
        Mcp.Changed += OnMcpChanged;
        Assets.Changed += root =>
        {
            try
            {
                _client?.OnAssetsChanged(new AssetsChangedNotification(root));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to send assets/changed");
            }
        };
    }

    private async Task<bool> OpenMcpUrlAsync(string server, string url, CancellationToken ct)
    {
        if (_client is { } client)
        {
            try
            {
                return (await client.OpenMcpUrlAsync(new McpOpenUrlRequest(server, url), ct)).Opened;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "mcp/openUrl failed; falling back to the system browser");
            }
        }
        return McpOAuth.OpenWithSystemBrowser(url);
    }

    /// <summary>MCP elicitation → UI（规格 §5.1）；没有 UI 或请求失败时 cancel。</summary>
    private async ValueTask<ModelContextProtocol.Protocol.ElicitResult> ElicitAsync(McpElicitation e, CancellationToken ct)
    {
        var cancel = new ModelContextProtocol.Protocol.ElicitResult { Action = "cancel" };
        if (_client is not { } client)
        {
            return cancel;
        }
        var json = System.Text.Json.JsonSerializer.SerializeToNode(e.Request,
            ModelContextProtocol.McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ModelContextProtocol.Protocol.ElicitRequestParams)));
        var request = new McpElicitRequest(e.SessionId, e.ServerKey, e.ServerTitle, string.IsNullOrEmpty(e.Request.Mode) ? "form" : e.Request.Mode,
            e.Request.Message, json?["requestedSchema"]?.ToJsonString(), e.Request.Url);
        McpElicitResponse response;
        try
        {
            response = await client.RequestMcpElicitationAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "mcp/elicit failed");
            return cancel;
        }
        var result = new ModelContextProtocol.Protocol.ElicitResult { Action = response.Action };
        if (response.Action == "accept" && response.ContentJson is { Length: > 0 } content)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            result.Content = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : null;
        }
        return result;
    }

    private int _mcpNotifyPending;

    /// <summary>MCP 状态变化合并成一次通知（200ms 内的变化只推送一次），UI 收到后重新拉取列表。</summary>
    private void OnMcpChanged()
    {
        if (Interlocked.Exchange(ref _mcpNotifyPending, 1) == 1)
        {
            return;
        }
        _ = Task.Delay(200).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _mcpNotifyPending, 0);
            try
            {
                _client?.OnMcpChanged(new McpChangedNotification(null));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to send mcp/changed");
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// settings.json 变化后：已打开会话中仍存在的模型换成新配置（Key / Base URL / 元数据），然后通知 UI。
    /// 已不存在的模型保持原样，直到用户切换模型。
    /// </summary>
    private void OnCatalogChanged()
    {
        foreach (var entry in _sessions.Values)
        {
            var current = entry.Session.Agent.Model;
            if (catalog.Find(current.Provider, current.Id) is { } updated && !ReferenceEquals(updated, current))
            {
                entry.Session.Agent.Model = updated;
            }
        }
        NotifyModelsChanged();
    }

    /// <summary>运行时报告的新能力也用于已打开的同模型会话，避免 options/set 仍按首次探测的旧元数据校验。</summary>
    private void OnRuntimesChanged()
    {
        foreach (var entry in _sessions.Values)
        {
            if (entry.Session.Agent is not ExternalAgent external)
            {
                continue;
            }
            var current = external.Model;
            if (runtimes.FindModel(external.RuntimeId, current.Id) is { } updated && !ReferenceEquals(updated.Model, current))
            {
                // 只替换同运行时、同 ID 的不可变元数据，不切换原生会话或重置用户参数。
                // 活动回合已持有 TurnOptions / TurnWriter 的模型快照；新能力供后续参数校验与回合使用。
                external.Model = updated.Model;
            }
        }
        // 必须先同步会话再推送：UI 收到通知后可能立即提交新增的思考档位。
        NotifyModelsChanged();
    }

    /// <summary>模型列表变化（settings.json、目录同步、外部运行时探测完成）后推送给 UI。</summary>
    private void NotifyModelsChanged()
    {
        try
        {
            _client?.OnModelsChanged(new ModelsChangedNotification(AgentHostServer.AllModelDtos(catalog, runtimes), catalog.Errors));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify models changed");
        }
    }

    public AgentSession Create(string cwd, ModelInfo model) => CreateBuiltin(SessionStore.Create(cwd), model);

    /// <summary>由外部运行时承载的新会话；CLI 进程在第一次提问时启动。</summary>
    public AgentSession CreateExternal(string cwd, IAgentRuntime runtime, ModelInfo model) => CreateExternal(SessionStore.Create(cwd), runtime, model);

    /// <summary>
    /// 新会话（docs/design/projects.md §5.3、§6.4）：会话头记录所属项目，<paramref name="directories"/> 是只给本会话追加的文件夹。
    /// <paramref name="runtime"/> 为 null 时由内置 Agent 承载。
    /// </summary>
    public async Task<AgentSession> CreateAsync(string cwd, ModelInfo model, IAgentRuntime? runtime, string? projectId,
        IReadOnlyList<string>? directories, CancellationToken cancellationToken = default)
    {
        var store = SessionStore.Create(cwd, projectId: projectId);
        var normalized = NormalizeDirectories(cwd, directories ?? []);
        if (normalized.Count > 0)
        {
            await store.AppendDirectoriesAsync(normalized, cancellationToken);
        }
        return runtime is null ? CreateBuiltin(store, model) : CreateExternal(store, runtime, model);
    }

    private AgentSession CreateBuiltin(SessionStore store, ModelInfo model) =>
        Register(AgentSession.CreateBuiltin(apis, model, store, catalog.GetApiKey, OptionsFor(store)));

    private AgentSession CreateExternal(SessionStore store, IAgentRuntime runtime, ModelInfo model) =>
        Register(AgentSession.Rebind(store, WithBridge(new ExternalAgent(runtime, model, store.Cwd), store)));

    /// <summary>只给本会话追加一个文件夹（§6.4）：支持 ~ 与相对路径（基于 cwd），必须是存在的目录；已在列表中或等于 cwd 时不变。</summary>
    public async Task<AgentSession> AddSessionDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        var session = Get(sessionId);
        var full = PathRules.Normalize(PathUtil.Resolve(session.Cwd, path));
        if (!Directory.Exists(full))
        {
            throw new InvalidOperationException($"'{path}' is not a directory.");
        }
        var current = session.Store.SessionDirectories;
        if (!PathRules.SamePath(full, session.Cwd) && !current.Any(d => PathRules.SamePath(d, full)))
        {
            await session.Store.AppendDirectoriesAsync([.. current, full], cancellationToken);
        }
        return session;
    }

    public async Task<AgentSession> RemoveSessionDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        var session = Get(sessionId);
        var current = session.Store.SessionDirectories;
        if (current.Any(d => PathRules.SamePath(d, path)))
        {
            await session.Store.AppendDirectoriesAsync(current.Where(d => !PathRules.SamePath(d, path)).ToArray(), cancellationToken);
        }
        return session;
    }

    private static IReadOnlyList<string> NormalizeDirectories(string cwd, IReadOnlyList<string> directories)
    {
        var list = new List<string>();
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }
            var full = PathRules.Normalize(PathUtil.Resolve(cwd, directory));
            if (!PathRules.SamePath(full, cwd) && !list.Any(d => PathRules.SamePath(d, full)))
            {
                list.Add(full);
            }
        }
        return list;
    }

    /// <summary>打开会话文件（已打开时直接返回）。会话文件只解析一次。</summary>
    public AgentSession OpenFile(string filePath)
    {
        lock (_gate)
        {
            if (_sessions.Values.FirstOrDefault(e => SameFile(e.Session, filePath)) is { } open)
            {
                return Touch(open).Session;
            }
            var snapshot = SessionStore.Read(filePath);
            _evicted.TryRemove(snapshot.SessionId, out var evicted);
            AgentSession session;
            if (runtimes.Find(RuntimeOf(snapshot)) is { } runtime)
            {
                // 外部运行时的会话只能在同一运行时中继续；CLI 尚未探测完成或已卸载时用占位模型，提问时再报错。
                var model = runtimes.FindModel(runtime.Id, snapshot.ModelId)?.Model
                            ?? runtime.Descriptor.Models.FirstOrDefault()
                            ?? RuntimeModels.Placeholder(runtime, snapshot.ModelId);
                // 第一次提问时用记录的原生会话 id 恢复
                session = Register(AgentSession.Open(filePath, (store, s) =>
                    WithBridge(new ExternalAgent(runtime, model, store.Cwd, s.NativeSessionId), store), snapshot));
            }
            else
            {
                // 会话记录的模型不在当前目录中（例如未配置 API Key）时退回第一个可用模型。
                var model = catalog.Find(snapshot.ModelProvider, snapshot.ModelId) ?? catalog.Find(catalog.DefaultModelRef) ?? catalog.Models[0];
                var options = OptionsFor(snapshot.Cwd, snapshot.ProjectId, snapshot.SessionDirectories);
                if (evicted is not null)
                {
                    options = options with { Approvals = evicted.Approvals };
                }
                session = Register(AgentSession.Open(apis, model, filePath, catalog.GetApiKey, options, snapshot));
            }
            // 解析会话文件留下大量临时对象，下一次清理时回收
            Interlocked.Exchange(ref _collectPending, 1);
            SessionOptions.Initialize(session);
            // 档位按记录恢复，包括完全授权；只是空闲释放过的会话用释放前的档位。
            var permission = evicted?.PermissionMode ?? snapshot.PermissionMode ?? PermissionMode.Ask;
            // role 按记录恢复；自定义 role 已被删除时回到 agent
            SessionOptions.Apply(session, new SessionOptionsDto(snapshot.ThinkingLevel?.ToString(), snapshot.FastMode, snapshot.ContextWindow,
                PermissionMode: SessionOptions.ToWire(permission), Role: snapshot.Role), Assets.Get(session.Cwd));
            return session;
        }
    }

    /// <summary>会话所属的外部运行时：优先取记录的原生会话，其次看模型厂商是否为运行时 id。</summary>
    public string? RuntimeOf(SessionSnapshot snapshot) =>
        snapshot.Runtime ?? (runtimes.Find(snapshot.ModelProvider) is { } runtime ? runtime.Id : null);

    /// <summary>
    /// 换承载会话的运行时（<paramref name="runtime"/> 为 null 表示内置 Agent）。只允许尚无消息、未在运行的会话：
    /// 已有对话时上下文在原运行时里，无法迁移。会话 id 与文件保持不变。
    /// </summary>
    public async Task<AgentSession> SwitchRuntimeAsync(string sessionId, IAgentRuntime? runtime, ModelInfo model)
    {
        var old = Get(sessionId);
        if (old.Agent.IsRunning || old.Agent.Messages.Count > 0)
        {
            throw new InvalidOperationException("This session already has a conversation; start a new session to use a different agent.");
        }
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            await DisposeEntryAsync(entry);
        }
        logger.LogInformation("Session {SessionId} switched to {Runtime}", sessionId, runtime?.Id ?? "builtin");
        return Register(runtime is null
            ? AgentSession.CreateBuiltin(apis, model, old.Store, catalog.GetApiKey, OptionsFor(old.Store))
            : AgentSession.Rebind(old.Store, WithBridge(new ExternalAgent(runtime, model, old.Cwd), old.Store)));
    }

    /// <summary>按会话文件查找已打开的会话（同一文件只允许一个 Agent 写入）。</summary>
    public AgentSession? FindByFile(string filePath) =>
        _sessions.Values.Select(e => e.Session).FirstOrDefault(s => SameFile(s, filePath));

    private static bool SameFile(AgentSession session, string filePath) =>
        string.Equals(Path.GetFullPath(session.Store.FilePath), Path.GetFullPath(filePath), StringComparison.Ordinal);

    public bool IsRunning(string sessionId) => _sessions.TryGetValue(sessionId, out var entry) && entry.Session.Agent.IsRunning;

    private AgentSession Register(AgentSession session)
    {
        var entry = new Entry(session);
        entry.Subscription = session.Agent.Subscribe((e, ct) => ForwardAsync(entry, e));
        if (session.Agent is Agent.Agent builtin)
        {
            builtin.BeforeToolCall = (ctx, ct) => ApproveAsync(session.Id, session, ctx, ct);
        }
        else if (session.Agent is ExternalAgent external)
        {
            external.ApprovalHandler = (request, ct) => ApproveExternalAsync(session.Id, request, ct);
            external.InputHandler = (request, ct) => RequestUserInputAsync(session.Id, external.RuntimeId, request.ToolCallId, request.Questions, ct);
            external.ElicitationHandler = (request, ct) => ElicitExternalAsync(session.Id, external.RuntimeId, request, ct);
            external.PlanReviewHandler = (request, ct) =>
                ReviewPlanAsync(session.Id, external.RuntimeId, request.ToolCallId, request.PlanPath ?? "", request.Plan, ct, allowClear: false);
            external.PlanModeChanged += planMode => _ = SyncRuntimePlanModeAsync(session.Id, planMode);
            external.SubagentHandler = (key, name, model) => Subagents.OpenExternal(session.Id, key, session.Cwd, name, model);
            external.CommandsChanged += commands => Publish(entry,
                new CommandsChanged(commands.Select(c => new RuntimeCommandDto(c.Name, c.Description, c.ArgumentHint)).ToList()));
        }
        if (session.Tools is { } tools)
        {
            // 后台任务列表变化：推送整体快照（主会话与它的子 Agent 共用同一个 BackgroundTasks，这里只推送归属本会话的）
            entry.Tasks = tools.BackgroundTasks;
            entry.TasksChanged = owner =>
            {
                if (owner is null || owner == session.Id)
                {
                    PublishBackgroundTasks(entry);
                }
            };
            tools.BackgroundTasks.Changed += entry.TasksChanged;
        }
        _sessions[session.Id] = entry;
        logger.LogInformation("Session {SessionId} registered in {Cwd}", session.Id, session.Cwd);
        return session;
    }

    /// <summary>取会话；已被空闲释放的会话从文件重新打开。</summary>
    public AgentSession Get(string sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var entry))
            {
                return Touch(entry).Session;
            }
            if (_evicted.TryGetValue(sessionId, out var evicted) && File.Exists(evicted.FilePath))
            {
                logger.LogInformation("Reopening idle-released session {SessionId}", sessionId);
                return OpenFile(evicted.FilePath);
            }
            _evicted.TryRemove(sessionId, out _);
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");
        }
    }

    private static Entry Touch(Entry entry)
    {
        Volatile.Write(ref entry.LastActive, Environment.TickCount64);
        return entry;
    }

    /// <summary>
    /// 释放空闲且可释放（<see cref="CanRelease"/>）的会话。
    /// 有会话被释放、刚打开过会话或刚有一轮结束时做一次压缩式 GC，把临时分配占用的堆还给系统。
    /// </summary>
    internal async Task SweepAsync()
    {
        var released = new List<string>();
        lock (_gate)
        {
            var now = Environment.TickCount64;
            foreach (var (id, entry) in _sessions)
            {
                var session = entry.Session;
                if (now - Volatile.Read(ref entry.LastActive) < IdleTimeout.TotalMilliseconds || !CanRelease(session))
                {
                    continue;
                }
                _evicted[id] = new Evicted(session.Store.FilePath, session.Agent.PermissionMode, session.Approvals);
                released.Add(id);
            }
        }
        foreach (var id in released)
        {
            try
            {
                logger.LogInformation("Releasing idle session {SessionId}", id);
                await CloseCoreAsync(id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release idle session {SessionId}", id);
            }
        }
        if ((released.Count > 0 || Interlocked.Exchange(ref _collectPending, 0) == 1) && !_sessions.Values.Any(e => e.Session.Agent.IsRunning))
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }

    /// <summary>
    /// 关闭会话会结束它启动的后台进程（外部运行时则是 CLI 及其后台任务），这些会话不释放；
    /// 新会话还没有文件，释放后无法重新打开。
    /// </summary>
    private bool CanRelease(AgentSession session) =>
        !session.Agent.IsRunning && session.External is null && !Coordinator.HasRunningChildren(session.Id) &&
        session.Tools?.BackgroundTasks.All.Any(t => t.IsRunning && (t.Owner is null || t.Owner == session.Id)) != true &&
        File.Exists(session.Store.FilePath);

    public async Task CloseAsync(string sessionId)
    {
        _evicted.TryRemove(sessionId, out _);
        Interlocked.Exchange(ref _collectPending, 1);
        await CloseCoreAsync(sessionId);
    }

    private async Task CloseCoreAsync(string sessionId)
    {
        logger.LogInformation("Closing session {SessionId}", sessionId);
        await Coordinator.CloseChildrenAsync(sessionId);
        Subagents.Release(sessionId);
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            await DisposeEntryAsync(entry);
        }
        await Bridge.ForgetAsync(sessionId);
    }

    private static async Task DisposeEntryAsync(Entry entry)
    {
        entry.Subscription?.Dispose();
        await entry.TitleCancellation.CancelAsync();
        if (entry.Tasks is { } tasks && entry.TasksChanged is { } changed)
        {
            tasks.Changed -= changed;
        }
        await entry.Session.DisposeAsync();
        if (entry.TitleTask is { } titleTask)
        {
            await titleTask;
        }
        entry.TitleCancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _sweeper.DisposeAsync();
        foreach (var id in _sessions.Keys.ToArray())
        {
            await CloseAsync(id);
        }
        await Bridge.DisposeAsync();
    }

    /// <summary>
    /// 内置 Agent 的审批：按 <see cref="PermissionPolicy"/> 判定，需要询问时转给 UI；没有 UI 连接时拒绝。
    /// 子 Agent 以父会话（<paramref name="sessionId"/>）的名义请求，权限档位跟随父会话，标题前缀为子 Agent 名。
    /// 只读命令规则只用于处于只读模式的主会话；子 Agent（包括 explore / plan）一律按父会话的档位判定（docs/design/subagent-view.md §5.7）。
    /// </summary>
    public async ValueTask<ToolCallDecision> ApproveAsync(string sessionId, AgentSession session, BeforeToolCallContext ctx, CancellationToken ct,
        string? agentName = null)
    {
        var mode = TryGet(sessionId)?.Agent.PermissionMode ?? session.Agent.PermissionMode;
        var readOnly = agentName is null && (session.Tools?.IsReadOnly ?? false);
        var verdict = PermissionPolicy.Evaluate(ctx.Permission, mode, session.Workspace, session.Approvals, readOnly);
        if (verdict.Allowed)
        {
            return ToolCallDecision.Allow;
        }
        if (_client is null)
        {
            return ToolCallDecision.Deny("No client is connected to approve this tool call.");
        }
        var title = agentName is null ? verdict.Title : $"[{agentName}] {verdict.Title}";
        var response = await _client.RequestToolApprovalAsync(new ToolApprovalRequest(sessionId, ctx.ToolCall.Id, ctx.ToolCall.Name,
            ctx.ToolCall.ArgumentsJson, verdict.Kind, title, null, verdict.SessionKey is not null), ct);
        if (!response.Approved)
        {
            return ToolCallDecision.Deny(response.Reason is { Length: > 0 } reason
                ? $"The user denied this tool call: {reason}"
                : "The user denied this tool call.");
        }
        if (response.ForSession && verdict.SessionKey is { } key)
        {
            session.Approvals.Add(key);
        }
        return ToolCallDecision.Allow;
    }

    Task<UserInputResult> IUserInputService.AskAsync(string sessionId, string toolCallId, IReadOnlyList<UserQuestion> questions,
        CancellationToken cancellationToken) =>
        RequestUserInputAsync(sessionId, "builtin", toolCallId, questions, cancellationToken);

    /// <summary>
    /// 向用户提问（input/request，docs/design/ask-user.md §6）：不看权限档位，从不自动回答。
    /// 中止时抛出 <see cref="OperationCanceledException"/>；没有 UI 或请求失败时抛出 <see cref="UserInputUnavailableException"/>。
    /// </summary>
    public async Task<UserInputResult> RequestUserInputAsync(string sessionId, string source, string? toolCallId,
        IReadOnlyList<UserQuestion> questions, CancellationToken ct)
    {
        if (_client is not { } client)
        {
            throw new UserInputUnavailableException("No client is connected.");
        }
        var request = new UserInputRequest(sessionId, Guid.NewGuid().ToString("N"), source, toolCallId,
            questions.Select(q => new UserQuestionDto(q.Id, q.Header, q.Question,
                q.Options.Select(o => new UserQuestionOptionDto(o.Label, o.Description)).ToList(), q.MultiSelect, q.AllowOther, q.IsSecret)).ToList());
        UserInputResponse response;
        try
        {
            response = await client.RequestUserInputAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "input/request failed");
            throw new UserInputUnavailableException(ex.Message, ex);
        }
        if (response.Outcome != UserInputResponse.Answered)
        {
            return UserInputResult.Declined;
        }
        return new UserInputResult(UserInputOutcome.Answered, (response.Answers ?? [])
            .Select(a => new UserAnswer(a.QuestionId, a.Selected, string.IsNullOrWhiteSpace(a.Text) ? null : a.Text)).ToList());
    }

    // ---------------- Plan 模式（docs/design/plan-mode.md） ----------------

    /// <summary>enter_plan_mode 确认问题的 input/request 来源：界面据此本地化标题与选项。</summary>
    public const string EnterPlanModeSource = "enterPlanMode";

    Task<PlanReviewResult> IPlanModeService.ReviewAsync(string sessionId, string toolCallId, string planPath, string plan,
        CancellationToken cancellationToken) =>
        ReviewPlanAsync(sessionId, "builtin", toolCallId, planPath, plan, cancellationToken);

    async Task<UserInputResult> IPlanModeService.ConfirmEnterAsync(string sessionId, string toolCallId, UserQuestion question,
        CancellationToken cancellationToken)
    {
        var result = await RequestUserInputAsync(sessionId, EnterPlanModeSource, toolCallId, [question], cancellationToken);
        if (EnterPlanModeTool.Accepted(result))
        {
            await ApplyHostOptionsAsync(sessionId, new SessionOptionsDto(Role: SessionRole.PlanName));
        }
        return result;
    }

    /// <summary>
    /// 计划审批（plan/review，§13.1）：不看权限档位，从不自动批准。批准时按选项切换模式与权限并推送 optionsChanged（§10）。
    /// 中止时抛出 <see cref="OperationCanceledException"/>；没有 UI 或请求失败时抛出 <see cref="UserInputUnavailableException"/>。
    /// </summary>
    /// <param name="allowClear">提供“清空上下文后执行”（只有内置 Agent）。</param>
    public async Task<PlanReviewResult> ReviewPlanAsync(string sessionId, string source, string? toolCallId, string planPath, string plan,
        CancellationToken ct, bool allowClear = true)
    {
        if (_client is not { } client)
        {
            throw new UserInputUnavailableException("No client is connected.");
        }
        var session = Get(sessionId);
        var choices = new List<string>();
        if (allowClear && session.Agent is TKCode.Agent.Agent { Compactor: not null })
        {
            choices.Add(PlanReviews.IdOf(PlanReviewChoice.Clear));
        }
        choices.Add(PlanReviews.IdOf(PlanReviewChoice.Auto));
        choices.Add(PlanReviews.IdOf(PlanReviewChoice.Ask));
        if (session.External is { } external ? external.SupportsSubagents : session.Tools?.Services.Agents is not null)
        {
            choices.Add(PlanReviews.IdOf(PlanReviewChoice.Acp));
        }
        var request = new PlanReviewRequest(sessionId, Guid.NewGuid().ToString("N"), source, toolCallId, planPath, plan, choices,
            SessionOptions.ToWire(session.Agent.PermissionMode));
        PlanReviewResponse response;
        try
        {
            response = await client.ReviewPlanAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "plan/review failed");
            throw new UserInputUnavailableException(ex.Message, ex);
        }
        switch (response.Outcome)
        {
            case PlanReviewResponse.Approved when response.Choice is { } id && choices.Contains(id) && PlanReviews.Parse(id) is { } choice:
                await ApplyPlanChoiceAsync(sessionId, session, choice);
                return new PlanReviewResult(PlanReviewOutcome.Approved, choice,
                    EditedPlan: string.IsNullOrWhiteSpace(response.EditedPlan) ? null : response.EditedPlan);
            case PlanReviewResponse.Revise:
                return new PlanReviewResult(PlanReviewOutcome.Revise, Feedback: string.IsNullOrWhiteSpace(response.Feedback) ? null : response.Feedback);
            default:
                return PlanReviewResult.Dismissed;
        }
    }

    /// <summary>批准的效果（§10）：Agent + 自动批准（完全授权时保持）/ Agent + 询问 / ACP + 当前档位。</summary>
    private Task ApplyPlanChoiceAsync(string sessionId, AgentSession session, PlanReviewChoice choice)
    {
        var current = session.Agent.PermissionMode;
        var (role, permission) = choice switch
        {
            PlanReviewChoice.Acp => (SessionRole.AcpName, current),
            PlanReviewChoice.Ask => (SessionRole.AgentName, PermissionMode.Ask),
            _ => (SessionRole.AgentName, current == PermissionMode.FullAccess ? PermissionMode.FullAccess : PermissionMode.AutoApprove),
        };
        return ApplyHostOptionsAsync(sessionId, new SessionOptionsDto(PermissionMode: SessionOptions.ToWire(permission), Role: role));
    }

    /// <summary>Host 主动改变模式或权限：与 options/set 相同的应用与持久化，再推送 optionsChanged 让界面更新选择器（§13.2）。</summary>
    private async Task ApplyHostOptionsAsync(string sessionId, SessionOptionsDto options)
    {
        var session = Get(sessionId);
        var applied = SessionOptions.Apply(session, options);
        await session.Store.AppendOptionsChangeAsync(session.Agent.ThinkingLevel, session.Agent.FastMode, session.ContextWindow,
            CancellationToken.None, session.Agent.PermissionMode, session.Role.Name);
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            Publish(entry, new SessionOptionsChanged(applied));
        }
    }

    /// <summary>外部运行时自己进入或离开了 plan（例如模型调用了 EnterPlanMode）：同步 TKCode 的模式（§15）。</summary>
    private async Task SyncRuntimePlanModeAsync(string sessionId, bool planMode)
    {
        try
        {
            var session = Get(sessionId);
            var inPlan = session.Role.Name == SessionRole.PlanName;
            if (planMode != inPlan)
            {
                await ApplyHostOptionsAsync(sessionId, new SessionOptionsDto(Role: planMode ? SessionRole.PlanName : SessionRole.AgentName));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync the runtime's plan mode for session {SessionId}", sessionId);
        }
    }

    /// <summary>运行时的结构化信息请求（ACP elicitation）→ MCP 表单卡片；没有 UI 或失败时 cancel。</summary>
    private async Task<RuntimeElicitationResult> ElicitExternalAsync(string sessionId, string runtimeId, RuntimeElicitationRequest request,
        CancellationToken ct)
    {
        if (_client is not { } client)
        {
            return RuntimeElicitationResult.Cancel;
        }
        var title = runtimes.Find(runtimeId)?.DisplayName ?? runtimeId;
        try
        {
            var response = await client.RequestMcpElicitationAsync(
                new McpElicitRequest(sessionId, runtimeId, title, "form", request.Message, request.SchemaJson, null), ct);
            return new RuntimeElicitationResult(response.Action, response.ContentJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Runtime elicitation failed");
            return RuntimeElicitationResult.Cancel;
        }
    }

    /// <summary>外部运行时的审批：没有 UI 连接时拒绝（ExternalAgent 也会把异常与断连转成拒绝）。</summary>
    private async ValueTask<ApprovalDecision> ApproveExternalAsync(string sessionId, RuntimeApprovalRequest request, CancellationToken ct)
    {
        if (_client is null)
        {
            return ApprovalDecision.Deny("No client is connected.");
        }
        var kind = request.Kind switch
        {
            ApprovalKind.Command => "command",
            ApprovalKind.FileChange => "fileChange",
            _ => "tool",
        };
        var response = await _client.RequestToolApprovalAsync(new ToolApprovalRequest(sessionId, request.ToolCallId, request.ToolName,
            request.ArgumentsJson, kind, request.Title, request.Reason, request.CanAllowForSession), ct);
        if (!response.Approved)
        {
            return ApprovalDecision.Deny(response.Reason);
        }
        return response.ForSession && request.CanAllowForSession
            ? new ApprovalDecision(ApprovalOutcome.AllowForSession)
            : ApprovalDecision.AllowOnce;
    }

    private ValueTask ForwardAsync(Entry entry, AgentEvent e)
    {
        Touch(entry);
        if (e is AgentEndEvent)
        {
            Interlocked.Exchange(ref _collectPending, 1);
        }
        if (Map(e) is { } mapped)
        {
            // 用户消息还没追加进历史：它的下标就是当前历史的长度（分叉的截止点，docs/design/message-rewind.md §5.1）
            if (mapped is MessageStarted { Role: "user" } started && e is MessageStartEvent)
            {
                mapped = started with { Index = entry.Session.History.Count };
            }
            Publish(entry, mapped);
        }
        if (e is MessageEndEvent { Message: UserMessage { Source: null } user } &&
            Interlocked.CompareExchange(ref entry.TitleAttempted, 1, 0) == 0)
        {
            var model = FastModelFor(entry.Session.Agent.Model);
            entry.TitleTask = Task.Run(() => GenerateTitleAsync(entry, model, user));
        }
        return ValueTask.CompletedTask;
    }

    private async Task GenerateTitleAsync(Entry entry, ModelInfo model, UserMessage user)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.TitleCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var session = entry.Session;
            var title = await _titles.GenerateAsync(model, session.Cwd, session.Id, user, timeout.Token);
            if (title is not null && await session.Store.TryAppendGeneratedTitleAsync(title, timeout.Token))
            {
                Publish(entry, new SessionTitleChanged(title));
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭会话或超时：保留首条消息作为标题。
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to generate title for session {SessionId}", entry.Session.Id);
        }
    }

    private void PublishBackgroundTasks(Entry entry)
    {
        // 运行中的全部保留；已结束的只保留最近 20 个（任务球不无限增长）
        var owned = entry.Tasks?.All.Where(t => t.Owner is null || t.Owner == entry.Session.Id).ToList() ?? [];
        var tasks = owned.Where(t => t.IsRunning)
            .Concat(owned.Where(t => !t.IsRunning).TakeLast(20))
            .Select(t => new BackgroundTaskDto(t.Id, t.Type, t.Description, t.Status))
            .ToList();
        Publish(entry, new BackgroundTasksChanged(tasks));
    }

    private void Publish(Entry entry, SessionEvent mapped)
    {
        if (_client is null)
        {
            return;
        }
        lock (entry.PublishGate)
        {
            try
            {
                // 标题与后台任务可和主循环并发完成；序号分配与发送必须一起串行化。
                _client.OnSessionEvent(new SessionEventNotification(entry.Session.Id, ++entry.Seq, mapped));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to forward session event");
            }
        }
    }

    internal static SessionEvent? Map(AgentEvent e) => e switch
    {
        AgentStartEvent => new AgentStarted(),
        AgentEndEvent => new AgentEnded(),
        MessageStartEvent { Message: UserMessage user } s => new MessageStarted(s.MessageId, "user", TextOf(user.Content),
            user.Content.OfType<ImageContent>().Select(i => i.Path).OfType<string>().ToArray() is { Length: > 0 } images ? images : null,
            user.Source, ToDto(user.Display)),
        MessageStartEvent { Message: ToolResultMessage or SystemMessage } => null,
        MessageStartEvent s => new MessageStarted(s.MessageId, "assistant", null),
        MessageUpdateEvent { Event: RetryEvent r } u => new RequestRetrying(u.MessageId, r.Attempt, r.MaxAttempts, (long)r.Delay.TotalMilliseconds, r.Reason),
        MessageUpdateEvent { Event: TextDeltaEvent d } u => new TextDelta(u.MessageId, d.ContentIndex, d.Delta),
        MessageUpdateEvent { Event: TextEndEvent d } u => new TextEnded(u.MessageId, d.ContentIndex),
        MessageUpdateEvent { Event: ThinkingDeltaEvent d } u => new ThinkingDelta(u.MessageId, d.ContentIndex, d.Delta),
        MessageUpdateEvent { Event: ThinkingEndEvent d } u => new ThinkingEnded(u.MessageId, d.ContentIndex),
        MessageUpdateEvent { Event: ToolCallStartEvent d } u => new ToolCallStarted(u.MessageId, d.ContentIndex, d.Id, d.Name),
        MessageUpdateEvent { Event: ToolCallDeltaEvent d } u => new ToolCallDelta(u.MessageId, d.ContentIndex, d.JsonFragment),
        MessageUpdateEvent { Event: ToolCallEndEvent d } u => new ToolCallEnded(u.MessageId, d.ContentIndex, d.ToolCall.Id, d.ToolCall.ArgumentsJson),
        MessageEndEvent { Message: AssistantMessage a } m => new MessageEnded(m.MessageId, "assistant", a.StopReason.ToString(), a.ErrorMessage,
            a.Usage is { } usage ? new UsageDto(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite) : null),
        MessageEndEvent { Message: UserMessage } m => new MessageEnded(m.MessageId, "user", "Stop", null, null),
        ToolExecutionStartEvent t => new ToolExecutionStarted(t.ToolCallId, t.ToolName, t.ArgumentsJson),
        CompactionStartEvent c => new CompactionStarted(Trigger(c.Trigger), c.PreTokens),
        CompactionEndEvent c => new CompactionEnded(Trigger(c.Trigger), c.PreTokens, c.Result?.PostTokens, c.Error),
        CompactionSuspendedEvent c => new CompactionSuspended(c.Reason),
        ToolExecutionUpdateEvent t => new ToolExecutionUpdated(t.ToolCallId, t.ToolName, HistoryPaging.Truncate(TextOf(t.Partial.Content)), t.Partial.Details),
        ToolExecutionEndEvent t => new ToolExecutionEnded(t.ToolCallId, t.ToolName, t.Result.IsError, HistoryPaging.Truncate(TextOf(t.Result.Content)),
            t.Duration.TotalMilliseconds, t.Result.Details),
        _ => null,
    };

    private static string Trigger(TKCode.Agent.Compaction.CompactionTrigger trigger) => trigger switch
    {
        TKCode.Agent.Compaction.CompactionTrigger.Auto => "auto",
        TKCode.Agent.Compaction.CompactionTrigger.Reactive => "reactive",
        TKCode.Agent.Compaction.CompactionTrigger.PlanHandoff => "planHandoff",
        _ => "manual",
    };

    public static IReadOnlyList<PromptSegmentDto>? ToDto(IReadOnlyList<MessageSegment>? segments) =>
        segments?.Select(s => new PromptSegmentDto(s.Kind, s.Text, s.Value, s.Label)).ToArray();

    private static string TextOf(IReadOnlyList<ContentBlock> content) => string.Concat(content.OfType<TextContent>().Select(t => t.Text));

    private sealed class Entry(AgentSession session)
    {
        public AgentSession Session { get; } = session;
        public IDisposable? Subscription { get; set; }
        public BackgroundTasks? Tasks { get; set; }
        public Action<string?>? TasksChanged { get; set; }
        public Lock PublishGate { get; } = new();
        public long Seq;
        public long LastActive = Environment.TickCount64;
        public int TitleAttempted = File.Exists(session.Store.FilePath) ? 1 : 0;
        public CancellationTokenSource TitleCancellation { get; } = new();
        public Task? TitleTask;
    }

    /// <summary>被释放的会话：重新打开时恢复不落盘的权限档位与本会话内的授权。</summary>
    private sealed record Evicted(string FilePath, PermissionMode PermissionMode, SessionApprovals Approvals);
}
