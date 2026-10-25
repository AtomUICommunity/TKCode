using Avalonia;
using Avalonia.Controls;
using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Rendering;
using StreamMark.Core.Model;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Controls;

/// <summary>Markdown 里用行内代码写出的文件路径：指向已存在的文件时可以点击，在右侧打开预览。</summary>
internal static class MarkdownCodePaths
{
    /// <summary>相对路径的基准目录：文件预览用文件所在目录，会话用工作目录。</summary>
    public static string? Directory(AvaloniaObject source, MainWindowViewModel vm) =>
        MarkdownServices.GetImageBaseUri(source) ?? vm.Conversation.Active?.Cwd ?? vm.Conversation.Workspace?.Primary;

    /// <summary>指针下的行内代码如果是一个已存在的文件，返回绝对路径。已经是链接的代码交给链接点击。</summary>
    public static string? Hit(TextSegmentView view, Point local, string? directory)
    {
        if (view.Layout is not { } layout)
        {
            return null;
        }
        var hit = layout.HitTestPoint(local);
        if (!hit.IsInside)
        {
            return null;
        }
        var content = view.Content;
        var span = SpanAt(content, hit.TextPosition) ?? (hit.IsTrailing ? SpanAt(content, hit.TextPosition - 1) : null);
        if (span is not { } found || found.LinkIndex >= 0 || (found.Style & InlineStyle.Code) == 0)
        {
            return null;
        }
        return MarkdownLinks.ResolveCodeSpan(content.Text.Substring(found.Start, found.Length), directory);
    }

    private static InlineSpan? SpanAt(InlineContent content, int index)
    {
        if (index < 0)
        {
            return null;
        }
        foreach (var span in content.Spans)
        {
            if (index >= span.Start && index < span.End)
            {
                return span;
            }
        }
        return null;
    }
}
