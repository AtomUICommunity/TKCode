using System.Text.Json;
using System.Text.Json.Serialization;

namespace TKCode.Runtimes;

/// <summary>
/// 运行时的磁盘缓存（例如通过 CLI 命令或真实 ACP 会话发现的模型列表，缓存后下次启动即可在选择器中显示）。
/// 以运行时 id 为文件名，内容带上可执行文件路径与版本，任一变化即视为失效。
/// </summary>
public sealed class RuntimeCache(string directory)
{
    public T? Read<T>(string runtimeId, string executablePath, string? version, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            var file = PathOf(runtimeId);
            if (!File.Exists(file))
            {
                return null;
            }
            var entry = JsonSerializer.Deserialize(File.ReadAllText(file), RuntimeCacheJsonContext.Default.RuntimeCacheEntry);
            return entry is not null && entry.ExecutablePath == executablePath && entry.Version == version
                ? entry.Payload.Deserialize(info)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write<T>(string runtimeId, string executablePath, string? version, T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var entry = new RuntimeCacheEntry(executablePath, version, JsonSerializer.SerializeToElement(payload, info));
            var file = PathOf(runtimeId);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, RuntimeCacheJsonContext.Default.RuntimeCacheEntry));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存只是优化
        }
    }

    private string PathOf(string runtimeId) =>
        Path.Combine(directory, string.Concat(runtimeId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".json");
}

public sealed record RuntimeCacheEntry(string ExecutablePath, string? Version, JsonElement Payload);

/// <summary>缓存的模型列表（运行时无关）。</summary>
public sealed record CachedModel(string Id, string Name, string? Description, IReadOnlyList<string>? ReasoningEfforts);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RuntimeCacheEntry))]
[JsonSerializable(typeof(IReadOnlyList<CachedModel>))]
public sealed partial class RuntimeCacheJsonContext : JsonSerializerContext;
