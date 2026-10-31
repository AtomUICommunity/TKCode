using Avalonia;
using Avalonia.Controls;
using StreamMark.Avalonia.Transcript;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 把会话视图滚动到某个模板条目（用户消息等）：⌘⌥↑/↓ 在提问之间跳转、查找用户消息时使用。
/// StreamMark 只能按 Markdown 块定位，模板条目只在进入视口附近时才实例化；这里先按条目序号估算位置，
/// 再按已实例化条目判断方向逐屏移动，直到目标实例化，最后把它对齐到视口顶部。
/// </summary>
public static class TranscriptNavigation
{
    private const double TopMargin = 16;
    private const int MaxSteps = 120;

    /// <summary>滚动到 <paramref name="item"/>（<see cref="ContentEntry.Content"/>）；找不到条目时返回 false。</summary>
    public static bool ScrollToItem(TranscriptView view, object item)
    {
        var entries = view.Entries;
        var target = IndexOf(entries, item);
        if (target < 0)
        {
            return false;
        }
        var panel = view.Panel;
        if (Realized(panel, item) is null)
        {
            // 初始估计：按条目序号的比例
            SetOffset(view, panel.Extent.Height * target / Math.Max(1, entries.Count) - panel.Viewport.Height * 0.3);
        }
        for (var step = 0; step < MaxSteps; step++)
        {
            view.UpdateLayout();
            if (Realized(panel, item) is not null)
            {
                // 对齐后，新实例化的行按实际高度重新排版，位置可能漂移：再校正几次直到稳定
                for (var settle = 0; settle < 6; settle++)
                {
                    if (Realized(panel, item) is not { } control || control.TranslatePoint(default, panel) is not { } origin)
                    {
                        break;
                    }
                    if (Math.Abs(origin.Y - TopMargin) < 1)
                    {
                        break;
                    }
                    var previous = view.Offset.Y;
                    SetOffset(view, previous + origin.Y - TopMargin);
                    view.UpdateLayout();
                    if (Math.Abs(view.Offset.Y - previous) < 0.5)
                    {
                        // 到头了（例如最后几条提问无法滚到顶部）
                        break;
                    }
                }
                return true;
            }
            var direction = Direction(panel, entries, target, view);
            if (direction == 0)
            {
                return false;
            }
            var before = view.Offset.Y;
            SetOffset(view, before + direction * Math.Max(48, panel.Viewport.Height * 0.8));
            if (Math.Abs(view.Offset.Y - before) < 0.5)
            {
                // 已经到头还没找到（估算高度偏差），下一轮再按新的实例化结果判断
                view.UpdateLayout();
                if (Realized(panel, item) is null)
                {
                    return false;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 跳到上一条（<paramref name="direction"/> &lt; 0）或下一条用户提问（⌘⌥↑ / ⌘⌥↓）。
    /// 参照点是视口里最靠上的模板条目：上一条 = 它之前最近的提问，下一条 = 它之后最近的提问。
    /// </summary>
    public static bool JumpToPrompt(TranscriptView view, int direction, Func<object, bool> isPrompt)
    {
        var entries = view.Entries;
        var reference = FirstVisible(view);
        if (direction < 0)
        {
            for (var i = Math.Min(reference, entries.Count) - 1; i >= 0; i--)
            {
                if (entries[i] is ContentEntry content && isPrompt(content.Content))
                {
                    return ScrollToItem(view, content.Content);
                }
            }
            return false;
        }
        for (var i = reference + 1; i < entries.Count; i++)
        {
            if (entries[i] is ContentEntry content && isPrompt(content.Content))
            {
                return ScrollToItem(view, content.Content);
            }
        }
        // 已经是最后一条提问：回到底部
        view.ScrollToEnd();
        return true;
    }

    /// <summary>视口里最靠上的模板条目（记住阅读位置用）；一个都没有时为 null。</summary>
    public static object? TopVisibleItem(TranscriptView view)
    {
        var entries = view.Entries;
        var best = int.MaxValue;
        foreach (var child in view.Panel.Children)
        {
            if (child.DataContext is { } context && child.Bounds.Bottom > 0 && child.Bounds.Top < view.Panel.Viewport.Height &&
                IndexOf(entries, context) is var index and >= 0 && index < best)
            {
                best = index;
            }
        }
        return best == int.MaxValue ? null : ((ContentEntry)entries[best]).Content;
    }

    /// <summary>视口里最靠上的模板条目的序号（顶部在视口上方一点点的也算）；没有时按滚动比例估计。</summary>
    private static int FirstVisible(TranscriptView view)
    {
        var entries = view.Entries;
        var panel = view.Panel;
        var best = int.MaxValue;
        foreach (var child in panel.Children)
        {
            if (child.DataContext is not { } context || child.Bounds.Bottom <= TopMargin + 1 || child.Bounds.Top >= panel.Viewport.Height)
            {
                continue;
            }
            var index = IndexOf(entries, context);
            if (index >= 0 && index < best)
            {
                best = index;
            }
        }
        if (best != int.MaxValue)
        {
            return best;
        }
        var fraction = panel.Extent.Height <= 0 ? 1 : view.Offset.Y / panel.Extent.Height;
        return (int)(fraction * entries.Count);
    }

    private static int IndexOf(IList<TranscriptEntry> entries, object item)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is ContentEntry content && ReferenceEquals(content.Content, item))
            {
                return i;
            }
        }
        return -1;
    }

    private static Control? Realized(TranscriptPanel panel, object item)
    {
        foreach (var child in panel.Children)
        {
            if (ReferenceEquals(child.DataContext, item) && child.IsVisible)
            {
                return child;
            }
        }
        return null;
    }

    /// <summary>目标在已实例化条目之前（-1）还是之后（1）。只看模板条目；一个都没有时按滚动位置的比例估计。</summary>
    private static int Direction(TranscriptPanel panel, IList<TranscriptEntry> entries, int target, TranscriptView view)
    {
        int min = int.MaxValue, max = -1;
        foreach (var child in panel.Children)
        {
            if (child.DataContext is { } context && IndexOf(entries, context) is var index and >= 0)
            {
                min = Math.Min(min, index);
                max = Math.Max(max, index);
            }
        }
        if (max >= 0)
        {
            return target < min ? -1 : target > max ? 1 : 0;
        }
        var fraction = panel.Extent.Height <= 0 ? 0 : view.Offset.Y / panel.Extent.Height;
        return (double)target / Math.Max(1, entries.Count) < fraction ? -1 : 1;
    }

    private static void SetOffset(TranscriptView view, double y)
    {
        var max = Math.Max(0, view.Panel.Extent.Height - view.Panel.Viewport.Height);
        view.Offset = new Vector(view.Offset.X, Math.Clamp(y, 0, max));
    }
}
