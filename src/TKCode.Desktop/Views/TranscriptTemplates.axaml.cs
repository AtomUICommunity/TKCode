using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using StreamMark.Avalonia.Transcript;
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

    /// <summary>助手消息底栏「回到提问」：滚动到这一轮的用户消息。</summary>
    private void OnScrollToPromptClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AssistantMessageItemViewModel message ||
            (sender as Visual)?.FindAncestorOfType<TranscriptView>() is not { } view)
        {
            return;
        }
        var entries = view.Entries;
        var index = -1;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is ContentEntry content && ReferenceEquals(content.Content, message))
            {
                index = i;
                break;
            }
        }
        for (var i = index - 1; i >= 0; i--)
        {
            if (entries[i] is ContentEntry { Content: UserMessageItemViewModel prompt })
            {
                TranscriptNavigation.ScrollToItem(view, prompt);
                return;
            }
        }
    }
}
