using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;
using TKCode.Desktop.Motion;
using TKCode.Desktop.Localization;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Controls;

/// <summary>
/// An explorer-owned Arc popup. MenuSurface is deliberately static (IsOpen remains false): only this host owns
/// placement, zoom, dismissal and focus restoration. Every open captures a fresh immutable target and fresh rows.
/// </summary>
public sealed class ExplorerContextMenu : IDisposable
{
    private readonly FileExplorerViewModel _explorer;
    private readonly Control _owner;
    private readonly Func<Exception, Task> _onError;
    private PointPopup? _popup;
    private MenuSurface? _surface;
    private Control? _focusTarget;
    private Control? _placementTarget;
    private ExplorerContextTarget? _target;
    private TopLevel? _topLevel;
    private bool _disposed;

    public ExplorerContextMenu(FileExplorerViewModel explorer, Control owner, Func<Exception, Task> onError)
    {
        _explorer = explorer;
        _owner = owner;
        _onError = onError;
        owner.DetachedFromVisualTree += OnOwnerDetached;
    }

    public bool IsOpen => _popup is { IsOpen: true };

    /// <summary>Position is in placementTarget coordinates. A tree row or search-result button receives focus back.</summary>
    public void Show(ExplorerContextTarget target, Control placementTarget, Point position, Control? focusTarget = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Close();
        if (TopLevel.GetTopLevel(placementTarget) is not { } topLevel ||
            _explorer.GetContextActions(target) is not { Count: > 0 } actions)
        {
            return;
        }
        var maxWidth = Math.Max(1, (topLevel.ClientSize.Width - 16) / UiZoom.Scale);
        var surface = new MenuSurface
        {
            Variant = MenuSurfaceVariant.Context,
            IsOpen = false,
            MinWidth = Math.Min(208, maxWidth),
            MaxWidth = maxWidth,
            MaxHeight = Math.Max(1, Math.Min(300, (topLevel.ClientSize.Height - 16) / UiZoom.Scale)),
            ClipToBounds = true,
        };
        int? previousGroup = null;
        foreach (var action in actions)
        {
            var group = Group(target, action);
            if (previousGroup is { } previous && previous != group)
            {
                surface.Items.Add(new MenuEntrySeparator());
            }
            previousGroup = group;
            var entry = new MenuEntry
            {
                Header = Strings.Get(Label(target, action)),
                Icon = Lucide.Geometry(Icon(action)),
                IsDestructive = action == ExplorerAction.Trash,
                IsEnabled = _explorer.CanExecuteAction(target, action),
                CommandParameter = action,
                ToggleType = action == ExplorerAction.ToggleHidden ? MenuEntryToggleType.CheckBox : MenuEntryToggleType.None,
                IsChecked = action == ExplorerAction.ToggleHidden && _explorer.ShowHidden,
            };
            // The host owns dispatch rather than binding a row command: restore focus before any action runs.
            entry.Click += (_, _) =>
            {
                if (ReferenceEquals(_surface, surface) && _explorer.CanExecuteAction(target, action))
                {
                    _ = ExecuteAsync(target, action);
                }
            };
            surface.Items.Add(entry);
        }
        surface.AddHandler(InputElement.KeyDownEvent, OnMenuKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        var popup = new PointPopup(position)
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Pointer,
            MarginToAnchor = 0,
            IsLightDismissEnabled = true,
            OverlayInputPassThroughElement = placementTarget,
            TakesFocusFromNativeControl = true,
            Child = new ZoomHost { Child = surface },
        };
        ((ISetLogicalParent)popup).SetParent(_owner);
        _surface = surface;
        _popup = popup;
        _focusTarget = focusTarget ?? placementTarget;
        _placementTarget = placementTarget;
        _target = target;
        placementTarget.DetachedFromVisualTree += OnOwnerDetached;
        MotionSettings.Changed += UpdateMotion;
        UiZoom.Changed += OnZoomChanged;
        UpdateMotion();
        _topLevel = topLevel;
        topLevel.AddHandler(InputElement.PointerPressedEvent, OnOutsidePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        topLevel.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrolled, RoutingStrategies.Bubble, handledEventsToo: true);
        topLevel.SizeChanged += OnViewportChanged;
        if (topLevel is Window window)
        {
            window.Deactivated += OnWindowDeactivated;
        }
        _explorer.PropertyChanged += OnExplorerChanged;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_popup, popup))
            {
                Close();
            }
            popup.Child = null;
            ((ISetLogicalParent)popup).SetParent(null);
        };
        popup.IsOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_surface, surface) && IsOpen)
            {
                var first = surface.Items.OfType<MenuEntry>().FirstOrDefault(entry => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled);
                if (first is null || !first.Focus(NavigationMethod.Directional))
                {
                    surface.Focus(NavigationMethod.Pointer);
                }
            }
        }, DispatcherPriority.Input);
    }

    public void Close()
    {
        var popup = _popup;
        var surface = _surface;
        var focusTarget = _focusTarget;
        _popup = null;
        _surface = null;
        _focusTarget = null;
        _target = null;
        if (_placementTarget is { } placementTarget)
        {
            placementTarget.DetachedFromVisualTree -= OnOwnerDetached;
            _placementTarget = null;
        }
        MotionSettings.Changed -= UpdateMotion;
        UiZoom.Changed -= OnZoomChanged;
        _explorer.PropertyChanged -= OnExplorerChanged;
        if (_topLevel is { } topLevel)
        {
            topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnOutsidePressed);
            topLevel.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScrolled);
            topLevel.SizeChanged -= OnViewportChanged;
            if (topLevel is Window window)
            {
                window.Deactivated -= OnWindowDeactivated;
            }
            _topLevel = null;
        }
        if (popup is null)
        {
            return;
        }
        if (surface is not null)
        {
            surface.IsHitTestVisible = false;
            // Static MenuSurface can still have a pending pointer-exit focus grace callback.
            // Disable it before focus handoff so a departing animated menu cannot steal focus back.
            surface.IsEnabled = false;
            surface.Focusable = false;
        }
        popup.IsOpen = false;
        if (focusTarget is { IsEffectivelyVisible: true } && TopLevel.GetTopLevel(focusTarget) is not null)
        {
            focusTarget.Focus(NavigationMethod.Pointer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Close();
        _owner.DetachedFromVisualTree -= OnOwnerDetached;
    }

    private async Task ExecuteAsync(ExplorerContextTarget target, ExplorerAction action)
    {
        Close();
        try
        {
            await _explorer.ExecuteActionAsync(target, action);
        }
        catch (Exception ex)
        {
            await _onError(ex);
        }
    }

    private bool IsInside(object? source) => _surface is { } surface && source is Visual visual &&
        (ReferenceEquals(surface, visual) || surface.IsVisualAncestorOf(visual));

    private void OnOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsInside(e.Source))
        {
            Close();
        }
    }

    private void OnScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta != default && !IsInside(e.Source))
        {
            Close();
        }
    }

    private void OnMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Tab)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnViewportChanged(object? sender, SizeChangedEventArgs e) => Close();
    private void OnWindowDeactivated(object? sender, EventArgs e) => Close();
    private void OnOwnerDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close();
    private void OnZoomChanged() => Close();

    private void UpdateMotion()
    {
        var level = MotionSettings.IsEnabled ? ArcMotionLevel.Full : ArcMotionLevel.Off;
        if (_surface is { } surface)
        {
            ArcMotion.SetLevel(surface, level);
        }
        if (_popup is { } popup)
        {
            ArcMotion.SetLevel(popup, level);
        }
    }

    private void OnExplorerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_target is { } captured && _explorer.GetContextActions(captured).Count == 0)
        {
            Close();
            return;
        }
        if (e.PropertyName == nameof(FileExplorerViewModel.IsBusy) && _surface is { } surface && _target is { } target)
        {
            foreach (var entry in surface.Items.OfType<MenuEntry>())
            {
                entry.IsEnabled = entry.CommandParameter is ExplorerAction action && _explorer.CanExecuteAction(target, action);
            }
        }
    }

    private TKCodeLangResourceKind Label(ExplorerContextTarget target, ExplorerAction action) => action switch
    {
        ExplorerAction.Open => TKCodeLangResourceKind.ExplorerOpen,
        ExplorerAction.Reveal => TKCodeLangResourceKind.ExplorerReveal,
        ExplorerAction.NewFile => TKCodeLangResourceKind.ExplorerNewFile,
        ExplorerAction.NewFolder => TKCodeLangResourceKind.ExplorerNewFolder,
        ExplorerAction.ToggleExpanded => IsExpanded(_explorer.RootNodes, target.Path)
            ? TKCodeLangResourceKind.ExplorerCollapse : TKCodeLangResourceKind.ExplorerExpand,
        ExplorerAction.ShowInFileManager => OperatingSystem.IsMacOS() ? TKCodeLangResourceKind.ExplorerShowInFinder
            : OperatingSystem.IsWindows() ? TKCodeLangResourceKind.ExplorerShowInExplorer : TKCodeLangResourceKind.ExplorerShowInFileManager,
        ExplorerAction.OpenTerminal => TKCodeLangResourceKind.ExplorerOpenTerminal,
        ExplorerAction.CopyFullPath => TKCodeLangResourceKind.ExplorerCopyFullPath,
        ExplorerAction.CopyRelativePath => TKCodeLangResourceKind.ExplorerCopyRelativePath,
        ExplorerAction.Rename => TKCodeLangResourceKind.ExplorerRename,
        ExplorerAction.Trash => OperatingSystem.IsWindows() ? TKCodeLangResourceKind.ExplorerRecycle : TKCodeLangResourceKind.ExplorerTrash,
        ExplorerAction.OpenRoot => TKCodeLangResourceKind.ExplorerOpenRoot,
        ExplorerAction.CopyRootPath => TKCodeLangResourceKind.ExplorerCopyRootPath,
        ExplorerAction.Refresh => TKCodeLangResourceKind.ExplorerRefresh,
        ExplorerAction.CollapseAll => TKCodeLangResourceKind.ExplorerCollapseAll,
        ExplorerAction.ToggleHidden => _explorer.ShowHidden ? TKCodeLangResourceKind.ExplorerHideHidden : TKCodeLangResourceKind.ExplorerShowHidden,
        ExplorerAction.RemoveSessionRoot => TKCodeLangResourceKind.RemoveSessionFolder,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static bool IsExpanded(IEnumerable<FileNodeViewModel> nodes, string path) => nodes.Any(node =>
        (node.FullPath == path && node.IsExpanded) || IsExpanded(node.Children, path));

    private static int Group(ExplorerContextTarget target, ExplorerAction action) => action switch
    {
        ExplorerAction.NewFile or ExplorerAction.NewFolder or ExplorerAction.Open => 0,
        ExplorerAction.Reveal => target.IsDirectory ? 1 : 0,
        ExplorerAction.ToggleExpanded => 1,
        ExplorerAction.ShowInFileManager or ExplorerAction.OpenTerminal or ExplorerAction.OpenRoot or ExplorerAction.CopyRootPath => 2,
        ExplorerAction.CopyFullPath or ExplorerAction.CopyRelativePath => 3,
        ExplorerAction.Rename or ExplorerAction.Trash => 4,
        ExplorerAction.Refresh or ExplorerAction.CollapseAll or ExplorerAction.ToggleHidden => 3,
        ExplorerAction.RemoveSessionRoot => 4,
        _ => 0,
    };

    private static LucideIconKind Icon(ExplorerAction action) => action switch
    {
        ExplorerAction.Open => LucideIconKind.File,
        ExplorerAction.Reveal => LucideIconKind.FolderSearch,
        ExplorerAction.NewFile => LucideIconKind.FilePlus,
        ExplorerAction.NewFolder => LucideIconKind.FolderPlus,
        ExplorerAction.ToggleExpanded => LucideIconKind.ChevronsUpDown,
        ExplorerAction.ShowInFileManager or ExplorerAction.OpenRoot => LucideIconKind.FolderOpen,
        ExplorerAction.OpenTerminal => LucideIconKind.SquareTerminal,
        ExplorerAction.CopyFullPath or ExplorerAction.CopyRelativePath or ExplorerAction.CopyRootPath => LucideIconKind.Copy,
        ExplorerAction.Rename => LucideIconKind.Pencil,
        ExplorerAction.Trash => LucideIconKind.Trash2,
        ExplorerAction.Refresh => LucideIconKind.RefreshCw,
        ExplorerAction.CollapseAll => LucideIconKind.FoldVertical,
        ExplorerAction.ToggleHidden => LucideIconKind.Eye,
        ExplorerAction.RemoveSessionRoot => LucideIconKind.FolderMinus,
        _ => LucideIconKind.File,
    };

    private sealed class PointPopup(Point position) : ArcPopup
    {
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            // ArcPopup resolves motion from PlacementTarget, which can be a plain search button rather than our tree.
            // Keep the project reduce-motion switch authoritative without changing that button's inherited settings.
            if (change.Property != IsMotionEnabledProperty && !MotionSettings.IsEnabled)
            {
                IsMotionEnabled = false;
            }
        }

        protected override void AdjustPlacementAnchor(CustomPopupPlacement placement, ref bool flipX, ref bool flipY)
        {
            if (PlacementTarget is { } target && TopLevel.GetTopLevel(target) is { } topLevel &&
                target.TranslatePoint(position, topLevel) is { } client)
            {
                client = new Point(Math.Clamp(client.X, 8, Math.Max(8, topLevel.ClientSize.Width - 8)),
                    Math.Clamp(client.Y, 8, Math.Max(8, topLevel.ClientSize.Height - 8)));
                placement.AnchorRectangle = new Rect(client, new Size(1, 1));
                var size = placement.PopupSize.Deflate(placement.Deflate);
                flipX = client.X + size.Width > topLevel.ClientSize.Width && client.X >= size.Width;
                flipY = client.Y + size.Height > topLevel.ClientSize.Height && client.Y >= size.Height;
            }
        }

        protected override void OnPlacementResolved(CustomPopupPlacement placement, Thickness shadow, double marginToAnchor,
            bool isOverlayHost, bool flipX, bool flipY)
        {
            placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.FlipY |
                PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY;
        }
    }
}
