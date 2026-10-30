using TKCode.Arc.Localization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TKCode.Config;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly UserSettingsService userSettings;
    private readonly AgentHostConnection host;
    private readonly ILogger<SettingsViewModel> settingsLogger;

    public MainWindowViewModel(
        SidebarViewModel sidebar,
        ConversationViewModel conversation,
        WorkbenchViewModel workbench,
        ThemeService themes,
        UserSettingsService userSettings,
        AgentHostConnection host,
        UpdatesViewModel updates,
        ILogger<SettingsViewModel> settingsLogger,
        IExplorerFileOperations? fileOperations = null,
        IExplorerPlatformActions? platformActions = null)
    {
        Updates = updates;
        // 卡片的「查看更新说明」打开设置 › 关于
        updates.OpenAbout = async () =>
        {
            IsSettingsOpen = true;
            await Settings.OpenAsync(SettingsSection.About);
        };
        Sidebar = sidebar;
        Conversation = conversation;
        Workbench = workbench;
        workbench.Host = host;
        conversation.Files.OpenFiles = () => workbench.Tabs.OfType<FileTabViewModel>().Select(t => t.FilePath);
        Themes = themes;
        this.userSettings = userSettings;
        this.host = host;
        this.settingsLogger = settingsLogger;
        _platformActions = platformActions ?? new ExplorerPlatformActions();
        Explorer = new FileExplorerViewModel(workbench, fileOperations, _platformActions);
        // 打开文件或面板时自动显示编辑区
        workbench.OpenRequested += () => IsWorkbenchOpen = true;
        workbench.Emptied += () => IsWorkbenchOpen = false;
        // 变更面板与转录中的“审查 / 打开”跟随当前会话
        conversation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationViewModel.Active))
            {
                workbench.SetChangesSource(conversation.Active);
            }
        };
        // 文件树跟随当前会话的工作目录，没有会话时跟随所选项目
        conversation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConversationViewModel.Active) or nameof(ConversationViewModel.Workspace))
            {
                SyncExplorerRoot();
            }
        };
        conversation.WorkspaceRootsChanged += SyncExplorerRoot;
        Explorer.RemoveSessionRootAsync = conversation.RemoveSessionDirectoryAsync;
        SyncExplorerRoot();
    }

    private readonly IExplorerPlatformActions _platformActions;

    public SidebarViewModel Sidebar { get; }

    /// <summary>项目菜单的「在 Finder 中显示」（按平台命名）。</summary>
    public string RevealProjectText => Strings.Get(OperatingSystem.IsWindows() ? TKCodeLangResourceKind.SessionRevealExplorer
        : OperatingSystem.IsMacOS() ? TKCodeLangResourceKind.SessionRevealFinder : TKCodeLangResourceKind.SessionRevealFolder);

    /// <summary>在系统文件管理器中显示文件夹（项目菜单）；失败时只记日志。</summary>
    public async void RevealFolder(string path)
    {
        try
        {
            await _platformActions.ShowAsync(path, isDirectory: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            settingsLogger.LogWarning(ex, "Revealing {Path} failed", path);
        }
    }
    public ConversationViewModel Conversation { get; }
    public WorkbenchViewModel Workbench { get; }
    public ThemeService Themes { get; }

    /// <summary>应用内更新：右下角提醒卡片、侧栏小圆点、设置 › 关于的「更新」一节。</summary>
    public UpdatesViewModel Updates { get; }

    /// <summary>右侧文件树。</summary>
    public FileExplorerViewModel Explorer { get; }

    [ObservableProperty]
    public partial bool IsExplorerOpen { get; set; }

    [RelayCommand]
    private void ToggleExplorer() => IsExplorerOpen = !IsExplorerOpen;

    /// <summary>文件树：主文件夹，以及项目的其他文件夹与本会话追加的文件夹（docs/design/projects.md §10.1）。</summary>
    private void SyncExplorerRoot() =>
        Explorer.SetRoots(Conversation.CurrentCwd, Conversation.CurrentAdditionalRoots, Conversation.CurrentSessionRoots);

    /// <summary>⌘K 命令面板。</summary>
    public CommandPaletteViewModel Palette => field ??= new CommandPaletteViewModel(this);

    /// <summary>确认框（由主窗口提供）：标题、正文、确定按钮文字。设置页惰性创建，所以经由这里转发。</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>设置页（全页，覆盖侧栏与主区域）。</summary>
    public SettingsViewModel Settings => field ??= new SettingsViewModel(userSettings, host, Conversation, Themes, SetLanguageAsync, settingsLogger)
    {
        ConfirmAsync = (title, text, ok) => ConfirmAsync?.Invoke(title, text, ok) ?? Task.FromResult(false),
    };

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; private set; }

    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        IsSettingsOpen = true;
        await Settings.OpenAsync();
    }

    /// <summary>托盘菜单「检查更新」：打开设置 › 关于并立即检查。</summary>
    [RelayCommand]
    private async Task OpenAboutAndCheckAsync()
    {
        IsSettingsOpen = true;
        await Settings.OpenAsync(SettingsSection.About);
        await Updates.CheckNowCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    /// <summary>托盘 / Dock / 跳转列表的「新会话」：先离开设置页。</summary>
    public void StartNewSession()
    {
        IsSettingsOpen = false;
        Sidebar.NewSessionCommand.Execute(null);
    }

    /// <summary>托盘 / Dock / 跳转列表的项目条目：先离开设置页，再像在侧栏里选中该项目一样进入。</summary>
    public void OpenProject(string path)
    {
        IsSettingsOpen = false;
        Sidebar.OpenProject(path);
    }

    public bool IsChinese => ArcLanguage.Current == LanguageTags.ZhCN;

    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool IsWorkbenchOpen { get; set; }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private void ToggleWorkbench() => IsWorkbenchOpen = !IsWorkbenchOpen;

    [RelayCommand]
    private Task ToggleTheme() => Themes.ToggleAsync();

    /// <summary>界面缩放：沿设置页的档位放大 / 缩小一档，或恢复 100%（⌘+ ⌘- ⌘0、命令面板）。</summary>
    [RelayCommand]
    private void ZoomIn() => userSettings.StepUiScale(1);

    [RelayCommand]
    private void ZoomOut() => userSettings.StepUiScale(-1);

    [RelayCommand]
    private void ZoomReset() => userSettings.SetUiScale(UiScaleLevels.Default);

    /// <summary>在中文与英文之间切换；Arc 内置控件文案随之切换，选择保存到 settings.json。</summary>
    [RelayCommand]
    private Task ToggleLanguage() => SetLanguageAsync(IsChinese ? "en-US" : "zh-CN");

    private Task SetLanguageAsync(string language)
    {
        if (ArcLanguageManager.Current is { } manager && LanguageTag.TryParse(language, out var tag) &&
            (tag == LanguageTags.EnUS || tag == LanguageTags.ZhCN))
        {
            manager.Set(tag);
            userSettings.SetLanguage(tag.Value);
            OnPropertyChanged(nameof(IsChinese));
        }
        return Task.CompletedTask;
    }
}
