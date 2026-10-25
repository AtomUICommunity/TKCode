using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.Views;

/// <summary>
/// “添加厂商”的模板选择框（设置 › 模型与厂商），经 <see cref="ScaledModal"/> 显示。
/// 打开与选中后的关闭由视图模型的 IsPickingTemplate 驱动（<see cref="ProvidersSettingsView"/>）；
/// 用户用 Esc、点遮罩或关闭按钮取消时，<see cref="ShowAsync"/> 返回，由调用方把视图模型的状态还原。
/// </summary>
public partial class ProviderTemplateDialog : DialogSurface
{
    private readonly ScaledModal _modal;

    protected override Type StyleKeyOverride => typeof(DialogSurface);

    public ProviderTemplateDialog()
    {
        InitializeComponent();
        CloseButtonName = Strings.Get(TKCodeLangResourceKind.Close);
        _modal = new ScaledModal(this);
    }

    /// <summary>显示并聚焦搜索框；模态框关闭（任何原因）后完成。</summary>
    public async Task ShowAsync(Window owner)
    {
        _modal.Show(owner, SearchBox);
        await _modal.Completion;
    }

    /// <summary>选中模板（或页面离开）后由视图模型触发的关闭。</summary>
    public void Close() => _modal.Close();
}
