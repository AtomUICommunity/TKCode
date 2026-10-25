using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TKCode.Arc;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

/// <summary>
/// 导出 MCP 服务器对话框（设置 › MCP 服务器），经 <see cref="ScaledModal"/> 显示。
/// 打开由视图模型的 Export 驱动（<see cref="McpSettingsView"/>）；用户取消时 <see cref="ShowAsync"/> 返回，由调用方把视图模型的状态还原。
/// </summary>
public partial class McpExportDialog : DialogSurface
{
    private readonly ScaledModal _modal;

    protected override Type StyleKeyOverride => typeof(DialogSurface);

    public McpExportDialog()
    {
        InitializeComponent();
        CloseButtonName = Strings.Get(TKCodeLangResourceKind.Close);
        _modal = new ScaledModal(this);
    }

    /// <summary>显示；模态框关闭（任何原因）后完成。</summary>
    public async Task ShowAsync(Window owner)
    {
        _modal.Show(owner);
        await _modal.Completion;
    }

    /// <summary>页面离开后由视图模型触发的关闭。</summary>
    public void Close() => _modal.Close();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is McpExportViewModel export && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(export.Json);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not McpExportViewModel export || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "mcp.json",
            DefaultExtension = "json",
        });
        if (file is null)
        {
            return;
        }
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(export.Json);
    }
}
