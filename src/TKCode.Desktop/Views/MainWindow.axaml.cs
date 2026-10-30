using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Rendering;
using TKCode.Config;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class MainWindow : Window
{
    private const double HeaderHeight = 44;
    // 与 MainWindow.axaml 中的 MinWidth / MinHeight 一致；窗口最小尺寸随界面缩放按比例放大，内容的可用逻辑尺寸才不会小于设计下限
    private const double BaseMinWidth = 880;
    private const double BaseMinHeight = 560;
    private const double MacTrafficLightInset = 78;
    private const double CaptionButtonsWidth = 138;

    private const double PanelTogglesWidth = 64;
    private double _workbenchWidth = 520;
    private double _explorerWidth = 284;
    private bool _explorerResized;  // 用户拖过文件树分割条后，不再按 1/4 上限收窄
    private const double ConversationMinWidth = 360;
    private const double EditorMinWidth = 320;
    private const double ExplorerMinWidth = 200;
    private readonly TranscriptAdapter _transcript;
    private Point _markdownPress;
    private bool _markdownArmed;
    private static readonly Cursor CodePathCursor = new(StandardCursorType.Hand);
    private ExplorerContextMenu? _explorerMenu;
    private FileExplorerViewModel? _explorerModel;

    private void AttachExplorer(FileExplorerViewModel? explorer)
    {
        _explorerMenu?.Dispose();
        _explorerMenu = null;
        if (_explorerModel is { } previous)
        {
            previous.FileDialogRequested = null;
            previous.CopyTextAsync = null;
        }
        _explorerModel = explorer;
        if (explorer is not null)
        {
            explorer.FileDialogRequested = request => ExplorerFileDialog.ShowAsync(this, request);
            explorer.CopyTextAsync = text => Clipboard?.SetTextAsync(text)
                ?? Task.FromException(new InvalidOperationException("The system clipboard is unavailable."));
            _explorerMenu = new ExplorerContextMenu(explorer, this, error => ExplorerFileDialog.ShowErrorAsync(this, error));
        }
    }

    private void ShowExplorerContext(FileNodeViewModel? node, bool isSearch, Control placement, Point position, Control focus)
    {
        if (_explorerModel is not { } explorer || explorer.CaptureContext(node, isSearch) is not { } target)
        {
            return;
        }
        if (node is not null)
        {
            explorer.SelectedNode = node;
        }
        _explorerMenu?.Show(target, placement, position, focus);
    }

    private void OnExplorerSearchRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Avalonia.Controls.Button { CommandParameter: FileNodeViewModel node } row
            && e.GetCurrentPoint(row).Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            if (IsLiveExplorerSearchRow(row, node))
            {
                row.Focus(NavigationMethod.Pointer);
                ShowExplorerContext(node, true, row, e.GetPosition(row), row);
            }
        }
    }

    private void OnExplorerSearchRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is Avalonia.Controls.Button { CommandParameter: FileNodeViewModel node } row
            && (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)))
        {
            e.Handled = true;
            if (row.IsFocused && IsLiveExplorerSearchRow(row, node))
            {
                ShowExplorerContext(node, true, row, new Point(8, row.Bounds.Height), row);
            }
        }
    }

    private bool IsLiveExplorerSearchRow(Control row, FileNodeViewModel node)
    {
        if (node.IsPlaceholder || !row.IsEffectivelyVisible || _explorerModel is not { IsSearching: true } explorer
            || !explorer.SearchResults.Contains(node))
        {
            return false;
        }
        var viewport = row.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().FirstOrDefault();
        return viewport is not null && row.TranslatePoint(default, viewport) is { } origin
            && new Rect(origin, row.Bounds.Size).Intersects(new Rect(viewport.Bounds.Size));
    }

    private void OnExplorerSearchBlankPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Avalonia.Controls.ScrollViewer scroll || !e.GetCurrentPoint(scroll).Properties.IsRightButtonPressed)
        {
            return;
        }
        // Only genuine viewport blank space: never reuse selection, a scrollbar, or a recycled/placeholder row.
        for (var visual = e.Source as Visual; visual is not null && !ReferenceEquals(visual, scroll); visual = visual.GetVisualParent())
        {
            if (visual is Avalonia.Controls.Primitives.ScrollBar or Avalonia.Controls.Primitives.Thumb or Avalonia.Controls.Button)
            {
                return;
            }
        }
        e.Handled = true;
        ShowExplorerContext(null, true, scroll, e.GetPosition(scroll), scroll);
    }

    /// <summary>双击会话标题：行内重命名。</summary>
    private void OnSessionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionItemViewModel item })
        {
            item.BeginRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>危险操作确认框（Arc 按钮，确定按钮为“删除”）。</summary>
    private Task<bool> ConfirmDangerAsync(string title, string message) =>
        ConfirmDialog.ShowAsync(this, title, message, Strings.Get(TKCodeLangResourceKind.SessionDelete), danger: true);

    /// <summary>通用确认框（Arc 按钮）。</summary>
    private Task<bool> ConfirmAsync(string title, string message, string okText) =>
        ConfirmDialog.ShowAsync(this, title, message, okText);

    /// <summary>
    /// 会话中的相对图片按会话的 cwd 解析；本地图片只允许读取已添加的项目的各个文件夹（以及当前会话追加的文件夹）之下的文件。
    /// </summary>
    private void UpdateImageRoots(MainWindowViewModel vm)
    {
        var cwd = vm.Conversation.Active?.Cwd ?? vm.Conversation.Workspace?.Primary;
        StreamMark.Avalonia.Extensions.MarkdownServices.SetImageBaseUri(Transcript, cwd);
        MarkdownSetup.SetAllowedFileRoots(vm.Sidebar.Projects.SelectMany(p => p.Project.Folders)
            .Concat(vm.Conversation.Active?.SessionDirectories ?? [])
            .Append(cwd ?? ""));
    }

    /// <summary>滚动到接近顶部时加载更早的一页历史（插入后会话视图自己保持当前阅读位置）。</summary>
    private async Task LoadEarlierIfNearTopAsync()
    {
        if (Transcript.Offset.Y > 300 || DataContext is not MainWindowViewModel vm ||
            vm.Conversation.Active is not { HasEarlierHistory: true, IsLoadingEarlier: false })
        {
            return;
        }
        await vm.Conversation.LoadEarlierHistoryAsync();
    }

    private void UpdateScrollToBottom() =>
        Motion.Presence.SetIsPresent(ScrollToBottomButton, !Transcript.IsPinnedToBottom && Transcript.Extent.Height > Transcript.Viewport.Height + 1);

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        Transcript.ScrollToEnd();
        Motion.Presence.SetIsPresent(ScrollToBottomButton, false);
    }

    private void OnMarkdownLinkClicked(object? sender, StreamMark.Avalonia.Rendering.LinkClickedEventArgs e)
    {
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            e.Handled = true;
            _ = OpenLinkAsync(uri, e.RequiresConfirmation);
            return;
        }
        if (DataContext is MainWindowViewModel vm)
        {
            // 会话继承工作目录，Markdown 文件预览则继承文件所在目录。
            var directory = e.Source is AvaloniaObject source ? MarkdownServices.GetImageBaseUri(source) : null;
            directory ??= vm.Conversation.Active?.Cwd ?? vm.Conversation.Workspace?.Primary;
            if (MarkdownLinks.ResolveFile(e.Url, directory) is { } path)
            {
                e.Handled = true;
                vm.Workbench.OpenFile(path);
            }
        }
    }

    private void OnMarkdownPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _markdownPress = e.GetPosition(this);
            _markdownArmed = true;
        }
    }

    private void OnMarkdownPointerMoved(object? sender, PointerEventArgs e)
    {
        if (FindSegment(e.Source) is not { } segment || DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        if (MarkdownCodePaths.Hit(segment, e.GetPosition(segment), MarkdownCodePaths.Directory(segment, vm)) is not null)
        {
            segment.Cursor = CodePathCursor;
        }
    }

    private void OnMarkdownPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_markdownArmed)
        {
            return;
        }
        _markdownArmed = false;
        if (e.Handled || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }
        var delta = e.GetPosition(this) - _markdownPress;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) >= 3 || FindSegment(e.Source) is not { } segment)
        {
            return;
        }
        if (TryOpenCodeSpan(segment, e.GetPosition(segment)))
        {
            e.Handled = true;
        }
    }

    /// <summary>点击行内代码中的文件路径时，在右侧工作区打开预览。</summary>
    internal bool TryOpenCodeSpan(TextSegmentView segment, Point local)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return false;
        }
        if (MarkdownCodePaths.Hit(segment, local, MarkdownCodePaths.Directory(segment, vm)) is not { } path)
        {
            return false;
        }
        vm.Workbench.OpenFile(path);
        return true;
    }

    private static TextSegmentView? FindSegment(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TextSegmentView segment)
            {
                return segment;
            }
        }
        return null;
    }

    // 链接文字看起来是另一个地址（例如 [https://github.com](https://evil.example)）时先确认
    private async Task OpenLinkAsync(Uri uri, bool confirm)
    {
        if (confirm && !await ConfirmAsync(Strings.Get(TKCodeLangResourceKind.LinkConfirmTitle),
                Strings.Format(TKCodeLangResourceKind.LinkConfirmMessageFormat, uri.AbsoluteUri), Strings.Get(TKCodeLangResourceKind.LinkOpen)))
        {
            return;
        }
        await Launcher.LaunchUriAsync(uri);
    }

    public MainWindow()
    {
        InitializeComponent();
        ExplorerTree.ContextRequested += (_, e) => ShowExplorerContext(e.Node, false, ExplorerTree, e.Position, e.FocusTarget);
        Closed += (_, _) => AttachExplorer(null);
        // 任务球里的“更改”入口：打开工作区的变更面板
        TaskOrb.OpenChangesRequested += () => (DataContext as ViewModels.MainWindowViewModel)?.Workbench.OpenChangesCommand.Execute(null);
        ExplorerSplitter.DragCompleted += (_, _) =>
        {
            _explorerResized = true;
            _explorerWidth = MainGrid.ColumnDefinitions[4].Width.Value;
        };
        ConfigureChrome();
        UiZoom.Changed += ApplyZoom;
        Closed += (_, _) => UiZoom.Changed -= ApplyZoom;
        ApplyZoom();

        // 会话视图：渲染扩展（公式、代码高亮、图表）对窗口内所有 Markdown 生效
        MarkdownServices.SetServices(this, MarkdownSetup.Services);
        Transcript.DataTemplates.AddRange(new TranscriptTemplates());
        _transcript = new TranscriptAdapter(Transcript.Entries);
        // 发送新消息时回到底部并恢复贴底跟随（流式中的贴底由会话视图自己处理）
        _transcript.UserMessageAdded += () => Transcript.ScrollToEnd();
        // 离开底部（不再贴底跟随）且内容超过一屏时显示“回到底部”
        Transcript.ScrollChanged += (_, _) =>
        {
            UpdateScrollToBottom();
            _ = LoadEarlierIfNearTopAsync();
        };
        // 侧栏开合动画期间会话视图固定宽度（降载：长会话不逐帧重新排版），结束后重排一次
        SidebarReveal.AnimatingChanged += (reveal, animating) =>
        {
            if (animating)
            {
                Freeze(Transcript, Transcript.Bounds.Width + (reveal.IsOpen ? 0 : 256));
            }
            else
            {
                ReleaseFreeze();
            }
        };
        // 系统“减少动态效果”可能在后台被修改
        Activated += (_, _) => Motion.MotionSettings.RefreshSystem();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // 把文件拖到消息区域：同样交给输入框（图片为附件，其他文件为绝对路径；输入框本身目标太小）
        DragDrop.SetAllowDrop(Transcript, true);
        Transcript.AddHandler(DragDrop.DragEnterEvent, (_, e) => DockedComposer.OnDragOver(e));
        Transcript.AddHandler(DragDrop.DragOverEvent, (_, e) => DockedComposer.OnDragOver(e));
        Transcript.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DockedComposer.SetDropTarget(false));
        Transcript.AddHandler(DragDrop.DropEvent, (_, e) => DockedComposer.OnDrop(e));
        // Markdown 链接（会话与文件预览，含图片链接 / 徽章）：文件在右侧预览，网页与邮件交给系统打开
        AddHandler(StreamMark.Avalonia.MarkdownView.LinkClickedEvent, OnMarkdownLinkClicked);
        // 行内代码里的文件路径：悬停为手型，点击在右侧打开预览（拖拽选择文字时不打开）
        AddHandler(PointerPressedEvent, OnMarkdownPointerPressed, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnMarkdownPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnMarkdownPointerReleased, RoutingStrategies.Bubble);
        DataContextChanged += (_, _) =>
        {
            AttachExplorer((DataContext as MainWindowViewModel)?.Explorer);
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Conversation.ConfirmDangerAsync = ConfirmDangerAsync;
                vm.Workbench.ConfirmAsync = ConfirmAsync;
                vm.ConfirmAsync = ConfirmAsync;
                vm.Conversation.SetClipboardAsync = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
                // /role：打开当前可见的那个模式选择器
                vm.Conversation.RolePickerRequested += () => (DockedRolePicker.IsEffectivelyVisible ? DockedRolePicker : EmptyRolePicker).Toggle();
                vm.PropertyChanged += OnViewModelPropertyChanged;
                vm.Conversation.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ConversationViewModel.Active))
                    {
                        _transcript.Attach(vm.Conversation.Active);
                        Transcript.ScrollToEnd();
                        // 切换会话：会话内容淡入并轻微上移
                        if (vm.Conversation.Active is not null)
                        {
                            Motion.Presence.PlayEnter(Transcript, Motion.PresenceEffect.SlideUp);
                        }
                    }
                    // 发送第一条消息：底部输入框从居中输入框的位置落到底部
                    if (e.PropertyName == nameof(ConversationViewModel.IsEmpty) && !vm.Conversation.IsEmpty && EmptyComposer.IsLoaded)
                    {
                        Motion.Morph.Fly(EmptyComposer, DockedComposer, ConversationColumn);
                    }
                    if (e.PropertyName is nameof(ConversationViewModel.Active) or nameof(ConversationViewModel.Workspace))
                    {
                        UpdateImageRoots(vm);
                    }
                };
                vm.Sidebar.Projects.CollectionChanged += (_, _) => UpdateImageRoots(vm);
                _transcript.Attach(vm.Conversation.Active);
                UpdateImageRoots(vm);
                vm.Workbench.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(WorkbenchViewModel.IsMaximized))
                    {
                        UpdateColumns(vm);
                    }
                    // 切换标签页：内容淡入
                    if (e.PropertyName == nameof(WorkbenchViewModel.ActiveTab) && vm.Workbench.ActiveTab is not null && ActiveTabContent.IsLoaded)
                    {
                        Motion.Presence.PlayEnter(ActiveTabContent, Motion.PresenceEffect.Fade);
                    }
                };
                vm.Palette.PropertyChanged += OnPalettePropertyChanged;
                vm.Palette.NewProjectRequested += () => _ = CreateProjectAsync();
                vm.Sidebar.CreateProjectRequested += () => _ = CreateProjectAsync();
                vm.Sidebar.EditProjectRequested += project => _ = EditProjectAsync(project);
                vm.Sidebar.RemoveProjectRequested += project => _ = RemoveProjectAsync(project);
                vm.Sidebar.RevealProjectRequested += project => vm.RevealFolder(project.Primary);
                vm.Sidebar.CopyPathRequested += path => _ = Clipboard?.SetTextAsync(path);
                vm.Conversation.AddDirectoryRequested += () => _ = AddSessionDirectoriesAsync(vm);
                vm.Conversation.WorkspaceRootsChanged += () => UpdateImageRoots(vm);
                UpdateHeaderInsets(vm);
                UpdateColumns(vm, animate: false);
            }
        };
    }

    /// <summary>
    /// Cursor 式窗口：不显示系统标题栏，内容延伸到窗口顶部。
    /// macOS 保留原生红绿灯并移到侧栏顶部；Windows / Linux 在右上角自绘窗口按钮。
    /// </summary>
    private void ConfigureChrome()
    {
        if (OperatingSystem.IsMacOS())
        {
            // 保留系统装饰（红绿灯），内容延伸到标题栏区域。
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaTitleBarHeightHint = HeaderHeight;
        }
        else
        {
            // 只留调整大小的边框，标题栏与窗口按钮由界面自绘
            WindowDecorations = WindowDecorations.BorderOnly;
            CaptionButtons.IsVisible = true;
        }
    }

    /// <summary>
    /// 界面缩放变化：窗口最小尺寸、macOS 标题栏高度提示与红绿灯避让宽度都要换算。
    /// 内容在缩放后的坐标系里排版，而原生红绿灯与窗口边缘不随缩放——避让宽度除以比例，缩放后才落在同一个物理位置。
    /// </summary>
    private void ApplyZoom()
    {
        var scale = UiZoom.Scale;
        MinWidth = BaseMinWidth * scale;
        MinHeight = BaseMinHeight * scale;
        if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaTitleBarHeightHint = HeaderHeight * scale;
        }
        if (DataContext is MainWindowViewModel vm)
        {
            UpdateHeaderInsets(vm);
            UpdateColumns(vm, animate: false);
        }
    }

    private void UpdateHeaderInsets(MainWindowViewModel vm)
    {
        var left = OperatingSystem.IsMacOS() ? MacTrafficLightInset / UiZoom.Scale : 8;
        SidebarHeader.Margin = new Thickness(left, 0, 0, 0);
        SettingsPage.SetTitleBarInset(left);
        MainHeaderLeft.Margin = new Thickness(vm.IsSidebarOpen ? 8 : left, 0, 0, 0);

        // 右上角的面板开关（以及 Windows / Linux 的窗口按钮）落在最右一栏的标题栏上，该栏标题栏右侧留出位置
        var caption = OperatingSystem.IsMacOS() ? 0 : CaptionButtonsWidth;
        PanelToggles.Margin = new Thickness(0, 0, 8 + caption, 0);
        var reserved = 8 + caption + PanelTogglesWidth + 4;
        ConversationHeader.Margin = new Thickness(0, 0, vm.IsWorkbenchOpen || vm.IsExplorerOpen ? 0 : reserved, 0);
        EditorHeaderRight.Margin = new Thickness(0, 0, vm.IsExplorerOpen ? 8 : reserved, 0);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel vm)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsSidebarOpen):
                UpdateHeaderInsets(vm);
                break;
            case nameof(MainWindowViewModel.IsWorkbenchOpen):
            case nameof(MainWindowViewModel.IsExplorerOpen):
                UpdateColumns(vm);
                UpdateHeaderInsets(vm);
                break;
        }
    }

    /// <summary>
    /// 主区域列宽：对话（*）| 分割条 | 编辑区 | 分割条 | 文件树。关闭时记住宽度；编辑区最大化时隐藏对话栏。
    /// 打开 / 关闭 / 最大化时列宽按弹簧动画过渡（animate=false 时直接到位：首次布局、动效关闭）。
    /// </summary>
    private void UpdateColumns(MainWindowViewModel vm, bool animate = true)
    {
        var columns = MainGrid.ColumnDefinitions;
        var maximized = vm.IsWorkbenchOpen && vm.Workbench.IsMaximized;

        if (_columnMotion is not { IsAnimating: true })
        {
            if (columns[2].Width.IsAbsolute && columns[2].ActualWidth > 0 && !_wasMaximized)
            {
                _workbenchWidth = columns[2].ActualWidth;
            }
            // 拖过之后只认拖出的宽度（DragCompleted 里记下），被挤窄不算
            if (!_explorerResized && columns[4].ActualWidth > 0)
            {
                _explorerWidth = columns[4].ActualWidth;
            }
        }

        // 按可用宽度分配：对话栏至少 ConversationMinWidth；文件树未被拖过时不超过 1/4（最大 420），拖过后保持拖出的宽度、
        // 只在放不下时让位；编辑区取剩余与记忆宽度中的较小值
        var available = MainGrid.Bounds.Width > 0 ? MainGrid.Bounds.Width : Bounds.Width / UiZoom.Scale - (vm.IsSidebarOpen ? 256 : 0);
        var explorerRoom = available - (maximized ? 0 : ConversationMinWidth) - (vm.IsWorkbenchOpen ? EditorMinWidth : 0) - 2;
        var explorerWant = _explorerResized ? _explorerWidth : Math.Min(Math.Min(_explorerWidth, available * 0.25), 420);
        var explorer = vm.IsExplorerOpen ? Math.Max(ExplorerMinWidth, Math.Min(explorerWant, explorerRoom)) : 0;
        var editorRoom = available - explorer - (maximized ? 0 : ConversationMinWidth) - 2;
        var editor = !vm.IsWorkbenchOpen ? 0 : Math.Max(EditorMinWidth, Math.Min(_workbenchWidth, editorRoom));
        _wasMaximized = maximized;

        void Final()
        {
            columns[0].Width = maximized ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            columns[0].MinWidth = maximized ? 0 : Math.Min(ConversationMinWidth, Math.Max(0, available - explorer - editor - 2));
            ConversationColumn.IsVisible = !maximized;
            WorkbenchSplitter.IsVisible = vm.IsWorkbenchOpen && !maximized;
            columns[2].Width = !vm.IsWorkbenchOpen ? new GridLength(0)
                : maximized ? new GridLength(1, GridUnitType.Star)
                : new GridLength(editor);
            columns[2].MinWidth = vm.IsWorkbenchOpen ? EditorMinWidth : 0;
            columns[4].Width = new GridLength(explorer);
            columns[4].MinWidth = vm.IsExplorerOpen ? ExplorerMinWidth : 0;
            EditorColumn.IsVisible = vm.IsWorkbenchOpen;
            ExplorerColumn.IsVisible = vm.IsExplorerOpen;
            ExplorerSplitter.IsVisible = vm.IsExplorerOpen;
            ReleaseFreeze();
        }

        var fromEditor = EditorColumn.IsVisible ? columns[2].ActualWidth : 0;
        var fromExplorer = ExplorerColumn.IsVisible ? columns[4].ActualWidth : 0;
        var toEditor = !vm.IsWorkbenchOpen ? 0 : maximized ? Math.Max(0, available - explorer - 2) : editor;
        if (!animate || !Motion.MotionSettings.IsEnabled || !IsLoaded ||
            (Math.Abs(fromEditor - toEditor) < 1 && Math.Abs(fromExplorer - explorer) < 1))
        {
            _columnMotion?.Snap(1);
            Final();
            return;
        }

        // 动画期间：对话栏照常占剩余宽度，编辑区 / 文件树按像素宽度插值
        columns[0].Width = new GridLength(1, GridUnitType.Star);
        columns[0].MinWidth = 0;
        columns[2].MinWidth = 0;
        columns[4].MinWidth = 0;
        ConversationColumn.IsVisible = true;
        WorkbenchSplitter.IsVisible = vm.IsWorkbenchOpen && !maximized;
        EditorColumn.IsVisible = true;
        ExplorerColumn.IsVisible = true;
        ExplorerSplitter.IsVisible = vm.IsExplorerOpen;
        // 降载：面板内容固定为较大的那个宽度（左对齐、越界部分被相邻栏或窗口边缘遮住），对话区会话视图固定当前宽度，
        // 动画只移动列边界，结束后各自只重新排版一次
        Freeze(EditorColumn, Math.Max(fromEditor, toEditor));
        Freeze(ExplorerColumn, Math.Max(fromExplorer, explorer));
        Freeze(Transcript, Math.Max(Transcript.Bounds.Width, available - toEditor - explorer - 2));

        // 打断进行中的动画：停在当前宽度，从这里继续
        _columnMotion?.Snap(_columnMotion.Value);
        _columnMotion = new Motion.MotionValue(this, 0, p =>
        {
            columns[2].Width = new GridLength(Math.Max(0, fromEditor + (toEditor - fromEditor) * p));
            columns[4].Width = new GridLength(Math.Max(0, fromExplorer + (explorer - fromExplorer) * p));
        });
        _columnMotion.AnimateTo(1, Motion.MotionTokens.Standard, Final);
    }

    private Motion.MotionValue? _columnMotion;
    private bool _wasMaximized;
    private readonly HashSet<Control> _frozen = [];

    private void Freeze(Control control, double width)
    {
        if (width <= 0)
        {
            return;
        }
        control.Width = width;
        if (control != Transcript)
        {
            control.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        }
        _frozen.Add(control);
    }

    private void ReleaseFreeze()
    {
        if (SidebarReveal.IsAnimating || _columnMotion is { IsAnimating: true })
        {
            return;
        }
        foreach (var control in _frozen)
        {
            control.ClearValue(WidthProperty);
            control.ClearValue(HorizontalAlignmentProperty);
        }
        _frozen.Clear();
    }

    private void OnAddPanelClick(object? sender, RoutedEventArgs e) => AddPanelPopup.IsOpen = !AddPanelPopup.IsOpen;

    private void OnAddPanelChosen(object? sender, RoutedEventArgs e) => AddPanelPopup.IsOpen = false;

    private static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>
    /// 全局快捷键（⌘ 在 Windows / Linux 上为 Ctrl）：⌘K 命令面板、⌘N 新对话、⇧⌘N 新建项目、⌘B 切换侧栏、⌘+ ⌘- ⌘0 界面缩放。
    /// 命令面板打开时接管 ↑ ↓ ↵ Esc Backspace。
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        var palette = vm.Palette;
        if (palette.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Up:
                    palette.MoveHighlight(-1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    palette.MoveHighlight(1);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    _ = palette.ExecuteHighlightedAsync();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    palette.Back();
                    e.Handled = true;
                    return;
                case Key.Back when palette.TryBackOnEmptySearch():
                    e.Handled = true;
                    return;
            }
        }
        if (vm.IsSettingsOpen && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            // 先关闭设置页内的弹层（添加厂商），再关闭设置页
            if (vm.Settings.IsPickingTemplate)
            {
                vm.Settings.CloseTemplatePickerCommand.Execute(null);
            }
            else
            {
                vm.CloseSettingsCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }
        // 界面缩放 ⌘+ ⌘- ⌘0：⌘+ 在多数键盘上是 ⇧⌘=，同时接受 ⌘= 与小键盘的 +、-、0
        var modifiers = e.KeyModifiers;
        if (modifiers == CommandModifier || modifiers == (CommandModifier | KeyModifiers.Shift))
        {
            var zoom = e.Key switch
            {
                Key.OemPlus or Key.Add => vm.ZoomInCommand,
                Key.OemMinus or Key.Subtract when modifiers == CommandModifier => vm.ZoomOutCommand,
                Key.D0 or Key.NumPad0 when modifiers == CommandModifier => vm.ZoomResetCommand,
                _ => null,
            };
            if (zoom is not null)
            {
                zoom.Execute(null);
                e.Handled = true;
                return;
            }
        }
        if (e.KeyModifiers == CommandModifier)
        {
            switch (e.Key)
            {
                case Key.K:
                    palette.Toggle();
                    e.Handled = true;
                    break;
                case Key.N:
                    palette.Close();
                    vm.Sidebar.NewSessionCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.B:
                    vm.ToggleSidebarCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemComma:
                    palette.Close();
                    _ = vm.OpenSettingsCommand.ExecuteAsync(null);
                    e.Handled = true;
                    break;
            }
        }
        else if (e.KeyModifiers == (CommandModifier | KeyModifiers.Shift) && e.Key == Key.N)
        {
            palette.OpenNewProject();
            e.Handled = true;
        }
        else if (e.KeyModifiers == (CommandModifier | KeyModifiers.Shift) && e.Key == Key.E)
        {
            vm.ToggleExplorerCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPalettePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && sender is CommandPaletteViewModel { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() => PaletteSearch.Focus(), DispatcherPriority.Input);
        }
        // 进入子页：列表从右侧滑入；返回：从左侧滑入
        if (e.PropertyName == nameof(CommandPaletteViewModel.Breadcrumb) && sender is CommandPaletteViewModel { IsOpen: true } palette)
        {
            TKCode.Desktop.Motion.Presence.PlayEnter(PaletteList,
                palette.HasBreadcrumb ? TKCode.Desktop.Motion.PresenceEffect.SlideLeft : TKCode.Desktop.Motion.PresenceEffect.SlideRight);
        }
    }

    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Palette.Close();
        }
    }

    /// <summary>/add-dir：选择要只给本会话追加的文件夹（docs/design/projects.md §10.4）。</summary>
    private async Task AddSessionDirectoriesAsync(MainWindowViewModel vm)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Get(TKCodeLangResourceKind.AddDirCommandDescription),
            AllowMultiple = true,
        });
        foreach (var path in folders.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            await vm.Conversation.AddSessionDirectoryAsync(path);
        }
    }

    /// <summary>创建项目弹窗（侧栏 +、项目选择器的「新建项目…」、命令面板、⇧⌘N，docs/design/projects.md §8.4）。</summary>
    private async Task CreateProjectAsync()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        vm.Palette.Close();
        await ProjectDialog.ShowAsync(this, null, dialog =>
        {
            vm.Sidebar.CreateProject(dialog.ResultName, dialog.ResultFolders);
            return Task.CompletedTask;
        });
    }

    private async Task EditProjectAsync(Project project)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }
        var outcome = await ProjectDialog.ShowAsync(this, project, dialog =>
        {
            vm.Sidebar.UpdateProject(project, dialog.ResultName, dialog.ResultFolders);
            return Task.CompletedTask;
        });
        if (outcome == ProjectDialogOutcome.RemoveRequested)
        {
            await RemoveProjectAsync(project);
        }
    }

    /// <summary>移除项目：确认后只从项目表移除，文件与会话都保留。</summary>
    private async Task RemoveProjectAsync(Project project)
    {
        if (DataContext is MainWindowViewModel vm &&
            await ConfirmDialog.ShowAsync(this, Strings.Format(TKCodeLangResourceKind.ProjectRemoveTitleFormat, project.Name),
                Strings.Get(TKCodeLangResourceKind.ProjectRemoveText), Strings.Get(TKCodeLangResourceKind.ProjectRemoveAction), danger: true))
        {
            vm.Sidebar.RemoveProject(project);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
