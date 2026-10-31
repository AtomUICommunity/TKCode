using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using StreamMark.Avalonia.Selection;
using StreamMark.Avalonia.Transcript;
using TKCode.Arc;
using TKCode.Desktop.Localization;
using ArcLucide = TKCode.Arc.Icons.Lucide;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 会话视图的右键菜单：复制、复制为 Markdown、全选、查找（docs/design/message-features-report.md R3）。
/// StreamMark 自带的菜单是 Avalonia 原生 MenuFlyout，Arc 主题没有它的模板，所以 <see cref="MarkdownSetup"/> 关掉了它；
/// 关掉时 StreamMark 在隧道阶段把右键请求标记为已处理，这里接收已处理的请求，改用 Arc 的菜单面板。
/// "查找"引发 StreamMark 的 <see cref="MarkdownContextMenu.FindRequestedEvent"/>，由宿主显示查找条。
/// </summary>
public static class TranscriptContextMenu
{
    /// <param name="find">是否提供"查找"（宿主有查找条时）。</param>
    public static void Attach(TranscriptView view, bool find)
    {
        var surface = new MenuSurface
        {
            Variant = MenuSurfaceVariant.Context,
            ClosesOnViewportChange = true,
        };
        view.AddHandler(Control.ContextRequestedEvent, (_, e) =>
        {
            // 有自己菜单或自己选择的控件（只读的输入框、普通可选文本）不接管
            if (OwnsContextMenu(e.Source as Visual, view))
            {
                return;
            }
            e.Handled = true;
            Populate(surface, view, find);
            if (e.TryGetPosition(view, out var point))
            {
                surface.OpenAt(view, point);
            }
            else
            {
                surface.OpenAt(view, new Point(12, 8), MenuOpenFocus.First);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        view.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (surface.IsOpen && e.GetCurrentPoint(view).Properties.IsLeftButtonPressed)
            {
                surface.Close(returnFocus: false);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        view.DetachedFromVisualTree += (_, _) => surface.Close(returnFocus: false);
    }

    private static bool OwnsContextMenu(Visual? source, TranscriptView view)
    {
        for (var visual = source; visual is not null && !ReferenceEquals(visual, view); visual = visual.GetVisualParent())
        {
            if (visual is TextBox or SelectableTextBlock or Composer.ComposerEditor)
            {
                return true;
            }
        }
        return false;
    }

    private static void Populate(MenuSurface surface, TranscriptView view, bool find)
    {
        var controller = view.SelectionController;
        var hasSelection = !view.Selection.IsEmpty;
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        MenuEntry Entry(TKCodeLangResourceKind text, ArcLucide.LucideIconKind icon, Action action, bool enabled = true, KeyGesture? gesture = null)
        {
            var entry = new MenuEntry
            {
                Header = Strings.Get(text),
                Icon = ArcLucide.Lucide.Geometry(icon),
                IsEnabled = enabled,
                InputGesture = gesture,
            };
            entry.Click += (_, _) => action();
            return entry;
        }

        surface.Items.Clear();
        surface.Items.Add(Entry(TKCodeLangResourceKind.MarkdownMenuCopy, ArcLucide.LucideIconKind.Copy,
            () => _ = controller.CopyAsync(), hasSelection, new KeyGesture(Key.C, modifier)));
        surface.Items.Add(Entry(TKCodeLangResourceKind.MarkdownMenuCopyAsMarkdown, ArcLucide.LucideIconKind.FileCode,
            () => _ = controller.CopyAsync(asMarkdown: true), hasSelection));
        var selectAll = Entry(TKCodeLangResourceKind.MarkdownMenuSelectAll, ArcLucide.LucideIconKind.TextSelect,
            controller.SelectAll, gesture: new KeyGesture(Key.A, modifier));
        selectAll.HasSeparatorBefore = true;
        surface.Items.Add(selectAll);
        if (find)
        {
            surface.Items.Add(Entry(TKCodeLangResourceKind.MarkdownMenuFind, ArcLucide.LucideIconKind.Search,
                () => view.Panel.RaiseEvent(new RoutedEventArgs(MarkdownContextMenu.FindRequestedEvent, view.Panel)),
                gesture: new KeyGesture(Key.F, modifier)));
        }
    }
}
