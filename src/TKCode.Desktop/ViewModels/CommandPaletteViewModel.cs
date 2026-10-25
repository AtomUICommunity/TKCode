using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.ViewModels;

/// <summary>命令面板中的一项：命令、会话或设备。</summary>
public sealed partial class PaletteItemViewModel(string title, Func<Task> execute) : ObservableObject
{
    public string Title { get; } = title;
    public Func<Task> Execute { get; } = execute;

    public LucideKind Icon { get; init; } = LucideKind.SquarePen;
    /// <summary>设置后显示品牌图标（会话的模型）而不是 <see cref="Icon"/>。</summary>
    public string? LogoKey { get; init; }
    public bool HasLogo { get; init; }
    public string? Subtitle { get; init; }
    public string? Shortcut { get; init; }
    /// <summary>设备在线指示（绿点）。</summary>
    public bool IsOnline { get; init; }

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }
}

/// <summary>面板的一页：根页（命令 + 会话搜索）或子页（例如“新建项目”的设备选择）。</summary>
public sealed record PalettePage(string? Breadcrumb, string Placeholder, Func<string, IEnumerable<PaletteItemViewModel>> Items, bool OpenHint);

/// <summary>
/// ⌘K 命令面板：搜索命令与会话；支持多级页面（← 返回 / Backspace 在空搜索时返回上一页）。
/// 键盘：↑↓ 移动高亮，↵ 执行，Esc 返回或关闭。
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly Stack<PalettePage> _pages = new();
    private readonly MainWindowViewModel _main;

    public CommandPaletteViewModel(MainWindowViewModel main)
    {
        _main = main;
    }

    public ObservableCollection<PaletteItemViewModel> Items { get; } = [];

    /// <summary>“新建项目”：请求视图打开创建项目弹窗（设备在弹窗里选择，docs/design/projects.md §8.4）。</summary>
    public event Action? NewProjectRequested;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBreadcrumb), nameof(HotkeyHint))]
    public partial string? Breadcrumb { get; private set; }

    public bool HasBreadcrumb => Breadcrumb is not null;

    /// <summary>搜索框右侧的键位提示：根页为 ⌘K（再按一次关闭），子页为 esc（返回）。</summary>
    public string HotkeyHint => Breadcrumb is null ? (OperatingSystem.IsMacOS() ? "⌘K" : "Ctrl+K") : "esc";

    [ObservableProperty]
    public partial string Placeholder { get; private set; } = "";

    /// <summary>页脚第二个提示：根页为“选择”，设备页为“打开”。</summary>
    [ObservableProperty]
    public partial bool OpenHint { get; private set; }

    public bool IsEmpty => Items.Count == 0;

    partial void OnSearchChanged(string value) => Refresh();

    [RelayCommand]
    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public void Open()
    {
        _pages.Clear();
        Push(RootPage());
        IsOpen = true;
    }

    /// <summary>新建项目（⇧⌘N）：关闭面板，打开创建项目弹窗。</summary>
    public void OpenNewProject()
    {
        Close();
        NewProjectRequested?.Invoke();
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        _pages.Clear();
        Search = "";
    }

    [RelayCommand]
    public void Back()
    {
        if (_pages.Count > 1)
        {
            _pages.Pop();
            Show(_pages.Peek());
        }
        else
        {
            Close();
        }
    }

    public void MoveHighlight(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }
        var index = Items.ToList().FindIndex(i => i.IsHighlighted);
        Highlight(Math.Clamp(index + delta, 0, Items.Count - 1));
    }

    public Task ExecuteHighlightedAsync() =>
        Items.FirstOrDefault(i => i.IsHighlighted) is { } item ? ExecuteAsync(item) : Task.CompletedTask;

    [RelayCommand]
    private async Task ExecuteAsync(PaletteItemViewModel item) => await item.Execute();

    /// <summary>空搜索时 Backspace 返回上一页。</summary>
    public bool TryBackOnEmptySearch()
    {
        if (Search.Length == 0 && _pages.Count > 1)
        {
            Back();
            return true;
        }
        return false;
    }

    private void Push(PalettePage page)
    {
        _pages.Push(page);
        Show(page);
    }

    private void Show(PalettePage page)
    {
        Breadcrumb = page.Breadcrumb;
        Placeholder = page.Placeholder;
        OpenHint = page.OpenHint;
        if (Search.Length > 0)
        {
            Search = ""; // 触发 Refresh
        }
        else
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        Items.Clear();
        if (_pages.TryPeek(out var page))
        {
            foreach (var item in page.Items(Search))
            {
                Items.Add(item);
            }
        }
        Highlight(0);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Highlight(int index)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IsHighlighted = i == index;
        }
    }

    private PalettePage RootPage() => new(null, Strings.Get(TKCodeLangResourceKind.SearchCommands), search =>
    {
        var commands = Commands().Where(c => Matches(c.Title, search));
        if (search.Length == 0)
        {
            return commands;
        }
        // 有搜索词时追加匹配的会话
        var chats = _main.Conversation.Sessions
            .Where(s => Matches(s.DisplayTitle, search) || Matches(s.ProjectName, search))
            .Take(8)
            .Select(s => new PaletteItemViewModel(s.DisplayTitle, () => Run(() => _main.Conversation.OpenSessionCommand.ExecuteAsync(s)))
            {
                LogoKey = s.ModelIconKey,
                HasLogo = true,
                Subtitle = s.ProjectName,
            });
        return commands.Concat(chats);
    }, OpenHint: false);

    private IEnumerable<PaletteItemViewModel> Commands()
    {
        var mod = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        var shift = OperatingSystem.IsMacOS() ? "⇧⌘" : "Ctrl+Shift+";
        yield return new PaletteItemViewModel(Strings.Get(TKCodeLangResourceKind.NewChat), () => Run(_main.Sidebar.NewSessionCommand.Execute))
        {
            Icon = LucideKind.SquarePen,
            Shortcut = mod + "N",
        };
        yield return new PaletteItemViewModel(Strings.Get(TKCodeLangResourceKind.NewProjectCommand), () => Run(() => NewProjectRequested?.Invoke()))
        {
            Icon = LucideKind.Folder,
            Shortcut = shift + "N",
        };
        yield return new PaletteItemViewModel(Strings.Get(TKCodeLangResourceKind.OpenSettings), () => Run(() => _main.OpenSettingsCommand.ExecuteAsync(null)))
        {
            Icon = LucideKind.SlidersHorizontal,
            Shortcut = mod + ",",
        };
        yield return new PaletteItemViewModel(Strings.Get(_main.Themes.IsDark ? TKCodeLangResourceKind.SwitchToLight : TKCodeLangResourceKind.SwitchToDark),
            () => Run(() => _main.ToggleThemeCommand.ExecuteAsync(null)))
        {
            Icon = _main.Themes.IsDark ? LucideKind.Sun : LucideKind.Moon,
        };
        yield return new PaletteItemViewModel(Strings.Get(_main.IsChinese ? TKCodeLangResourceKind.SwitchToEnglish : TKCodeLangResourceKind.SwitchToChinese),
            () => Run(_main.ToggleLanguageCommand.Execute))
        {
            Icon = LucideKind.Languages,
        };
        yield return new PaletteItemViewModel(Strings.Get(TKCodeLangResourceKind.ZoomIn), () => Run(_main.ZoomInCommand.Execute))
        {
            Icon = LucideKind.ZoomIn,
            Shortcut = mod + "+",
        };
        yield return new PaletteItemViewModel(Strings.Get(TKCodeLangResourceKind.ZoomOut), () => Run(_main.ZoomOutCommand.Execute))
        {
            Icon = LucideKind.ZoomOut,
            Shortcut = mod + "-",
        };
        yield return new PaletteItemViewModel(Strings.Get(TKCodeLangResourceKind.ZoomReset), () => Run(_main.ZoomResetCommand.Execute))
        {
            Icon = LucideKind.Scan,
            Shortcut = mod + "0",
        };
        yield return new PaletteItemViewModel(Strings.Get(TKCodeLangResourceKind.ToggleSidebar), () => Run(_main.ToggleSidebarCommand.Execute))
        {
            Icon = LucideKind.PanelLeft,
            Shortcut = mod + "B",
        };
        yield return new PaletteItemViewModel(Strings.Get(TKCodeLangResourceKind.ToggleExplorer), () => Run(_main.ToggleExplorerCommand.Execute))
        {
            Icon = LucideKind.FolderTree,
            Shortcut = shift + "E",
        };
        yield return new PaletteItemViewModel(Strings.Get(TKCodeLangResourceKind.ToggleWorkbench), () => Run(_main.ToggleWorkbenchCommand.Execute))
        {
            Icon = LucideKind.PanelRight,
        };
    }

    /// <summary>执行后关闭面板。</summary>
    private async Task Run(Func<Task> action)
    {
        Close();
        await action();
    }

    private Task Run(Action<object?> action)
    {
        Close();
        action(null);
        return Task.CompletedTask;
    }

    private Task Run(Action action)
    {
        Close();
        action();
        return Task.CompletedTask;
    }

    private static bool Matches(string text, string search) =>
        search.Length == 0 || text.Contains(search, StringComparison.OrdinalIgnoreCase);
}
