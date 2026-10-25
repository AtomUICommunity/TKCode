using System.ComponentModel;
using TKCode.Runtimes.Process;

namespace TKCode.Runtimes.Discovery;

/// <summary>在创建 ACP 会话前，用 OpenCode 的纯文本 models 命令发现模型。</summary>
internal static class OpenCodeModelDiscovery
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>失败或没有有效模型时返回 null，由调用方保留已有缓存。</summary>
    internal static async Task<IReadOnlyList<CachedModel>?> DiscoverAsync(string executable, ExecutableLocator locator,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var output = await CliProcess.RunToCompletionAsync(executable, ["models"], Timeout,
                cancellationToken, locator).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Parse(output);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>每行一个完整 provider/model ID；模型部分可以包含多个斜杠。</summary>
    internal static IReadOnlyList<CachedModel>? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var models = new List<CachedModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var id = line.Trim();
            if (IsModelId(id) && seen.Add(id))
            {
                models.Add(new CachedModel(id, id, null, null));
            }
        }
        return models.Count == 0 ? null : models;
    }

    private static bool IsModelId(string id)
    {
        var separator = id.IndexOf('/');
        if (separator <= 0 || separator == id.Length - 1 || id.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }
        // OpenCode 支持自定义 ID，不能限定为 ASCII 白名单；~namespace 等也是已有模型的合法别名。
        // 只过滤明显的日志 / JSON / 终端控制序列，保留提供商和模型的原始标识。
        if (id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '{' or '}' or '[' or ']' or '\\'))
        {
            return false;
        }
        return id.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }
}
