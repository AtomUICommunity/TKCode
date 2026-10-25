using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TKCode.Agent.Coding;
using TKCode.Agent.Coding.Sessions;
using TKCode.Ai.Messages;

namespace TKCode.AgentHost;

/// <summary>索引中的一个会话：列表需要的全部字段，按文件大小与修改时间判断是否需要重新解析。</summary>
public sealed record SessionIndexEntry(
    string FilePath,
    long Length,
    long LastWriteTicks,
    string SessionId,
    string Cwd,
    string? FirstMessageTitle,
    string? CustomTitle,
    bool Pinned,
    string? ModelRef,
    string? Runtime,
    string? NativeSessionId,
    string? ProjectId = null);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<SessionIndexEntry>))]
internal sealed partial class SessionIndexJsonContext : JsonSerializerContext;

/// <summary>
/// 会话列表的索引缓存（~/.tkcode/cache/session-index.json）。列出会话时只对文件做 stat，
/// 大小或修改时间变化的文件才重新解析，因此会话再多、单个会话再长，列表也不会变慢。
/// </summary>
public sealed class SessionIndex(ILogger<SessionIndex> logger)
{
    private const int MaxTitleLength = 80;
    private readonly Lock _gate = new();
    private Dictionary<string, SessionIndexEntry>? _entries;

    private static string IndexFile => Path.Combine(TKCodePaths.Home, "cache", "session-index.json");

    /// <summary>扫描会话目录、刷新变化的条目，返回全部会话（置顶在前，其余按最近修改倒序）。</summary>
    public IReadOnlyList<SessionIndexEntry> Refresh()
    {
        lock (_gate)
        {
            var entries = _entries ??= Load();
            var root = SessionStore.DefaultRoot;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var changed = false;
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    var file = new FileInfo(path);
                    seen.Add(file.FullName);
                    if (entries.TryGetValue(file.FullName, out var cached) &&
                        cached.Length == file.Length && cached.LastWriteTicks == file.LastWriteTimeUtc.Ticks)
                    {
                        continue;
                    }
                    if (Parse(file) is { } entry)
                    {
                        entries[file.FullName] = entry;
                    }
                    else
                    {
                        entries.Remove(file.FullName);
                    }
                    changed = true;
                }
            }
            foreach (var missing in entries.Keys.Where(k => !seen.Contains(k)).ToArray())
            {
                entries.Remove(missing);
                changed = true;
            }
            if (changed)
            {
                Save(entries);
            }
            return entries.Values
                .OrderByDescending(e => e.Pinned)
                .ThenByDescending(e => e.LastWriteTicks)
                .ToArray();
        }
    }

    /// <summary>删除文件后同步移除索引（下次 Refresh 也会发现）。</summary>
    public void Forget(string filePath)
    {
        lock (_gate)
        {
            if (_entries?.Remove(Path.GetFullPath(filePath)) == true)
            {
                Save(_entries);
            }
        }
    }

    private SessionIndexEntry? Parse(FileInfo file)
    {
        try
        {
            var snapshot = SessionStore.ReadSummary(file.FullName);
            var modelRef = snapshot.ModelId is null ? null
                : snapshot.ModelProvider is null ? snapshot.ModelId
                : $"{snapshot.ModelProvider}/{snapshot.ModelId}";
            return new SessionIndexEntry(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, snapshot.SessionId, snapshot.Cwd,
                TitleOf(snapshot.AllMessages), snapshot.CustomTitle, snapshot.IsPinned, modelRef, snapshot.Runtime, snapshot.NativeSessionId,
                snapshot.ProjectId);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Skipping unreadable session {File}", file.FullName);
            return null;
        }
    }

    /// <summary>会话标题：第一条用户输入（不含任务通知与其他会话的消息）的首行，过长时截断。</summary>
    public static string? TitleOf(IReadOnlyList<Message> messages)
    {
        var text = messages.OfType<UserMessage>().Where(u => u.Source is null).Select(u => string.Concat(u.Content.OfType<TextContent>().Select(t => t.Text))).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        // 图片引用「[Image #N: 路径]」只保留编号
        var line = ImageReference.Collapse(text.Trim()).Split('\n')[0].Trim();
        return line.Length > MaxTitleLength ? line[..MaxTitleLength] + "…" : line;
    }

    private Dictionary<string, SessionIndexEntry> Load()
    {
        try
        {
            if (File.Exists(IndexFile))
            {
                var list = JsonSerializer.Deserialize(File.ReadAllText(IndexFile), SessionIndexJsonContext.Default.ListSessionIndexEntry);
                if (list is not null)
                {
                    return list.Where(e => e.FilePath is not null).ToDictionary(e => e.FilePath, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Session index is unreadable; rebuilding");
        }
        return new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, SessionIndexEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IndexFile)!);
            var temp = IndexFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries.Values.ToList(), SessionIndexJsonContext.Default.ListSessionIndexEntry));
            File.Move(temp, IndexFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to save session index");
        }
    }
}

internal static partial class ImageReference
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\[Image #(\d+): [^\]\n]+\]")]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    public static string Collapse(string text) => Pattern().Replace(text, "[Image #$1]");
}
