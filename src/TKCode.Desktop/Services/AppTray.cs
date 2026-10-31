using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using TKCode.Config;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Updates;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Services;

/// <summary>
/// 系统托盘（Windows 通知区域 / macOS 菜单栏 / Linux StatusNotifier）与「关闭窗口不退出」：
/// 默认点窗口的关闭按钮只隐藏窗口，会话继续运行；从托盘菜单「退出 TKCode」、macOS ⌘Q 或安装更新时才真正退出。
/// settings.json 的 closeToTray: false 恢复为关闭窗口即退出。
/// 同一份「新会话 / 最近项目」还出现在 macOS Dock 右键菜单和 Windows 任务栏按钮的跳转列表（含「退出」）里，
/// 它们的条目由 <see cref="BuildItems"/> / <see cref="BuildDockItems"/> / <see cref="BuildJumpList"/> 统一生成。
/// </summary>
public sealed class AppTray
{
    /// <summary>项目菜单最多列出的项目数（最近使用的在前）。</summary>
    internal const int ProjectLimit = 8;

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Window _window;
    private readonly UserSettingsService _settings;
    private readonly UpdatesViewModel _updates;
    private readonly MainWindowViewModel _main;
    private readonly ProjectService _projects;
    private readonly TrayIcon _icon;

    /// <summary>托盘菜单只创建一次：Avalonia.Native 会把它导出为原生菜单，换成另一个实例时更新会失败（“menu being updated does not match”）。Dock 菜单同理。</summary>
    private readonly NativeMenu _menu = new();
    private readonly NativeMenu? _dockMenu;

    /// <summary>未读的会话数（docs/design/notifications.md §4.5）：托盘提示文字与图标红点。</summary>
    private int _unread;
    private WindowIcon? _plainIcon;
    private WindowIcon? _dotIcon;

    public AppTray(Application app, IClassicDesktopStyleApplicationLifetime desktop, Window window, UserSettingsService settings, UpdatesViewModel updates,
        MainWindowViewModel main, ProjectService projects)
    {
        _desktop = desktop;
        _window = window;
        _settings = settings;
        _updates = updates;
        _main = main;
        _projects = projects;
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://TKCode/Assets/app-logo.png"))),
            ToolTipText = "TKCode",
            Menu = _menu,
        };
        _icon.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(app, [_icon]);
        if (OperatingSystem.IsMacOS())
        {
            _dockMenu = new NativeMenu();
            NativeDock.SetMenu(app, _dockMenu);
        }

        window.Closing += OnWindowClosing;
        // macOS：窗口隐藏后点 Dock 图标重新显示
        if (app.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    ShowWindow();
                }
            };
        }
        updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UpdatesViewModel.HasUpdate) or nameof(UpdatesViewModel.CardMode) or nameof(UpdatesViewModel.VersionText))
            {
                Rebuild();
            }
        };
        Strings.LanguageChanged += (_, _) =>
        {
            Rebuild();
            _icon.ToolTipText = ToolTip(_unread);
        };
        // 切换项目、新建 / 修改 / 移除项目后「最近项目」随之变化
        main.Conversation.WorkspaceChanged += Rebuild;
        projects.Changed += Rebuild;
        Rebuild();
    }

    private bool CloseToTray => _settings.Current.CloseToTray != false;

    /// <summary>有会话在后台结束或等待处理、还没打开过：提示文字显示数量，图标右上角加红点。</summary>
    public void SetUnread(int count)
    {
        if (count == _unread)
        {
            return;
        }
        var hadDot = _unread > 0;
        _unread = count;
        _icon.ToolTipText = ToolTip(count);
        if (hadDot == count > 0)
        {
            return;
        }
        try
        {
            _icon.Icon = count > 0
                ? _dotIcon ??= new WindowIcon(Notifications.BadgeRenderer.TrayIcon(dot: true))
                : _plainIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://TKCode/Assets/app-logo.png")));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning($"TKCode: updating the tray icon failed: {ex.Message}");
        }
    }

    /// <summary>托盘提示文字：「TKCode」，有未读时附上数量。</summary>
    internal static string ToolTip(int unread) =>
        unread > 0 ? $"TKCode · {Strings.Format(TKCodeLangResourceKind.TrayUnreadFormat, unread)}" : "TKCode";

    /// <summary>只拦截用户点关闭按钮（或 Alt+F4 / ⌘W）；退出程序、系统关机时照常关闭。</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing && CloseToTray)
        {
            e.Cancel = true;
            _window.Hide();
        }
    }

    public void ShowWindow()
    {
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Show();
        _window.Activate();
    }

    private void Rebuild()
    {
        var projects = _projects.Recent(ProjectLimit);
        FillMenu(_menu, BuildItems(_updates, projects));
        if (_dockMenu is not null)
        {
            FillMenu(_dockMenu, BuildDockItems(projects));
        }
        var (tasks, entries) = BuildJumpList(projects);
        WindowsJumpList.Update(Strings.Get(TKCodeLangResourceKind.TrayProjects), entries, tasks);
    }

    private void FillMenu(NativeMenu menu, IReadOnlyList<TrayItem> items)
    {
        menu.Items.Clear();
        foreach (var item in items)
        {
            menu.Items.Add(ToNative(item));
        }
    }

    private NativeMenuItemBase ToNative(TrayItem item)
    {
        if (item.Text is null)
        {
            return new NativeMenuItemSeparator();
        }
        var native = new NativeMenuItem(item.Text) { IsEnabled = item.Action != TrayAction.None || item.Children is not null };
        if (item.Children is { } children)
        {
            native.Menu = new NativeMenu();
            foreach (var child in children)
            {
                native.Menu.Items.Add(ToNative(child));
            }
        }
        else
        {
            native.Click += (_, _) => Run(item);
        }
        return native;
    }

    private void Run(TrayItem item)
    {
        switch (item.Action)
        {
            case TrayAction.Show:
                Execute(AppCommand.Show);
                break;
            case TrayAction.NewSession:
                Execute(AppCommand.NewSession);
                break;
            case TrayAction.OpenProject when item.Argument is { } path:
                Execute(AppCommand.OpenProject(path));
                break;
            case TrayAction.Download:
                ShowWindow();
                _updates.DownloadCommand.Execute(null);
                break;
            case TrayAction.Install:
                ShowWindow();
                _updates.InstallCommand.Execute(null);
                break;
            case TrayAction.CheckUpdates:
                ShowWindow();
                _main.OpenAboutAndCheckCommand.Execute(null);
                break;
            case TrayAction.Settings:
                ShowWindow();
                _main.OpenSettingsCommand.Execute(null);
                break;
            case TrayAction.Quit:
                Execute(AppCommand.Quit);
                break;
        }
    }

    /// <summary>
    /// 执行一条指令：托盘 / Dock 菜单的点击、另一个 TKCode 进程转发来的命令行参数（跳转列表条目）、首次启动时带的参数。
    /// 必须在 UI 线程上调用。
    /// </summary>
    internal void Execute(AppCommand command)
    {
        switch (command.Kind)
        {
            case AppCommandKind.Show:
                ShowWindow();
                break;
            case AppCommandKind.NewSession:
                ShowWindow();
                _main.StartNewSession();
                break;
            case AppCommandKind.OpenProject when command.Argument is { } path:
                ShowWindow();
                _main.OpenProject(path);
                break;
            case AppCommandKind.Quit:
                _desktop.Shutdown();
                break;
        }
    }

    /// <summary>托盘菜单的内容（与平台无关，便于测试）；Text 为 null 表示分隔线。</summary>
    internal static IReadOnlyList<TrayItem> BuildItems(UpdatesViewModel updates, IReadOnlyList<Project>? projects = null)
    {
        var items = new List<TrayItem>
        {
            new(Strings.Get(TKCodeLangResourceKind.TrayShow), TrayAction.Show),
            new(Strings.Get(TKCodeLangResourceKind.NewSession), TrayAction.NewSession),
            new(Strings.Get(TKCodeLangResourceKind.TrayProjects), TrayAction.None, Children: ProjectItems(projects ?? [], placeholder: true)),
            TrayItem.Separator,
        };
        switch (updates.Service.Status)
        {
            case UpdateStatus.Available when updates.CanInstallInApp:
                items.Add(new(Strings.Format(TKCodeLangResourceKind.TrayDownloadUpdateFormat, updates.VersionText), TrayAction.Download));
                break;
            case UpdateStatus.Available:
                items.Add(new(Strings.Format(TKCodeLangResourceKind.UpdateAvailableFormat, updates.VersionText), TrayAction.Show));
                break;
            case UpdateStatus.Downloading:
                items.Add(new(Strings.Format(TKCodeLangResourceKind.UpdateDownloadingFormat, updates.VersionText), TrayAction.None));
                break;
            case UpdateStatus.Ready:
                items.Add(new(Strings.Format(TKCodeLangResourceKind.TrayInstallUpdateFormat, updates.VersionText), TrayAction.Install));
                break;
            default:
                items.Add(new(Strings.Get(TKCodeLangResourceKind.UpdateCheckNow), TrayAction.CheckUpdates));
                break;
        }
        items.Add(new(Strings.Get(TKCodeLangResourceKind.OpenSettings), TrayAction.Settings));
        items.Add(TrayItem.Separator);
        items.Add(new(Strings.Get(TKCodeLangResourceKind.TrayQuit), TrayAction.Quit));
        return items;
    }

    /// <summary>
    /// macOS Dock 右键菜单：新会话 + 平铺的最近项目（Dock 菜单习惯平铺）。
    /// 「退出」「隐藏」等由系统自带，不重复添加。
    /// </summary>
    internal static IReadOnlyList<TrayItem> BuildDockItems(IReadOnlyList<Project> projects)
    {
        var items = new List<TrayItem> { new(Strings.Get(TKCodeLangResourceKind.NewSession), TrayAction.NewSession) };
        if (projects.Count > 0)
        {
            items.Add(TrayItem.Separator);
            items.Add(new(Strings.Get(TKCodeLangResourceKind.TrayProjects), TrayAction.None));
            items.AddRange(ProjectItems(projects, placeholder: false));
        }
        return items;
    }

    /// <summary>
    /// Windows 任务栏按钮右键的跳转列表：任务（新会话、退出 TKCode）+「项目」类别（最近项目）。
    /// 任务栏按钮的右键菜单由系统绘制，「关闭窗口」只会隐藏到托盘，所以这里单独提供真正的「退出」。
    /// </summary>
    internal static (IReadOnlyList<JumpListEntry> Tasks, IReadOnlyList<JumpListEntry> Projects) BuildJumpList(IReadOnlyList<Project> projects)
    {
        IReadOnlyList<JumpListEntry> tasks =
        [
            new(Strings.Get(TKCodeLangResourceKind.NewSession), AppCommand.NewSession.ToCommandLine(), null),
            new(Strings.Get(TKCodeLangResourceKind.TrayQuit), AppCommand.Quit.ToCommandLine(), null),
        ];
        var entries = projects.Select(p => new JumpListEntry(p.Name, AppCommand.OpenProject(p.Primary).ToCommandLine(), p.Primary)).ToList();
        return (tasks, entries);
    }

    /// <summary>项目条目；重名的项目（不同目录下的同名文件夹）附上上级目录名以便区分。没有项目时可放一条灰色的占位。</summary>
    private static List<TrayItem> ProjectItems(IReadOnlyList<Project> projects, bool placeholder)
    {
        if (projects.Count == 0)
        {
            return placeholder ? [new(Strings.Get(TKCodeLangResourceKind.TrayNoProjects), TrayAction.None)] : [];
        }
        return projects.Select(p =>
        {
            var duplicated = projects.Count(o => string.Equals(o.Name, p.Name, StringComparison.OrdinalIgnoreCase)) > 1;
            var parent = duplicated ? Path.GetFileName(Path.GetDirectoryName(p.Primary.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))) : null;
            return new TrayItem(string.IsNullOrEmpty(parent) ? p.Name : $"{p.Name} ({parent})", TrayAction.OpenProject, p.Primary);
        }).ToList();
    }
}

internal enum TrayAction
{
    None,
    Show,
    NewSession,
    OpenProject,
    Download,
    Install,
    CheckUpdates,
    Settings,
    Quit,
}

/// <param name="Text">null 表示分隔线。</param>
/// <param name="Argument">OpenProject 的项目路径。</param>
/// <param name="Children">非 null 表示子菜单（父条目自身不可点击）。</param>
internal sealed record TrayItem(string? Text, TrayAction Action, string? Argument = null, IReadOnlyList<TrayItem>? Children = null)
{
    public static readonly TrayItem Separator = new(null, TrayAction.None);
}
