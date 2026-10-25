using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.Views;

/// <summary>
/// 跟随界面缩放的确认框（Arc <see cref="DialogSurface"/> + <see cref="ScaledModal"/>）：标题、正文、取消与确认两个按钮。
/// 不用 Arc 的 <c>Dialog.ConfirmAsync</c>：它的覆盖层不跟随 uiScale（docs/design/projects.md §9.3）。
/// </summary>
public static class ConfirmDialog
{
    /// <returns>点了确认按钮时为 true；取消、Esc、点遮罩或关闭窗口时为 false。</returns>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmText, bool danger = false)
    {
        var confirmed = false;
        var cancel = new Arc.Button { Variant = ButtonVariant.Secondary, Content = Strings.Get(TKCodeLangResourceKind.Cancel) };
        var confirm = new Arc.Button { Variant = danger ? ButtonVariant.Danger : ButtonVariant.Primary, Content = confirmText };
        var surface = new DialogSurface
        {
            Title = title,
            Size = ControlSize.Small,
            ShowCloseButton = true,
            CloseButtonName = Strings.Get(TKCodeLangResourceKind.Close),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            Actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, confirm } },
        };
        var modal = new ScaledModal(surface);
        cancel.Click += (_, _) => modal.Cancel();
        confirm.Click += (_, _) =>
        {
            confirmed = true;
            modal.Close();
        };
        modal.Show(owner, cancel);
        await modal.Completion;
        return confirmed;
    }
}
