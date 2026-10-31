using Avalonia.Controls;
using StreamMark.Avalonia.Extensions;
using TKCode.Desktop.Controls;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

/// <summary>子 Agent 标签：转录视图 + 适配器，跟随 DataContext（切换标签时同一视图可能换绑另一个子 Agent）。</summary>
public partial class SubagentTabView : UserControl
{
    private readonly TranscriptAdapter _adapter;

    public SubagentTabView()
    {
        InitializeComponent();
        Transcript.DataTemplates.AddRange(new TranscriptTemplates());
        _adapter = new TranscriptAdapter(Transcript.Entries);
        TranscriptContextMenu.Attach(Transcript, find: false);
        DataContextChanged += (_, _) =>
        {
            var tab = DataContext as SubagentTabViewModel;
            _adapter.Attach(tab?.Transcript);
            // 相对图片按父会话的工作目录解析（子 Agent 与父会话共用 cwd）
            MarkdownServices.SetImageBaseUri(Transcript, tab?.Transcript.Cwd);
            Transcript.ScrollToEnd();
        };
    }
}
