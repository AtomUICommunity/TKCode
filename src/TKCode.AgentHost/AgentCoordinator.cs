using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using TKCode.Agent;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Sessions;
using TKCode.Agent.Coding.Tools.Agents;
using TKCode.AgentHost.Peers;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;

namespace TKCode.AgentHost;

/// <summary>
/// 子 Agent 与跨会话消息（docs/design/builtin-agent-tools.md §9）：
/// 子 Agent 是不出现在会话列表里的内置 Agent 会话，与父会话共享工作区、后台任务、任务清单、审批记录与权限档位，
/// 审批以父会话的名义转给 UI。子 Agent 结束后保留到父会话关闭，可以继续通过 send_message 追问。
/// </summary>
public sealed class AgentCoordinator(SessionManager sessions, PeerDirectory peers, ILogger logger) : IAgentCoordinator, IPeerHandler
{
    private readonly ConcurrentDictionary<string, Child> _children = new(StringComparer.Ordinal);

    private sealed class Child(string id, string name, string type, string parentId, AgentSession session)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Type { get; } = type;
        public string ParentId { get; } = parentId;
        public AgentSession Session { get; } = session;
        public int ToolUses;
        public long Tokens;
        public string Status = "running";

        /// <summary>派生时的第一次运行由 agent 工具等待；之后由消息引起的运行结束时通知父会话。</summary>
        public TaskCompletionSource<SubagentOutcome>? SpawnRun;
        public IDisposable? Subscription;
    }

    public static string RefOf(string sessionId) => sessionId.Length > 8 ? sessionId[..8] : sessionId;

    public async Task<SubagentHandle> SpawnAsync(SubagentRequest request, CancellationToken cancellationToken)
    {
        if (_children.ContainsKey(request.ParentSessionId))
        {
            throw new InvalidOperationException("Subagents cannot launch further subagents.");
        }
        var parent = sessions.Get(request.ParentSessionId);
        var model = request.Model is { Length: > 0 } modelRef
            ? sessions.FindBuiltinModel(modelRef) ?? throw new InvalidOperationException($"Model '{modelRef}' is not available for subagents.")
            : sessions.FastModelFor(parent.Agent.Model);
        if (parent.Tools is null)
        {
            throw new InvalidOperationException("Subagents are only available in built-in agent sessions.");
        }

        var name = UniqueName(request.ParentSessionId, request.Name is { Length: > 0 } n ? Slug(n) : request.AgentType);
        var file = Path.Combine(TKCodePaths.Home, "subagents", request.ParentSessionId, $"{DateTime.UtcNow:yyyyMMddTHHmmss}_{name}.jsonl");
        var store = SessionStore.CreateAt(file, parent.Cwd);
        var session = AgentSession.CreateBuiltin(sessions.Apis, model, store, sessions.GetApiKey, new BuiltinOptions
        {
            Workspace = parent.Workspace,
            BackgroundTasks = parent.Tools.BackgroundTasks,
            Tasks = parent.Tools.Tasks,
            Approvals = parent.Approvals,
            ToolSet = SubagentTypes.ToolSetOf(request.AgentType),
            ExtraPromptSections = SubagentTypes.PromptOf(request.AgentType, sessions.NameOf(parent)),
            Services = sessions.ServicesFor(parent.Cwd) with { UserInput = null, PlanMode = null },   // 子 Agent 不向用户提问，也没有 Plan 模式
            AutoCompact = parent.Agent is TKCode.Agent.Agent { AutoCompact: var autoCompact } ? autoCompact : true,
            CacheTtl = sessions.CacheTtl,
            ShellProfile = sessions.ShellProfile,
        });
        // 子 Agent 沿用父会话的上下文预算（自动压缩阈值据此计算）
        session.ContextWindow = parent.ContextWindow > 0 ? Math.Min(parent.ContextWindow, model.ContextWindow) : model.ContextWindow;
        session.Agent.PermissionMode = parent.Agent.PermissionMode;
        session.Agent.ThinkingLevel = SessionOptions.Nearest(ThinkingLevels.Available(model), parent.Agent.ThinkingLevel);

        var child = new Child(store.SessionId, name, request.AgentType, request.ParentSessionId, session)
        {
            SpawnRun = new TaskCompletionSource<SubagentOutcome>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var handle = new SubagentHandle(child.Id, name, request.AgentType, child.SpawnRun.Task, () =>
        {
            logger.LogWarning("Subagent {Name} abort requested by its parent's task controls", name);
            session.Agent.Abort();
        }, model.Name);
        if (session.Agent is Agent.Agent builtin)
        {
            builtin.BeforeToolCall = (ctx, ct) => sessions.ApproveAsync(request.ParentSessionId, session, ctx, ct, name);
        }
        var transcript = sessions.Subagents.AttachBuiltin(request.ParentSessionId, request.ToolCallId, session, name, model.Name);
        child.Subscription = session.Agent.Subscribe(async (e, _) =>
        {
            await OnChildEvent(child, handle, e);
            await sessions.Subagents.OnEventAsync(transcript, e);
        });
        _children[child.Id] = child;
        logger.LogInformation("Subagent {Name} ({Type}) started for session {Parent}", name, request.AgentType, request.ParentSessionId);

        session.Prompt(request.Prompt);
        await Task.Yield();
        return handle;
    }

    private ValueTask OnChildEvent(Child child, SubagentHandle handle, AgentEvent e)
    {
        switch (e)
        {
            case AgentStartEvent:
                child.Status = "running";
                break;
            case ToolExecutionStartEvent tool:
                Interlocked.Increment(ref child.ToolUses);
                handle.ReportProgress(child.ToolUses, child.Tokens, Activity(tool.ToolName, tool.ArgumentsJson));
                break;
            case MessageEndEvent { Message: AssistantMessage assistant }:
                Interlocked.Add(ref child.Tokens, assistant.Usage.Total);
                handle.ReportProgress(child.ToolUses, child.Tokens, null);
                break;
            case AgentEndEvent end:
                var last = child.Session.Agent.Messages.OfType<AssistantMessage>().LastOrDefault();
                var status = last?.StopReason switch
                {
                    StopReason.Aborted => "stopped",
                    StopReason.Error => "failed",
                    _ => "completed",
                };
                child.Status = status;
                logger.LogInformation("Subagent {Name} run ended: {Status}, {ToolUses} tool uses, {Tokens} tokens, stopReason {StopReason}, error {Error}",
                    child.Name, status, child.ToolUses, child.Tokens, last?.StopReason, last?.ErrorMessage);
                var report = last is null ? "" : string.Concat(last.Content.OfType<TextContent>().Select(t => t.Text));
                if (status == "failed" && last?.ErrorMessage is { } error)
                {
                    report = report.Length > 0 ? $"{report}\n\nError: {error}" : $"Error: {error}";
                }
                var outcome = new SubagentOutcome(status, report, child.ToolUses, child.Tokens);
                if (Interlocked.Exchange(ref child.SpawnRun, null) is { } spawn)
                {
                    spawn.TrySetResult(outcome);
                }
                else if (end.NewMessages.Count > 0)
                {
                    NotifyParent(child, outcome);
                }
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>由消息引起的后续运行结束：把子 Agent 的回复交给父会话。</summary>
    private void NotifyParent(Child child, SubagentOutcome outcome)
    {
        if (sessions.TryGet(child.ParentId) is not { } parent)
        {
            return;
        }
        var text = new StringBuilder("<task-notification>\n")
            .Append("<type>agent</type>\n")
            .Append("<agent>").Append(child.Name).Append("</agent>\n")
            .Append("<status>").Append(outcome.Status).Append("</status>\n")
            .Append("<result>\n").Append(outcome.Report.Trim()).Append("\n</result>\n")
            .Append("</task-notification>\n")
            .Append("This is an automated notification: subagent \"").Append(child.Name).Append("\" finished handling a message. It is not from the user.");
        parent.Deliver(new UserMessage([new TextContent(text.ToString())]) { Source = "task-notification" });
    }

    public async Task<IReadOnlyList<AgentPeer>> ListAsync(string callerSessionId, CancellationToken cancellationToken)
    {
        var result = new List<AgentPeer>();
        if (_children.TryGetValue(callerSessionId, out var self))
        {
            if (sessions.TryGet(self.ParentId) is { } parent)
            {
                result.Add(new AgentPeer("main", RefOf(parent.Id), "parent", parent.Agent.IsRunning ? "busy" : "idle", parent.Cwd));
            }
            result.AddRange(_children.Values.Where(c => c.ParentId == self.ParentId && c.Id != self.Id).Select(ToPeer));
            return result;
        }
        result.AddRange(_children.Values.Where(c => c.ParentId == callerSessionId).Select(ToPeer));
        result.AddRange(sessions.TopLevel().Where(s => s.Id != callerSessionId)
            .Select(s => new AgentPeer(sessions.NameOf(s), RefOf(s.Id), "session", s.Agent.IsRunning ? "busy" : "idle", s.Cwd, SessionIndex.TitleOf(s.Agent.Messages))));
        foreach (var peer in await peers.ListPeersAsync(cancellationToken))
        {
            result.Add(new AgentPeer(peer.Session.Name, peer.Session.Ref, $"session in another TKCode window (pid {peer.ProcessId})",
                peer.Session.Busy ? "busy" : "idle", peer.Session.Cwd, peer.Session.Title));
        }
        return result;
    }

    private static AgentPeer ToPeer(Child child) =>
        new(child.Name, RefOf(child.Id), $"subagent ({child.Type})", child.Session.Agent.IsRunning ? "busy" : child.Status);

    public async Task<string> SendAsync(string callerSessionId, string to, string message, string? summary, CancellationToken cancellationToken)
    {
        var (name, reference) = ParseAddress(to);
        var callerName = _children.TryGetValue(callerSessionId, out var self) ? self.Name : sessions.NameOf(sessions.Get(callerSessionId));
        var callerRef = RefOf(callerSessionId);

        // 1. 本 Host 内：父会话、子 Agent、其他会话。
        var local = ResolveLocal(callerSessionId, name, reference);
        if (local.Count > 1)
        {
            throw new InvalidOperationException($"'{to}' matches several agents ({string.Join(", ", local.Select(l => $"{l.Name} [{RefOf(l.Session.Id)}]"))}); append the [ref].");
        }
        if (local.Count == 1)
        {
            if (local[0].Session.Id == callerSessionId)
            {
                throw new InvalidOperationException("You cannot send a message to yourself.");
            }
            return Deliver(local[0].Session, local[0].Name, callerName, callerRef, message);
        }

        // 2. 本机其他 Host。
        var remote = (await peers.ListPeersAsync(cancellationToken))
            .Where(p => string.Equals(p.Session.Name, name, StringComparison.OrdinalIgnoreCase)
                        && (reference is null || p.Session.Ref.StartsWith(reference, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (remote.Count > 1)
        {
            throw new InvalidOperationException($"'{to}' matches several sessions in other TKCode windows; append the [ref].");
        }
        if (remote.Count == 1)
        {
            return await peers.SendAsync(remote[0], new PeerSendRequest(remote[0].Session.Ref, callerName, callerRef, message), cancellationToken);
        }
        throw new KeyNotFoundException($"No agent named '{to}'. Use list_agents to see who you can message.");
    }

    private List<(string Name, AgentSession Session)> ResolveLocal(string callerSessionId, string name, string? reference)
    {
        var candidates = new List<(string Name, AgentSession Session)>();
        if (_children.TryGetValue(callerSessionId, out var self) && name.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            if (sessions.TryGet(self.ParentId) is { } parent)
            {
                candidates.Add(("main", parent));
            }
            return candidates;
        }
        candidates.AddRange(_children.Values
            .Where(c => c.ParentId == callerSessionId || (self is not null && c.ParentId == self.ParentId))
            .Select(c => (c.Name, c.Session)));
        candidates.AddRange(sessions.TopLevel().Select(s => (sessions.NameOf(s), s)));
        return candidates
            .Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) || RefOf(c.Session.Id).Equals(name, StringComparison.OrdinalIgnoreCase))
            .Where(c => reference is null || c.Session.Id.StartsWith(reference, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Deliver(AgentSession target, string targetName, string fromName, string fromRef, string message)
    {
        var text = $"<session-message from=\"{Escape(fromName)}\" from-ref=\"{fromRef}\">\n{message}\n</session-message>\n" +
                   "This message was sent by another agent session, not by the user. To reply, use send_message with to set to the from attribute.";
        var busy = target.Agent.IsRunning;
        target.Deliver(new UserMessage([new TextContent(text)]) { Source = "session-message" });
        return busy
            ? $"Message delivered to {targetName}; it is busy and will see the message after its current step."
            : $"Message delivered to {targetName}; it was idle and has started working on it.";
    }

    // ---------------- 跨进程（IPeerHandler） ----------------

    public IReadOnlyList<PeerSessionDto> ListLocal() =>
        sessions.TopLevel()
            .Select(s => new PeerSessionDto(sessions.NameOf(s), RefOf(s.Id), s.Cwd, SessionIndex.TitleOf(s.Agent.Messages), s.Agent.IsRunning))
            .ToArray();

    public string DeliverLocal(PeerSendRequest request)
    {
        var target = sessions.TopLevel().FirstOrDefault(s => RefOf(s.Id).Equals(request.To, StringComparison.OrdinalIgnoreCase))
                     ?? throw new KeyNotFoundException($"Session [{request.To}] is not open in this TKCode window.");
        return Deliver(target, sessions.NameOf(target), request.FromName, request.FromRef, request.Message);
    }

    /// <summary>父会话关闭时一并关闭它的子 Agent。</summary>
    public async Task CloseChildrenAsync(string parentSessionId)
    {
        foreach (var child in _children.Values.Where(c => c.ParentId == parentSessionId).ToArray())
        {
            _children.TryRemove(child.Id, out _);
            logger.LogWarning("Subagent {Name} closed with parent session {Parent} (running: {Running}, {ToolUses} tool uses)",
                child.Name, parentSessionId, child.Session.Agent.IsRunning, child.ToolUses);
            child.Subscription?.Dispose();
            Interlocked.Exchange(ref child.SpawnRun, null)?.TrySetResult(new SubagentOutcome("stopped", "The parent session was closed.", child.ToolUses, child.Tokens));
            await child.Session.DisposeAsync();
        }
    }

    public bool IsSubagent(string sessionId) => _children.ContainsKey(sessionId);

    public bool HasRunningChildren(string parentSessionId) =>
        _children.Values.Any(c => c.ParentId == parentSessionId && c.Session.Agent.IsRunning);

    private string UniqueName(string parentId, string baseName)
    {
        var taken = _children.Values.Where(c => c.ParentId == parentId).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName) && !baseName.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            return baseName;
        }
        for (var i = 2; ; i++)
        {
            if (!taken.Contains($"{baseName}-{i}"))
            {
                return $"{baseName}-{i}";
            }
        }
    }

    /// <summary>"name [ref]" → (name, ref)。</summary>
    private static (string Name, string? Reference) ParseAddress(string to)
    {
        var trimmed = to.Trim();
        var open = trimmed.LastIndexOf('[');
        if (open > 0 && trimmed.EndsWith(']'))
        {
            return (trimmed[..open].Trim(), trimmed[(open + 1)..^1].Trim());
        }
        return (trimmed, null);
    }

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        }
        var slug = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "agent" : slug.Length > 40 ? slug[..40] : slug;
    }

    private static string? Activity(string tool, string argumentsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            foreach (var key in (string[])["description", "path", "pattern", "command", "url", "query", "to"])
            {
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var text = value.GetString()!.Split('\n')[0];
                    return $"{tool}: {(text.Length > 80 ? text[..80] + "…" : text)}";
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return tool;
    }

    private static string Escape(string value) => value.Replace("\"", "&quot;", StringComparison.Ordinal);
}
