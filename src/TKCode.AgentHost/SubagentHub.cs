using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TKCode.Agent;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Sessions;
using TKCode.Ai.Messages;
using TKCode.Rpc;
using TKCode.Runtimes;

namespace TKCode.AgentHost;

/// <summary>
/// 子 Agent 的转录（docs/design/subagent-view.md §5）：按（父会话，派生它的工具调用）登记，三种运行时共用。
/// 只有被 UI 打开的转录才转发事件；打开时在同一把锁内取历史快照与进行中的事件（Replay），保证不重不漏。
/// </summary>
public sealed class SubagentHub(Func<IAgentHostClient?> client, ILogger logger)
{
    private readonly ConcurrentDictionary<(string Parent, string Key), Transcript> _transcripts = new();

    /// <summary>~/.tkcode/subagents/&lt;父会话 id&gt;/：子 Agent 的转录与 index.jsonl。</summary>
    public static string DirectoryOf(string parentSessionId) => Path.Combine(TKCodePaths.Home, "subagents", parentSessionId);

    public sealed class Transcript
    {
        internal Transcript(string parentSessionId, string key, string name, string? model, SessionStore? store)
        {
            ParentSessionId = parentSessionId;
            Key = key;
            Name = name;
            Model = model;
            Store = store;
        }

        public string ParentSessionId { get; }

        /// <summary>运行时内部的子 Agent 键：内置为子会话 id，Claude 为 parent_tool_use_id，Codex 为子 thread id。</summary>
        public string Key { get; }

        public string Name { get; internal set; }
        public string? Model { get; internal set; }
        public string? ToolCallId { get; internal set; }
        internal string FileName = "";

        /// <summary>外部运行时的转录由 Hub 写入；内置子 Agent 由它自己的会话写入（为 null）。</summary>
        internal SessionStore? Store { get; }

        internal readonly object Gate = new();

        /// <summary>已结束的消息：按 MessageEndEvent 累积（不读 Agent.Messages，避免与 Replay 在并发时重复）。</summary>
        internal readonly List<Message> Messages = [];
        internal readonly List<(string Owner, SessionEvent Event)> Replay = [];
        internal bool IsRunning;
        internal bool Watched;
        internal long Seq;

        /// <summary>外部运行时持有的写入入口（同一键只创建一次）。</summary>
        internal RuntimeSubagent? Runtime;
    }

    /// <summary>内置子 Agent：事件来自它自己的会话（持久化也由会话完成）。</summary>
    public Transcript AttachBuiltin(string parentSessionId, string? toolCallId, AgentSession session, string name, string? model)
    {
        var transcript = new Transcript(parentSessionId, session.Id, name, model, null);
        _transcripts[(parentSessionId, session.Id)] = transcript;
        if (toolCallId is not null)
        {
            Link(transcript, toolCallId, session.Store.FilePath);
        }
        return transcript;
    }

    /// <summary>外部运行时的子 Agent：Hub 累积消息并写入 ~/.tkcode/subagents/&lt;父会话&gt;/。已登记过同一键时返回原转录。</summary>
    public Transcript AttachExternal(string parentSessionId, string key, string cwd, string name, string? model)
    {
        return _transcripts.GetOrAdd((parentSessionId, key), _ =>
        {
            var file = Path.Combine(DirectoryOf(parentSessionId), $"{DateTime.UtcNow:yyyyMMddTHHmmss}_{Sanitize(name)}_{Sanitize(key)}.jsonl");
            return new Transcript(parentSessionId, key, name, model, SessionStore.CreateAt(file, cwd));
        });
    }

    /// <summary>外部运行时的子 Agent：写入器的事件进入这份转录。</summary>
    public RuntimeSubagent OpenExternal(string parentSessionId, string key, string cwd, string name, string? model)
    {
        var transcript = AttachExternal(parentSessionId, key, cwd, name, model);
        return transcript.Runtime ??= new RuntimeSubagent(e => OnEventAsync(transcript, e), toolCallId => Link(transcript, toolCallId),
            (n, m) => Describe(transcript, n, m));
    }

    public Transcript? Find(string parentSessionId, string key) => _transcripts.GetValueOrDefault((parentSessionId, key));

    /// <summary>把转录关联到父会话中派生它的工具调用，并写入索引（重启后按工具调用 id 查找）。</summary>
    public void Link(Transcript transcript, string toolCallId, string? filePath = null)
    {
        if (transcript.ToolCallId == toolCallId)
        {
            return;
        }
        transcript.ToolCallId = toolCallId;
        transcript.FileName = Path.GetFileName(filePath ?? transcript.Store?.FilePath ?? "");
        WriteIndex(transcript);
    }

    /// <summary>运行时报告的模型或名称晚于登记时（例如 Claude 的 resolvedModel）：已关联的转录重写一行索引（查找时取最后一行）。</summary>
    public void Describe(Transcript transcript, string? name, string? model)
    {
        var changed = false;
        if (name is { Length: > 0 } && name != transcript.Name)
        {
            transcript.Name = name;
            changed = true;
        }
        if (model is { Length: > 0 } && model != transcript.Model)
        {
            transcript.Model = model;
            changed = true;
        }
        if (changed && transcript.ToolCallId is not null)
        {
            WriteIndex(transcript);
        }
    }

    private void WriteIndex(Transcript transcript)
    {
        try
        {
            var dir = DirectoryOf(transcript.ParentSessionId);
            Directory.CreateDirectory(dir);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("toolCallId", transcript.ToolCallId);
                writer.WriteString("agentId", transcript.Key);
                writer.WriteString("file", transcript.FileName);
                writer.WriteString("name", transcript.Name);
                if (transcript.Model is { } model)
                {
                    writer.WriteString("model", model);
                }
                writer.WriteEndObject();
            }
            File.AppendAllText(Path.Combine(dir, IndexFile), Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to index subagent {Name}", transcript.Name);
        }
    }

    /// <summary>子 Agent 的事件：维护运行状态、历史与 Replay，被打开时转发给 UI。</summary>
    public ValueTask OnEventAsync(Transcript transcript, AgentEvent e)
    {
        Message? persist = null;
        lock (transcript.Gate)
        {
            switch (e)
            {
                case AgentStartEvent:
                    transcript.IsRunning = true;
                    break;
                case AgentEndEvent:
                    transcript.IsRunning = false;
                    transcript.Replay.Clear();
                    break;
                case MessageEndEvent end:
                    transcript.Messages.Add(end.Message);
                    if (transcript.Store is not null)
                    {
                        persist = end.Message;
                    }
                    // 消息已进入历史：它的进行中事件不再需要重放；工具结果同时结束该工具的执行事件
                    var owner = end.Message is ToolResultMessage result ? result.ToolCallId : end.MessageId;
                    transcript.Replay.RemoveAll(r => r.Owner == owner || r.Owner == end.MessageId);
                    break;
                case CompactionEndEvent:
                    transcript.Replay.RemoveAll(r => r.Owner == CompactionOwner);
                    break;
            }
            if (SessionManager.Map(e) is not { } mapped)
            {
                return Persist(transcript, persist);
            }
            if (e is not (AgentStartEvent or AgentEndEvent or MessageEndEvent or CompactionEndEvent) && OwnerOf(e) is { } key)
            {
                if (mapped is ToolExecutionUpdated)
                {
                    // 同一工具只保留最新一条进度
                    transcript.Replay.RemoveAll(r => r.Owner == key && r.Event is ToolExecutionUpdated);
                }
                transcript.Replay.Add((key, mapped));
            }
            if (transcript is { Watched: true, ToolCallId: { } toolCallId } && client() is { } ui)
            {
                try
                {
                    ui.OnSubagentEvent(new SubagentEventNotification(transcript.ParentSessionId, toolCallId, ++transcript.Seq, mapped));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to forward subagent event");
                }
            }
        }
        return Persist(transcript, persist);
    }

    private ValueTask Persist(Transcript transcript, Message? message)
    {
        if (message is null || transcript.Store is not { } store)
        {
            return ValueTask.CompletedTask;
        }
        return new ValueTask(PersistAsync(store, message));
    }

    private async Task PersistAsync(SessionStore store, Message message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            await store.AppendMessageAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist subagent message to {File}", store.FilePath);
        }
    }

    private const string CompactionOwner = "\0compaction";

    private static string? OwnerOf(AgentEvent e) => e switch
    {
        MessageStartEvent s => s.MessageId,
        MessageUpdateEvent u => u.MessageId,
        ToolExecutionStartEvent t => t.ToolCallId,
        ToolExecutionUpdateEvent t => t.ToolCallId,
        ToolExecutionEndEvent t => t.ToolCallId,
        CompactionStartEvent => CompactionOwner,
        _ => null,
    };

    /// <summary>
    /// 打开转录：内存中有则取快照并开始转发；否则从磁盘读取（不转发）。<paramref name="parentHistory"/> 用于旧转录（没有索引）的回退查找。
    /// </summary>
    public SubagentOpenResult Open(string parentSessionId, string toolCallId, Func<IReadOnlyList<Message>?> parentHistory,
        Func<IReadOnlyList<Message>, HistoryMessageDto[]> toHistory)
    {
        if (_transcripts.Values.FirstOrDefault(t => t.ParentSessionId == parentSessionId && t.ToolCallId == toolCallId) is { } live)
        {
            lock (live.Gate)
            {
                live.Watched = true;
                return new SubagentOpenResult(live.Key, live.Name, live.Model, toHistory(live.Messages),
                    live.Replay.Select(r => r.Event).ToArray(), live.IsRunning);
            }
        }
        var (file, agentId, name, model) = FindOnDisk(parentSessionId, toolCallId, parentHistory)
            ?? throw new KeyNotFoundException($"No transcript for subagent call '{toolCallId}'.");
        var snapshot = SessionStore.Read(file);
        return new SubagentOpenResult(agentId ?? snapshot.SessionId, name ?? Path.GetFileNameWithoutExtension(file), model,
            toHistory(snapshot.Messages), [], false);
    }

    public void Close(string parentSessionId, string toolCallId)
    {
        foreach (var transcript in _transcripts.Values.Where(t => t.ParentSessionId == parentSessionId && t.ToolCallId == toolCallId))
        {
            lock (transcript.Gate)
            {
                transcript.Watched = false;
            }
        }
    }

    /// <summary>父会话关闭：释放它的转录（文件保留）。</summary>
    public void Release(string parentSessionId)
    {
        foreach (var key in _transcripts.Keys.Where(k => k.Parent == parentSessionId).ToArray())
        {
            _transcripts.TryRemove(key, out _);
        }
    }

    private const string IndexFile = "index.jsonl";

    private (string File, string? AgentId, string? Name, string? Model)? FindOnDisk(string parentSessionId, string toolCallId,
        Func<IReadOnlyList<Message>?> parentHistory)
    {
        var dir = DirectoryOf(parentSessionId);
        if (!Directory.Exists(dir))
        {
            return null;
        }
        var index = Path.Combine(dir, IndexFile);
        if (File.Exists(index))
        {
            // 同一工具调用可能被索引多次（重新关联），取最后一条
            foreach (var line in File.ReadLines(index).Reverse())
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("toolCallId", out var id) && id.GetString() == toolCallId &&
                        root.TryGetProperty("file", out var f) && f.GetString() is { Length: > 0 } name && File.Exists(Path.Combine(dir, name)))
                    {
                        return (Path.Combine(dir, name), Str(root, "agentId"), Str(root, "name"), Str(root, "model"));
                    }
                }
                catch (JsonException)
                {
                    // 写到一半的行：跳过
                }
            }
        }
        // 旧的内置转录没有索引：父会话工具结果的 Details 里有 agentId，与文件头的 sessionId 对应
        var details = parentHistory()?.OfType<ToolResultMessage>().LastOrDefault(r => r.ToolCallId == toolCallId)?.Details;
        if (details is null)
        {
            return null;
        }
        string? agentId;
        try
        {
            using var doc = JsonDocument.Parse(details);
            agentId = Str(doc.RootElement, "agentId");
        }
        catch (JsonException)
        {
            return null;
        }
        if (agentId is null)
        {
            return null;
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            if (Path.GetFileName(file) != IndexFile && HeaderIdOf(file) == agentId)
            {
                return (file, agentId, null, null);
            }
        }
        return null;
    }

    private static string? HeaderIdOf(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            if (reader.ReadLine() is not { } first)
            {
                return null;
            }
            using var doc = JsonDocument.Parse(first);
            return Str(doc.RootElement, "sessionId");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var s = new string(chars);
        return s.Length > 48 ? s[..48] : s;
    }
}
