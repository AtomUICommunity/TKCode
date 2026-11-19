using System.Globalization;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Services;

/// <summary>diff 的解析结果；行数超过上限时 <see cref="Truncated"/> 为 true，最后一行是 “…” 。</summary>
public sealed record DiffParseResult(IReadOnlyList<DiffLineViewModel> Lines, bool Truncated);

/// <summary>
/// unified diff 的解析：跳过文件头（<c>--- </c> / <c>+++ </c>）、空行与 <c>\ No newline at end of file</c>，
/// 按块头 <c>@@ -a,b +c,d @@</c> 推算每行的新旧行号；最多 <c>maxLines</c> 行，超出时追加一行 “…” 并标记截断。
/// 工具详情（<see cref="ParsedToolDetails"/>）与历史面板共用。
/// </summary>
public static class UnifiedDiffParser
{
    /// <summary>默认上限：超长的改动只显示开头（完整内容仍在文件里）。</summary>
    public const int DefaultMaxLines = 400;

    private const string NoNewlineMarker = "\\ No newline at end of file";

    public static DiffParseResult Parse(string diff, int maxLines = DefaultMaxLines)
    {
        var lines = new List<DiffLineViewModel>();
        int oldNumber = 0, newNumber = 0;
        var truncated = false;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                continue;
            }
            if (line.Length == 0 || line.TrimEnd('\r') == NoNewlineMarker)
            {
                continue;
            }
            if (lines.Count >= maxLines)
            {
                lines.Add(new DiffLineViewModel("…", DiffLineKind.Hunk));
                truncated = true;
                break;
            }
            if (line[0] == '@')
            {
                (oldNumber, newNumber) = HunkStart(line);
                lines.Add(new DiffLineViewModel(line, DiffLineKind.Hunk));
                continue;
            }
            // 首字符（+ / - / 空格）单独放在符号列，文字只保留行内容
            var text = line[1..];
            lines.Add(line[0] switch
            {
                '+' => new DiffLineViewModel(text, DiffLineKind.Added) { NewNumber = newNumber++ },
                '-' => new DiffLineViewModel(text, DiffLineKind.Removed) { OldNumber = oldNumber++ },
                _ => new DiffLineViewModel(text, DiffLineKind.Context) { OldNumber = oldNumber++, NewNumber = newNumber++ },
            });
        }
        return new DiffParseResult(lines, truncated);
    }

    /// <summary>“@@ -12,4 +12,5 @@”的起始行号；无法解析时返回 (0, 0)。</summary>
    private static (int Old, int New) HunkStart(string header)
    {
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return (0, 0);
        }
        return (Start(parts[1]), Start(parts[2]));

        static int Start(string range) =>
            int.TryParse(range.AsSpan(1).ToString().Split(',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
