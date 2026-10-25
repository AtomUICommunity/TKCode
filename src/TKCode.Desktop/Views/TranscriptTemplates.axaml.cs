using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TKCode.Desktop.Controls;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

/// <summary>会话转录的数据模板（主窗口与子 Agent 标签共用）。每个会话视图各用一份实例。</summary>
public partial class TranscriptTemplates : DataTemplates
{
    public TranscriptTemplates()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnMessageImageTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Visual anchor && (sender as Control)?.DataContext is ImageAttachmentViewModel image)
        {
            ImageOpener.Open(anchor, image);
        }
    }

    private async void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AssistantMessageItemViewModel message && TopLevel.GetTopLevel(sender as Visual)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(message.CopyText);
        }
    }
}
