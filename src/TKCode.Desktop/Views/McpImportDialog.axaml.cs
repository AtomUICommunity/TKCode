using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TKCode.Arc;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

/// <summary>
/// 导入 MCP 服务器对话框（设置 › MCP 服务器），经 <see cref="ScaledModal"/> 显示。
/// 打开与导入成功后的关闭由视图模型的 Import 驱动（<see cref="McpSettingsView"/>）；
/// 用户用 Esc、点遮罩或关闭按钮取消时，<see cref="ShowAsync"/> 返回，由调用方把视图模型的状态还原。
/// </summary>
public partial class McpImportDialog : DialogSurface
{
    private readonly ScaledModal _modal;

    protected override Type StyleKeyOverride => typeof(DialogSurface);

    public McpImportDialog()
    {
        InitializeComponent();
        CloseButtonName = Strings.Get(TKCodeLangResourceKind.Close);
        _modal = new ScaledModal(this);
    }

    /// <summary>显示并聚焦粘贴框；模态框关闭（任何原因）后完成。</summary>
    public async Task ShowAsync(Window owner)
    {
        _modal.Show(owner, PasteBox);
        await _modal.Completion;
    }

    /// <summary>导入成功（或页面离开）后由视图模型触发的关闭。</summary>
    public void Close() => _modal.Close();

    /// <summary>选择一个配置文件（JSON / TOML），内容放入粘贴框并解析。</summary>
    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not McpImportViewModel import || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MCP config") { Patterns = ["*.json", "*.jsonc", "*.toml"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }
        try
        {
            import.LoadFile(path, await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            import.LoadFile(path, "");
        }
    }
}
