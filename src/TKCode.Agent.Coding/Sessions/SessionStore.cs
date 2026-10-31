using System.Text;
using System.Text.Json;
using TKCode.Agent.Compaction;
using TKCode.Agent.Coding.Prompt;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;

namespace TKCode.Agent.Coding.Sessions;

/// <summary>
/// 只追加的 JSONL 会话文件。文件在第一条消息写入时才创建；损坏的行在读取时跳过。
/// 默认位置：~/.tkcode/sessions/&lt;cwd 编码&gt;/&lt;时间戳&gt;_&lt;sessionId&gt;.jsonl
/// </summary>
public sealed class SessionStore
{
    public const int CurrentVersion = 1;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string? _leafId;
    private bool _created;
    /// <summary>手动命名（包括清空）或自动标题已经写入，后台生成不得覆盖。</summary>
    private bool _titleChanged;
    /// <summary>文件创建前追加的非消息记录（模型 / 参数变化），随第一条消息一起写入，避免空会话落盘。</summary>
    private readonly StringBuilder _pending = new();

    private SessionStore(string filePath, string sessionId, string cwd, string? projectId)
    {
        FilePath = filePath;
        SessionId = sessionId;
        Cwd = cwd;
        ProjectId = projectId;
    }

    public string FilePath { get; }
    public string SessionId { get; }
    public string Cwd { get; }

    /// <summary>会话所属的项目（写在会话头里，之后不变）；null 表示旧会话或不在项目中工作。</summary>
    public string? ProjectId { get; }

    /// <summary>只给本会话追加的文件夹（docs/design/projects.md §6.4）；随 <see cref="AppendDirectoriesAsync"/> 更新。</summary>
    public IReadOnlyList<string> SessionDirectories { get; private set; } = [];

    /// <summary>本会话从哪里分叉而来；不是分叉的会话为 null。</summary>
    public ForkedFromEntry? ForkedFrom { get; private set; }

    public static string DefaultRoot =>
        Path.Combine(TKCodePaths.Home, "sessions");

    public static SessionStore Create(string cwd, string? root = null, string? projectId = null)
    {
        var sessionId = Guid.CreateVersion7().ToString("N");
        var dir = Path.Combine(root ?? DefaultRoot, EncodeCwd(cwd));
        var file = Path.Combine(dir, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}_{sessionId}.jsonl");
        return new SessionStore(file, sessionId, cwd, projectId);
    }

    /// <summary>在指定文件创建新会话（子 Agent 的转录不放在会话目录下，不出现在会话列表中）。</summary>
    public static SessionStore CreateAt(string filePath, string cwd, string? projectId = null) =>
        new(Path.GetFullPath(filePath), Guid.CreateVersion7().ToString("N"), cwd, projectId);

    /// <summary>打开既有会话，返回存储与沿当前分支（根 → 叶）的消息。</summary>
    public static (SessionStore Store, IReadOnlyList<Message> Messages) Open(string filePath)
    {
        var (store, snapshot) = OpenSnapshot(filePath);
        return (store, snapshot.Messages);
    }

    /// <summary>打开既有会话，同时返回完整快照（外部运行时需要其中的原生会话 id）。</summary>
    public static (SessionStore Store, SessionSnapshot Snapshot) OpenSnapshot(string filePath, SessionSnapshot? snapshot = null)
    {
        snapshot ??= Read(filePath);
        return (new SessionStore(filePath, snapshot.SessionId, snapshot.Cwd, snapshot.ProjectId)
        {
            _leafId = snapshot.LeafId,
            _created = true,
            _titleChanged = snapshot.CustomTitle is not null,
            SessionDirectories = snapshot.SessionDirectories,
            ForkedFrom = snapshot.ForkedFrom,
        }, snapshot);
    }

    /// <summary>
    /// 只读解析会话文件：沿当前分支（根 → 叶）收集消息，以及分支上最后一次生效的模型与各项参数。
    /// 用于恢复会话；不会创建 <see cref="SessionStore"/>。
    /// </summary>
    public static SessionSnapshot Read(string filePath) => Read(filePath, summary: false);

    /// <summary>
    /// 会话列表用的快照：只解析用户消息，助手消息与工具结果（思考、工具输出、图片）跳过正文，
    /// 只保留它们在树中的位置。<see cref="SessionSnapshot.Messages"/> 因此只含用户消息。
    /// </summary>
    public static SessionSnapshot ReadSummary(string filePath) => Read(filePath, summary: true);

    private static SessionSnapshot Read(string filePath, bool summary)
    {
        SessionHeader? header = null;
        string? customTitle = null;
        var pinned = false;
        IReadOnlyList<string> directories = [];
        ForkedFromEntry? forkedFrom = null;
        var entries = new Dictionary<string, TreeEntry>();
        string? leaf = null;
        using var lines = new Utf8LineReader(filePath);
        while (lines.Next() is { } line)
        {
            SessionEntry? entry;
            try
            {
                entry = summary && SkippedEntry.TryRead(line.Span) is { } skipped
                    ? skipped
                    : JsonSerializer.Deserialize(line.Span, SessionJsonContext.Default.SessionEntry);
            }
            catch (JsonException)
            {
                continue;
            }
            switch (entry)
            {
                case SessionHeader h:
                    header = h;
                    break;
                case SessionMetaEntry meta:
                    customTitle = meta.Title is null ? customTitle : meta.Title.Length == 0 ? null : meta.Title;
                    pinned = meta.Pinned ?? pinned;
                    break;
                case SessionDirectoriesEntry d:
                    directories = d.Directories ?? [];
                    break;
                case ForkedFromEntry f:
                    forkedFrom = f;
                    break;
                case TreeEntry t:
                    entries[t.Id] = t;
                    leaf = t.Id;
                    break;
            }
        }
        if (header is null)
        {
            throw new InvalidDataException($"'{filePath}' is not a TKCode session file.");
        }

        var branch = new List<TreeEntry>();
        for (var id = leaf; id is not null && entries.TryGetValue(id, out var entry); id = entry.ParentId)
        {
            branch.Add(entry);
        }
        branch.Reverse();

        var messages = new List<Message>();
        var earlier = new List<Message>();
        CompactionEntry? compaction = null;
        ModelChangeEntry? model = null;
        ThinkingLevel? thinking = null;
        bool? fast = null;
        int? contextWindow = null;
        RuntimeSessionEntry? runtime = null;
        TKCode.Agent.PermissionMode? permission = null;
        string? role = null;
        IReadOnlyList<PromptSection>? initialPrompt = null;
        IReadOnlyList<PromptSection>? currentPrompt = null;
        IReadOnlyList<TKCode.Agent.Coding.Tools.TaskItem>? tasks = null;
        foreach (var entry in branch)
        {
            switch (entry)
            {
                case TasksEntry t:
                    tasks = t.Tasks;
                    break;
                case SystemPromptEntry p:
                    initialPrompt ??= p.Sections;
                    currentPrompt = p.Sections;
                    break;
                case MessageEntry m:
                    messages.Add(m.Message);
                    break;
                case CompactionEntry c:
                    // 压缩之前的消息只用于显示；之后的第一条系统提示记录是新的开头
                    earlier.AddRange(messages);
                    messages.Clear();
                    compaction = c;
                    initialPrompt = null;
                    break;
                case ModelChangeEntry c:
                    model = c;
                    break;
                case OptionsChangeEntry o:
                    thinking = o.ThinkingLevel ?? thinking;
                    fast = o.FastMode ?? fast;
                    contextWindow = o.ContextWindow ?? contextWindow;
                    permission = o.PermissionMode ?? permission;
                    role = o.Role ?? role;
                    break;
                case RuntimeSessionEntry r:
                    runtime = r;
                    break;
            }
        }
        return new SessionSnapshot(header.SessionId, header.Cwd, header.Timestamp, leaf, messages,
            model?.Provider, model?.ModelId, thinking, fast, contextWindow, runtime?.Runtime, runtime?.NativeSessionId, permission, customTitle, pinned)
        {
            InitialSystemPrompt = initialPrompt,
            SystemPrompt = currentPrompt,
            Tasks = tasks,
            Role = role,
            ProjectId = header.ProjectId,
            SessionDirectories = directories,
            ForkedFrom = forkedFrom,
            EarlierMessages = earlier,
            Compaction = compaction,
        };
    }

    public Task AppendMessageAsync(Message message, CancellationToken ct = default)
        => AppendTreeAsync(parent => new MessageEntry(NewEntryId(), parent, ImageAttachments.ForStorage(message)), ct);

    public Task AppendModelChangeAsync(string provider, string modelId, CancellationToken ct = default)
        => AppendTreeAsync(parent => new ModelChangeEntry(NewEntryId(), parent, provider, modelId), ct);

    public Task AppendOptionsChangeAsync(ThinkingLevel? thinkingLevel, bool? fastMode, int? contextWindow, CancellationToken ct = default,
        TKCode.Agent.PermissionMode? permissionMode = null, string? role = null)
        => AppendTreeAsync(parent => new OptionsChangeEntry(NewEntryId(), parent, thinkingLevel, fastMode, contextWindow, permissionMode, role), ct);

    public Task AppendSystemPromptAsync(IReadOnlyList<PromptSection> sections, CancellationToken ct = default)
        => AppendTreeAsync(parent => new SystemPromptEntry(NewEntryId(), parent, sections), ct);

    public Task AppendTasksAsync(IReadOnlyList<TKCode.Agent.Coding.Tools.TaskItem> tasks, CancellationToken ct = default)
        => AppendTreeAsync(parent => new TasksEntry(NewEntryId(), parent, tasks), ct);

    public Task AppendCompactionAsync(CompactionTrigger trigger, string summary, long preTokens, long postTokens, long durationMs,
        string? instructions, bool truncatedForRetry, CancellationToken ct = default)
        => AppendTreeAsync(parent => new CompactionEntry(NewEntryId(), parent, trigger, summary, preTokens, postTokens, durationMs)
        {
            Instructions = instructions,
            TruncatedForRetry = truncatedForRetry,
        }, ct);

    public Task AppendRuntimeSessionAsync(string runtime, string nativeSessionId, CancellationToken ct = default)
        => AppendTreeAsync(parent => new RuntimeSessionEntry(NewEntryId(), parent, runtime, nativeSessionId), ct);

    /// <summary>追加元数据（重命名 / 置顶）。文件尚未创建时随第一条消息写入。</summary>
    public Task AppendMetaAsync(string? title, bool? pinned, CancellationToken ct = default) =>
        AppendMetaCoreAsync(title, pinned, onlyIfUntitled: false, ct);

    /// <summary>仅在没有命名操作时保存自动标题；检查和写入共用同一把锁，保护生成期间发生的手动重命名。</summary>
    public Task<bool> TryAppendGeneratedTitleAsync(string title, CancellationToken ct = default) =>
        AppendMetaCoreAsync(title, null, onlyIfUntitled: true, ct);

    private async Task<bool> AppendMetaCoreAsync(string? title, bool? pinned, bool onlyIfUntitled, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (onlyIfUntitled && _titleChanged)
            {
                return false;
            }
            var line = Serialize(new SessionMetaEntry(title, pinned));
            if (!_created)
            {
                _pending.AppendLine(line);
            }
            else
            {
                await File.AppendAllTextAsync(FilePath, line + Environment.NewLine, ct);
            }
            _titleChanged |= title is not null;
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>记录只给本会话追加的文件夹（完整列表，最后一条生效）。文件尚未创建时随第一条消息写入。</summary>
    public async Task AppendDirectoriesAsync(IReadOnlyList<string> directories, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var line = Serialize(new SessionDirectoriesEntry(directories));
            SessionDirectories = directories;
            if (!_created)
            {
                _pending.AppendLine(line);
            }
            else
            {
                await File.AppendAllTextAsync(FilePath, line + Environment.NewLine, ct);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>记录本会话从哪里分叉而来。文件尚未创建时随第一条消息写入。</summary>
    public async Task AppendForkedFromAsync(ForkedFromEntry entry, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var line = Serialize(entry);
            ForkedFrom = entry;
            if (!_created)
            {
                _pending.AppendLine(line);
            }
            else
            {
                await File.AppendAllTextAsync(FilePath, line + Environment.NewLine, ct);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>对未在 Host 中打开的会话文件直接追加元数据。</summary>
    public static Task AppendMetaToFileAsync(string filePath, string? title, bool? pinned, CancellationToken ct = default) =>
        File.AppendAllTextAsync(filePath, Serialize(new SessionMetaEntry(title, pinned)) + Environment.NewLine, ct);

    private async Task AppendTreeAsync(Func<string?, TreeEntry> factory, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var entry = factory(_leafId);
            if (!_created && entry is not MessageEntry)
            {
                _pending.AppendLine(Serialize(entry));
                _leafId = entry.Id;
                return;
            }
            var builder = new StringBuilder();
            if (!_created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                builder.AppendLine(Serialize(new SessionHeader(SessionId, Cwd, CurrentVersion, ProjectId)));
                builder.Append(_pending);
            }
            builder.AppendLine(Serialize(entry));
            await File.AppendAllTextAsync(FilePath, builder.ToString(), ct);
            _created = true;
            _pending.Clear();
            _leafId = entry.Id;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string Serialize(SessionEntry entry) => JsonSerializer.Serialize(entry, SessionJsonContext.Default.SessionEntry);

    private static string NewEntryId() => Guid.NewGuid().ToString("N")[..8];

    private static string EncodeCwd(string cwd)
    {
        var chars = cwd.Select(c => c is '/' or '\\' or ':' ? '-' : c).ToArray();
        return "--" + new string(chars).Trim('-') + "--";
    }
}

/// <summary>会话文件的只读快照（当前分支）。</summary>
public sealed record SessionSnapshot(
    string SessionId,
    string Cwd,
    DateTimeOffset CreatedAt,
    string? LeafId,
    IReadOnlyList<Message> Messages,
    string? ModelProvider,
    string? ModelId,
    ThinkingLevel? ThinkingLevel,
    bool? FastMode,
    int? ContextWindow,
    string? Runtime = null,
    string? NativeSessionId = null,
    TKCode.Agent.PermissionMode? PermissionMode = null,
    string? CustomTitle = null,
    bool IsPinned = false)
{
    /// <summary>内置 Agent 会话开头的系统提示（旧会话为 null）。</summary>
    public IReadOnlyList<PromptSection>? InitialSystemPrompt { get; init; }

    /// <summary>当前生效的系统提示（含中途变化）。</summary>
    public IReadOnlyList<PromptSection>? SystemPrompt { get; init; }

    /// <summary>会话的 role 名（null 表示 agent）。</summary>
    public string? Role { get; init; }

    /// <summary>会话头里记录的项目（docs/design/projects.md §5.3）。</summary>
    public string? ProjectId { get; init; }

    /// <summary>只给本会话追加的文件夹（§6.4）。</summary>
    public IReadOnlyList<string> SessionDirectories { get; init; } = [];

    /// <summary>任务清单的最新快照。</summary>
    public IReadOnlyList<TKCode.Agent.Coding.Tools.TaskItem>? Tasks { get; init; }

    /// <summary>本会话从哪里分叉而来（docs/design/message-rewind.md）。</summary>
    public ForkedFromEntry? ForkedFrom { get; init; }

    /// <summary>最后一次压缩之前的消息（只用于界面回看与标题，不发给模型）；<see cref="Messages"/> 从压缩之后开始。</summary>
    public IReadOnlyList<Message> EarlierMessages { get; init; } = [];

    /// <summary>当前分支上最后一次压缩。</summary>
    public CompactionEntry? Compaction { get; init; }

    /// <summary>界面显示的完整历史：压缩之前与之后的消息。</summary>
    public IReadOnlyList<Message> AllMessages => EarlierMessages.Count == 0 ? Messages : [.. EarlierMessages, .. Messages];
}
