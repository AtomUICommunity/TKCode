using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Assets;
using TKCode.Agent.Coding.Sessions;
using Microsoft.Extensions.Logging;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Config;
using TKCode.Rpc;
using TKCode.Runtimes;

namespace TKCode.AgentHost;

public sealed class AgentHostServer(
    SessionManager sessions,
    ModelCatalog catalog,
    ProviderTester tester,
    TKCode.AgentHost.Catalog.CatalogSync catalogSync,
    RuntimeRegistry runtimes,
    SessionIndex index,
    McpService mcp,
    FileIndexService files,
    ILogger<AgentHostServer> logger)
    : IAgentHostServer
{
    private const int MaxListedSessions = 200;

    /// <summary>单次查询的上限。侧栏按“展开更多”逐页累加，会话总数超过它时不再返回更多。</summary>
    private const int MaxQuerySessions = 2000;

    public Task<HostInfo> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProtocolVersion != RpcProtocol.Version)
        {
            throw new InvalidOperationException($"Protocol mismatch: client {request.ProtocolVersion}, host {RpcProtocol.Version}.");
        }
        var version = typeof(AgentHostServer).Assembly.GetName().Version?.ToString() ?? "0";
        return Task.FromResult(new HostInfo(RpcProtocol.Version, version, Environment.ProcessId));
    }

    public Task<IReadOnlyList<ModelDto>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AllModelDtos(catalog, runtimes));

    /// <summary>目录模型在前，外部运行时（已就绪）的模型在后。</summary>
    public static IReadOnlyList<ModelDto> AllModelDtos(ModelCatalog catalog, RuntimeRegistry runtimes) =>
        [.. ModelDtos(catalog), .. RuntimeModelDtos(runtimes)];

    public static IEnumerable<ModelDto> RuntimeModelDtos(RuntimeRegistry runtimes) =>
        runtimes.All.Where(r => r.Descriptor.Status == RuntimeStatus.Ready)
            .SelectMany(r => r.Descriptor.Models.Select(m => SessionOptions.ToDto(m, r.DisplayName, r.Id, r.Descriptor.Capabilities.SupportsSubagents)));

    public Task<IReadOnlyList<RuntimeDto>> ListRuntimesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RuntimeDto>>(runtimes.All.Select(r => r.Descriptor).Select(d => new RuntimeDto(
            d.Id, d.DisplayName, (RuntimeStatusDto)(int)d.Status, d.Version, d.ExecutablePath, d.AuthMethod, d.Error, d.Models.Count,
            d.Capabilities.SupportsSteering, d.Capabilities.SupportsResume)).ToArray());

    /// <summary>模型选择器中的模型（以 Ref 区分不同厂商的同名模型）。</summary>
    public static IReadOnlyList<ModelDto> ModelDtos(ModelCatalog catalog)
    {
        var names = catalog.Providers.ToDictionary(p => p.Id, p => p.Name);
        return catalog.Models.Select(m => SessionOptions.ToDto(m, names.GetValueOrDefault(m.Provider, m.Provider))).ToArray();
    }

    public Task<ProvidersSnapshotDto> ListProvidersAsync(CancellationToken cancellationToken = default)
    {
        var providers = catalog.Providers.Select(p => new ProviderDto(
            p.Id, p.Name, p.Api ?? "", p.BaseUrl, p.IsBuiltIn, p.IsConfigured, p.Enabled, p.IsActive,
            (KeySourceDto)(int)p.Key.Source, p.Key.Variable,
            p.Key is { Source: KeySource.Settings, Value: { } value } ? Secrets.Mask(value) : null,
            p.HasModelsOverride,
            p.Models.Select(m => new ProviderModelDto(m.Model.Id, m.Model.Name, m.Enabled, m.Model.ContextWindow, m.Model.Reasoning,
                m.Model.SupportsImages, m.InCatalog, m.Model.MaxTokens, m.Model.SupportsFastMode,
                m.Catalog?.Cost is [{ } input, ..] ? input : null,
                m.Catalog?.Cost is [_, { } output, ..] ? output : null,
                m.IsDiscovered, m.HasOverride, m.Api, m.ApiOverridden, m.DefaultApi)).ToArray(),
            p.Errors,
            p.SupportsDiscovery,
            p.DiscoveryError)).ToArray();
        return Task.FromResult(new ProvidersSnapshotDto(providers, catalog.Errors, catalog.Warnings, ConfigPaths.SettingsFile, catalog.BuiltIn.Generated));
    }

    public Task<IReadOnlyList<CatalogProviderDto>> ListCatalogProvidersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CatalogProviderDto>>(catalog.BuiltIn.Providers
            .Select(p => new CatalogProviderDto(p.Key, p.Value.Name, p.Value.Api, p.Value.BaseUrl, p.Value.Env, p.Value.Models.Count, p.Value.KeyOptional))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    public Task ReloadSettingsAsync(CancellationToken cancellationToken = default)
    {
        catalog.Reload();
        sessions.Mcp.Reload();
        return Task.CompletedTask;
    }

    public Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken = default) =>
        tester.TestAsync(request, cancellationToken);

    public Task RefreshProviderModelsAsync(string providerId, CancellationToken cancellationToken = default) =>
        catalog.RefreshModelsAsync(providerId, cancellationToken);

    public Task<CatalogStatusDto> GetCatalogStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(catalogSync.Status());

    public Task<CatalogUpdateDto> CheckCatalogAsync(CancellationToken cancellationToken = default) =>
        catalogSync.CheckAsync(catalog.Providers.Where(p => p.IsConfigured).Select(p => p.Id).ToHashSet(), cancellationToken);

    public Task<CatalogStatusDto> ApplyCatalogUpdateAsync(CancellationToken cancellationToken = default) => Task.FromResult(catalogSync.Apply());

    public Task DiscardCatalogUpdateAsync(CancellationToken cancellationToken = default)
    {
        catalogSync.Discard();
        return Task.CompletedTask;
    }

    public async Task<SessionDto> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var external = runtimes.FindModel(request.ModelId);
        var model = external?.Model ?? catalog.Get(request.ModelId);
        var session = await sessions.CreateAsync(request.Cwd, model, external?.Runtime, request.ProjectId, request.Directories, cancellationToken);
        SessionOptions.Initialize(session);
        await session.Store.AppendModelChangeAsync(model.Provider, model.Id, cancellationToken);
        var options = SessionOptions.Apply(session, request.Options, sessions.Assets.Get(session.Cwd));
        // 会话文件在第一条消息时才创建；这条参数记录先缓存在 SessionStore 中，随第一条消息写入。
        await session.Store.AppendOptionsChangeAsync(session.Agent.ThinkingLevel, session.Agent.FastMode, session.ContextWindow, cancellationToken,
            session.Agent.PermissionMode, session.Role.Name);
        return ToDto(session, options);
    }

    private static SessionDto ToDto(AgentSession session, SessionOptionsDto options) =>
        new(session.Id, session.Cwd, ModelCatalog.RefOf(session.Agent.Model), session.Store.FilePath, options, session.External?.RuntimeId,
            session.Store.ProjectId, session.Store.SessionDirectories);

    public async Task<SessionDto> AddSessionDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        var session = await sessions.AddSessionDirectoryAsync(sessionId, path, cancellationToken);
        return ToDto(session, SessionOptions.Current(session));
    }

    public async Task<SessionDto> RemoveSessionDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        var session = await sessions.RemoveSessionDirectoryAsync(sessionId, path, cancellationToken);
        return ToDto(session, SessionOptions.Current(session));
    }

    public Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(string? cwd, CancellationToken cancellationToken = default)
    {
        var projects = sessions.Projects.Projects;
        return Task.FromResult<IReadOnlyList<SessionSummaryDto>>(Filter(index.Refresh(), cwd, null, projects).Take(MaxListedSessions)
            .Select(e => ToSummary(e, projects)).ToArray());
    }

    public Task<SessionPageDto> QuerySessionsAsync(string? cwd, int offset, int limit, string? projectId = null, CancellationToken cancellationToken = default)
    {
        var projects = sessions.Projects.Projects;
        var all = Filter(index.Refresh(), cwd, projectId, projects).ToArray();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxQuerySessions);
        var page = all.Skip(offset).Take(limit).Select(e => ToSummary(e, projects)).ToArray();
        var next = offset + page.Length < all.Length ? offset + page.Length : (int?)null;
        return Task.FromResult(new SessionPageDto(page, all.Length, next));
    }

    /// <summary>按 cwd 与所属项目（docs/design/projects.md §5.3）筛选。</summary>
    private static IEnumerable<SessionIndexEntry> Filter(IEnumerable<SessionIndexEntry> entries, string? cwd, string? projectId,
        IReadOnlyList<Project> projects)
    {
        if (cwd is not null)
        {
            entries = entries.Where(e => PathRules.SamePath(e.Cwd, cwd));
        }
        if (projectId is not null)
        {
            var wanted = projectId == ProjectCatalog.NoProjectFilter ? null : projectId;
            entries = entries.Where(e => ProjectCatalog.Resolve(projects, e.ProjectId, e.Cwd)?.Id == wanted);
        }
        return entries;
    }

    private SessionSummaryDto ToSummary(SessionIndexEntry e, IReadOnlyList<Project> projects)
    {
        var provider = e.ModelRef is { } model && model.IndexOf('/') is var slash and > 0 ? model[..slash] : null;
        var runtime = e.Runtime ?? runtimes.Find(provider)?.Id;
        return new SessionSummaryDto(e.SessionId, e.FilePath, e.Cwd, e.CustomTitle ?? e.FirstMessageTitle, e.ModelRef,
            new DateTimeOffset(e.LastWriteTicks, TimeSpan.Zero), sessions.IsRunning(e.SessionId), runtime, e.Pinned,
            e.CustomTitle is not null, e.NativeSessionId, ProjectCatalog.Resolve(projects, e.ProjectId, e.Cwd)?.Id);
    }

    public async Task RenameSessionAsync(string filePath, string title, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (sessions.FindByFile(filePath) is { } open)
        {
            await open.Store.AppendMetaAsync(title, null, cancellationToken);
        }
        else
        {
            await SessionStore.AppendMetaToFileAsync(RequireSessionFile(filePath), title, null, cancellationToken);
        }
    }

    public async Task PinSessionAsync(string filePath, bool pinned, CancellationToken cancellationToken = default)
    {
        if (sessions.FindByFile(filePath) is { } open)
        {
            await open.Store.AppendMetaAsync(null, pinned, cancellationToken);
        }
        else
        {
            await SessionStore.AppendMetaToFileAsync(RequireSessionFile(filePath), null, pinned, cancellationToken);
        }
    }

    public async Task DeleteSessionAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var path = RequireSessionFile(filePath);
        if (sessions.FindByFile(path) is { } open)
        {
            open.Agent.Abort();
            await sessions.CloseAsync(open.Id);
        }
        var sessionId = SessionStore.ReadSummary(path).SessionId;
        File.Delete(path);
        index.Forget(path);
        // 子 Agent 的转录随会话删除（docs/design/subagent-view.md §5.1）
        var subagents = SubagentHub.DirectoryOf(sessionId);
        if (sessionId.Length > 0 && Directory.Exists(subagents))
        {
            Directory.Delete(subagents, recursive: true);
        }
        logger.LogInformation("Session file {File} deleted", path);
    }

    /// <summary>在新会话中继续：复制当前分支的消息与参数到同一项目下的新会话（外部 Agent 的上下文在 CLI 里，无法复制）。</summary>
    public async Task<OpenSessionResult> ForkSessionAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var snapshot = SessionStore.Read(RequireSessionFile(filePath));
        if (sessions.RuntimeOf(snapshot) is not null)
        {
            throw new InvalidOperationException("Sessions of external agents cannot be continued in a new session.");
        }
        if (snapshot.Messages.Count == 0)
        {
            throw new InvalidOperationException("The session has no messages to continue from.");
        }
        var store = SessionStore.Create(snapshot.Cwd, projectId: snapshot.ProjectId);
        if (snapshot.SessionDirectories.Count > 0)
        {
            await store.AppendDirectoriesAsync(snapshot.SessionDirectories, cancellationToken);
        }
        // 计划文件跟着会话走（docs/design/plan-mode.md §6）
        TKCode.Agent.Coding.Tools.Plan.PlanFiles.CopyFor(filePath, store.FilePath);
        CopySubagents(snapshot.SessionId, store.SessionId);
        if (snapshot.ModelId is not null)
        {
            await store.AppendModelChangeAsync(snapshot.ModelProvider ?? "", snapshot.ModelId, cancellationToken);
        }
        var permission = snapshot.PermissionMode;
        await store.AppendOptionsChangeAsync(snapshot.ThinkingLevel, snapshot.FastMode, snapshot.ContextWindow, cancellationToken, permission, snapshot.Role);
        if (snapshot.CustomTitle is { } title)
        {
            await store.AppendMetaAsync(title, null, cancellationToken);
        }
        foreach (var message in snapshot.EarlierMessages)
        {
            await store.AppendMessageAsync(message, cancellationToken);
        }
        if (snapshot.Compaction is { } c)
        {
            // 压缩过的会话：新会话从同一份摘要继续（系统提示在打开时按当前环境重建）
            await store.AppendCompactionAsync(c.Trigger, c.Summary, c.PreTokens, c.PostTokens, c.DurationMs, c.Instructions, c.TruncatedForRetry,
                cancellationToken);
        }
        foreach (var message in snapshot.Messages)
        {
            await store.AppendMessageAsync(message, cancellationToken);
        }
        return await OpenSessionAsync(store.FilePath, cancellationToken);
    }

    /// <summary>只允许操作会话目录下的 .jsonl 文件。</summary>
    private static string RequireSessionFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var root = Path.GetFullPath(SessionStore.DefaultRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            throw new InvalidOperationException($"'{filePath}' is not a TKCode session file.");
        }
        return full;
    }

    public Task<OpenSessionResult> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default) =>
        OpenSessionAsync(filePath, null, cancellationToken);

    public Task<HistoryPageDto> GetHistoryAsync(string sessionId, int before, int turnLimit, CancellationToken cancellationToken = default)
    {
        var messages = sessions.Get(sessionId).History;
        before = Math.Clamp(before, 0, messages.Count);
        var start = HistoryPaging.StartOfRecentTurns(messages, before, Math.Max(1, turnLimit));
        return Task.FromResult(new HistoryPageDto(ToHistory(messages, start, before), start));
    }

    public Task<OpenSessionResult> OpenSessionAsync(string filePath, int? turnLimit, CancellationToken cancellationToken = default) =>
        Task.FromResult(Describe(sessions.OpenFile(filePath), turnLimit));

    public async Task<PromptResult> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        var behavior = request.WhileRunning == StreamingBehaviorDto.Steer ? StreamingBehavior.Steer : StreamingBehavior.FollowUp;
        var session = sessions.Get(request.SessionId);
        var message = TKCode.Agent.Coding.Sessions.ImageAttachments.CreatePrompt(ExpandCommands(session.Cwd, session.Agent.Model, request),
            (request.Images ?? []).Select(i => (i.Path, i.MimeType))) with
        {
            Display = request.Segments?.Select(s => new MessageSegment(s.Kind, s.Text, s.Value, s.Label)).ToArray(),
        };
        if (request.McpPrompt is not null || request.McpResources is { Count: > 0 })
        {
            message = await mcp.ExpandAsync(session.Cwd, request, message, session.Agent.Model.SupportsImages, cancellationToken);
        }
        var disposition = session.Prompt(message, behavior);
        return new PromptResult(disposition == PromptDisposition.Started);
    }

    /// <summary>
    /// 输入框里的 command 与 skill tag（docs/design/composer.md §9.2、§10.2）：
    /// skill 的说明以 &lt;skill&gt; 块、command 的 .md 正文依次放在用户文字之前（skill 的名字仍留在用户文字里）。
    /// 找不到的 command / skill 报错（不悄悄当作普通文字发出去）。
    /// 内置提示词命令（/init、/commit，docs/design/composer.md §9.3）优先于同名的自定义 command。
    /// </summary>
    private string ExpandCommands(string cwd, ModelInfo model, PromptRequest request)
    {
        var tags = request.Segments?.Where(s => s.Kind is "command" or "skill" && s.Value is not null).ToList() ?? [];
        if (tags.Count == 0)
        {
            return request.Text;
        }
        var catalog = sessions.Assets.Get(cwd);
        var parts = new List<string>();
        foreach (var skill in tags.Where(t => t.Kind == "skill").Select(t => t.Value!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var asset = catalog.FindSkill(skill) ?? throw new InvalidOperationException($"Unknown skill /{skill}.");
            var body = TKCode.Agent.Coding.Assets.AgentAssetStore.ReadBody(asset.Path)
                       ?? throw new InvalidOperationException($"Cannot read skill /{skill} ({asset.Path}).");
            parts.Add(TKCode.Agent.Coding.Assets.SkillBlock.Render(asset, body));
        }
        foreach (var name in tags.Where(t => t.Kind == "command").Select(t => t.Value!))
        {
            if (BuiltinPromptCommands.Render(name, model) is { } prompt)
            {
                parts.Add(prompt);
                continue;
            }
            var asset = catalog.FindCommand(name) ?? throw new InvalidOperationException($"Unknown command /{name}.");
            var body = TKCode.Agent.Coding.Assets.AgentAssetStore.ReadBody(asset.Path)
                       ?? throw new InvalidOperationException($"Cannot read command /{name} ({asset.Path}).");
            parts.Add(body.Trim());
        }
        if (request.Text.Trim().Length > 0)
        {
            parts.Add(request.Text.Trim());
        }
        return string.Join("\n\n", parts);
    }

    public Task AbortAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Abort requested for session {SessionId} (user stop)", sessionId);
        // 被空闲释放的会话不在运行，无需为了停止而重新打开
        sessions.TryGet(sessionId)?.Agent.Abort();
        return Task.CompletedTask;
    }

    public Task CompactAsync(string sessionId, string? instructions, CancellationToken cancellationToken = default)
    {
        var session = sessions.Get(sessionId);
        logger.LogInformation("Compaction requested for session {SessionId}", sessionId);
        // 不等待压缩完成：进度与结果以会话事件推送
        _ = session.CompactAsync(string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim());
        return Task.CompletedTask;
    }

    public Task<ContextUsageDto> GetContextUsageAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var usage = sessions.Get(sessionId).GetContextUsage();
        var breakdown = usage.Breakdown is { } b
            ? new ContextBreakdownDto(b.SystemPrompt, b.SystemTools, b.McpTools, b.Skills, b.Messages, b.Other)
            : null;
        return Task.FromResult(new ContextUsageDto(usage.Tokens, breakdown, usage.Cache.HitRate));
    }

    public async Task<SessionOptionsDto> SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken = default)
    {
        var session = sessions.Get(sessionId);
        var target = runtimes.FindModel(modelId);
        var model = target?.Model ?? catalog.Get(modelId);
        if (target?.Runtime.Id != session.External?.RuntimeId)
        {
            // 换到另一个运行时（或内置 ↔ 外部）：只有尚无消息的会话可以，换掉底层的 Agent 循环
            var permission = session.Agent.PermissionMode;
            session = await sessions.SwitchRuntimeAsync(sessionId, target?.Runtime, model);
            SessionOptions.Initialize(session);
            session.Agent.PermissionMode = permission; // 权限档位是用户的选择，不随运行时重置
        }
        session.Agent.Model = model;
        await session.Store.AppendModelChangeAsync(model.Provider, model.Id, cancellationToken);
        return await SetOptionsAsync(sessionId, new SessionOptionsDto(), cancellationToken);
    }

    public async Task<SessionOptionsDto> SetOptionsAsync(string sessionId, SessionOptionsDto options, CancellationToken cancellationToken = default)
    {
        var session = sessions.Get(sessionId);
        var applied = SessionOptions.Apply(session, options, options.Role is null ? null : sessions.Assets.Get(session.Cwd));
        await session.Store.AppendOptionsChangeAsync(session.Agent.ThinkingLevel, session.Agent.FastMode, session.ContextWindow, cancellationToken,
            session.Agent.PermissionMode, session.Role.Name);
        return applied;
    }

    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default) => sessions.CloseAsync(sessionId);

    public Task<SubagentOpenResult> OpenSubagentAsync(string sessionId, string toolCallId, CancellationToken cancellationToken = default) =>
        Task.FromResult(sessions.Subagents.Open(sessionId, toolCallId, () => ParentHistory(sessionId),
            messages => ToHistory(messages, 0, messages.Count)));

    private IReadOnlyList<Message>? ParentHistory(string sessionId)
    {
        try
        {
            return sessions.Get(sessionId).History;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    public Task CloseSubagentAsync(string sessionId, string toolCallId, CancellationToken cancellationToken = default)
    {
        sessions.Subagents.Close(sessionId, toolCallId);
        return Task.CompletedTask;
    }

    private OpenSessionResult Describe(AgentSession session, int? turnLimit = null)
    {
        var messages = session.History;
        var start = turnLimit is { } limit ? HistoryPaging.StartOfRecentTurns(messages, messages.Count, Math.Max(1, limit)) : 0;
        return new OpenSessionResult(
            ToDto(session, SessionOptions.Current(session)),
            ToHistory(messages, start, messages.Count),
            session.Agent.IsRunning,
            start);
    }

    private static HistoryMessageDto[] ToHistory(IReadOnlyList<Message> messages, int start, int end)
    {
        var result = new List<HistoryMessageDto>(end - start);
        for (var i = start; i < end; i++)
        {
            if (ToHistory(messages[i]) is { } dto)
            {
                result.Add(dto);
            }
        }
        return result.ToArray();
    }

    private static HistoryMessageDto? ToHistory(Message message) => message switch
    {
        UserMessage u => new HistoryMessageDto("user", u.Timestamp, u.Content.Select(c => c switch
        {
            TextContent t => new HistoryText(t.Text),
            ImageContent { Path: { } path } => (HistoryBlockDto?)new HistoryImage(path),
            _ => null,
        }).OfType<HistoryBlockDto>().ToArray(), Source: u.Source, Segments: SessionManager.ToDto(u.Display)),
        AssistantMessage a => new HistoryMessageDto("assistant", a.Timestamp,
            a.Content.Select(ToHistory).OfType<HistoryBlockDto>().ToArray(),
            a.StopReason.ToString(), a.ErrorMessage,
            a.Usage is { } usage ? new UsageDto(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite) : null),
        ToolResultMessage r => new HistoryMessageDto("toolResult", r.Timestamp,
            [new HistoryToolResult(r.ToolCallId, r.ToolName, r.IsError,
                HistoryPaging.Truncate(string.Concat(r.Content.OfType<TextContent>().Select(t => t.Text))), r.Details)]),
        _ => null,
    };

    private static HistoryBlockDto? ToHistory(ContentBlock block) => block switch
    {
        TextContent t => new HistoryText(t.Text),
        ThinkingContent t => new HistoryThinking(HistoryPaging.Truncate(t.Thinking), t.DurationMs),
        ToolCallContent c => new HistoryToolCall(c.Id, c.Name, c.ArgumentsJson),
        _ => null,
    };

    /// <summary>分叉会话时复制子 Agent 的转录：复制过去的工具调用 id 不变，卡片仍能打开（docs/design/subagent-view.md §5.1）。</summary>
    private void CopySubagents(string fromSessionId, string toSessionId)
    {
        var from = SubagentHub.DirectoryOf(fromSessionId);
        if (!Directory.Exists(from))
        {
            return;
        }
        try
        {
            var to = SubagentHub.DirectoryOf(toSessionId);
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to copy subagent transcripts of {SessionId}", fromSessionId);
        }
    }

    public Task<McpServersDto> ListMcpServersAsync(string? cwd, CancellationToken cancellationToken = default) => Task.FromResult(mcp.List(cwd));

    public Task<McpToolsDto> ListMcpToolsAsync(string key, string? cwd, CancellationToken cancellationToken = default) =>
        mcp.ToolsAsync(key, cwd, cancellationToken);

    public Task<McpLogDto> GetMcpLogAsync(string key, string? cwd, CancellationToken cancellationToken = default) => Task.FromResult(mcp.Log(key, cwd));

    public Task<McpTestResult> TestMcpServerAsync(McpTestRequest request, CancellationToken cancellationToken = default) =>
        mcp.TestAsync(request, cancellationToken);

    public Task RestartMcpServerAsync(string key, string? cwd, bool login, CancellationToken cancellationToken = default) =>
        mcp.RestartAsync(key, cwd, login);

    public Task LogoutMcpServerAsync(string key, string? cwd, CancellationToken cancellationToken = default) => mcp.LogoutAsync(key, cwd);

    public Task<McpCatalogDto> GetMcpCatalogAsync(string? cwd, CancellationToken cancellationToken = default) => mcp.CatalogAsync(cwd, cancellationToken);

    public Task<FileSearchResultDto> SearchFilesAsync(FileSearchRequest request, CancellationToken cancellationToken = default) =>
        files.SearchAsync(request, cancellationToken);

    public Task<RevertFileResult> RevertFileAsync(RevertFileRequest request, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(request.Path))
        {
            return Task.FromResult(new RevertFileResult($"Path must be absolute: {request.Path}"));
        }
        var error = TKCode.Agent.Coding.Tools.Files.FileRevert.Revert(request.Path, request.ExpectedSha256, request.RestoreText);
        return Task.FromResult(new RevertFileResult(error));
    }

    public Task<AgentAssetsDto> ListAssetsAsync(string? cwd, CancellationToken cancellationToken = default)
    {
        var catalog = sessions.Assets.Get(cwd);
        var root = cwd is not null && Directory.Exists(cwd) ? TKCode.Agent.Coding.Assets.AgentAssetLoader.ProjectRoot(cwd) : null;
        return Task.FromResult(new AgentAssetsDto(root,
            catalog.Commands.Select(ToDto).ToList(),
            catalog.Skills.Select(ToDto).ToList(),
            catalog.Roles.Select(ToDto).ToList(),
            catalog.Shadowed.Select(s => new AgentAssetShadowDto(ToDto(s.Hidden), ToDto(s.By))).ToList(),
            catalog.Problems.Select(p => new AgentAssetProblemDto(p.Path, p.Message)).ToList()));
    }

    private static AgentAssetDto ToDto(TKCode.Agent.Coding.Assets.AgentAsset a) => new(
        a.Kind.ToString().ToLowerInvariant(), a.Name, a.Description, a.Source.ToString().ToLowerInvariant(), a.Path, a.DisplayName, a.Tools, a.Model, a.Icon);

    public Task TrustMcpServerAsync(string key, string cwd, bool trusted, CancellationToken cancellationToken = default)
    {
        mcp.Trust(key, cwd, trusted);
        return Task.CompletedTask;
    }
}
