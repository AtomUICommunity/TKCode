using Avalonia.Controls;

namespace TKCode.Desktop.Views;

/// <summary>
/// 用户消息气泡：超过 <see cref="ViewModels.UserMessageItemViewModel.CollapsedLineLimit"/> 行时默认收起，点击展开显示全部。
/// 行数按实际排版计算（自动换行也算），短消息不显示按钮；收起 / 展开由 Arc 的 ExpandableContent 完成。
/// </summary>
public partial class UserMessageBubble : UserControl
{
    public UserMessageBubble()
    {
        InitializeComponent();
    }
}
