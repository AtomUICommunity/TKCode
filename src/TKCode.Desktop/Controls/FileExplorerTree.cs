using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.Motion;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Motion;
using TKCode.Desktop.ViewModels;
using ArcTreeView = TKCode.Arc.TreeView;

namespace TKCode.Desktop.Controls;

/// <summary>A resolved row identity (null only for genuine blank tree space) and the exact focus/placement anchor.</summary>
public sealed class ExplorerContextRequestedEventArgs(FileNodeViewModel? node, Point position, Control focusTarget) : EventArgs
{
    public FileNodeViewModel? Node { get; } = node;
    public Point Position { get; } = position;
    public Control FocusTarget { get; } = focusTarget;
}

/// <summary>Arc's virtualized tree, with filesystem loading and editor integration kept in the explorer model.</summary>
public sealed class FileExplorerTree : ArcTreeView
{
    public static readonly StyledProperty<FileExplorerViewModel?> ExplorerProperty =
        AvaloniaProperty.Register<FileExplorerTree, FileExplorerViewModel?>(nameof(Explorer));

    private FileExplorerViewModel? _model;
    private TreeSource? _source;
    private readonly HashSet<FileNodeViewModel> _expandedNodes = [];
    private bool _attached;
    private bool _syncing;
    private bool _restorePosted;

    protected override Type StyleKeyOverride => typeof(ArcTreeView);

    public FileExplorerViewModel? Explorer
    {
        get => GetValue(ExplorerProperty);
        set => SetValue(ExplorerProperty, value);
    }

    public new event EventHandler<ExplorerContextRequestedEventArgs>? ContextRequested;

    public FileExplorerTree()
    {
        // Public generated token keys are scoped to this tree; other Arc trees retain their default density.
        Resources[TreeViewTokenKind.RowSlotHeight] = 26d;
        Resources[TreeViewTokenKind.IndentWidth] = 14d;
        Padding = new Thickness(2, 0, 2, 2);
        FontSize = 12;
        AddHandler(PointerPressedEvent, OnContextPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnContextKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChildrenSelector = item => ((FileNodeViewModel)item).Children;
        HeaderSelector = item => ((FileNodeViewModel)item).Name;
        IconSelector = IconFor;
        ItemExpanded += (_, e) =>
        {
            if (e.Item is FileNodeViewModel { IsDirectory: true, IsPlaceholder: false } node)
            {
                _expandedNodes.Add(node);
                if (!_syncing && _model is { } model)
                {
                    _ = model.ExpandAsync(node);
                }
            }
        };
        ItemCollapsed += (_, e) =>
        {
            if (e.Item is FileNodeViewModel node)
            {
                _expandedNodes.Remove(node);
                if (!_syncing)
                {
                    _model?.Collapse(node);
                }
            }
        };
        ItemInvoked += (_, e) =>
        {
            // Arc itself toggles folders after invoking the row. Forwarding directory activation would toggle twice.
            if (e.Item is FileNodeViewModel { IsDirectory: false, IsPlaceholder: false } node)
            {
                _model?.ActivateCommand.Execute(node);
            }
        };
        SelectionChanged += (_, e) =>
        {
            if (!_syncing && _model is { } model)
            {
                if (e.NewItem is FileNodeViewModel { IsPlaceholder: true })
                {
                    SyncSelection();
                }
                else
                {
                    model.SelectedNode = e.NewItem as FileNodeViewModel;
                }
            }
        };
        UpdateMotion();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ExplorerProperty && _attached)
        {
            AttachModel(Explorer);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        MotionSettings.Changed += UpdateMotion;
        UpdateMotion();
        AttachModel(Explorer);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        MotionSettings.Changed -= UpdateMotion;
        AttachModel(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachModel(FileExplorerViewModel? model)
    {
        ClearExpansion();
        if (_model is { } previous)
        {
            previous.PropertyChanged -= OnModelChanged;
            previous.RootNodes.CollectionChanged -= OnRootsChanged;
            previous.ExpansionChanged -= OnExpansionChanged;
            previous.RevealRequested -= OnRevealRequested;
            previous.ChildrenLoaded -= OnChildrenLoaded;
        }
        _model = model;
        _source = model is null ? null : new TreeSource(model.RootNodes);
        _syncing = true;
        try
        {
            SelectedItem = null;
            ItemsSource = _source;
        }
        finally
        {
            _syncing = false;
        }
        if (model is not null)
        {
            model.PropertyChanged += OnModelChanged;
            model.RootNodes.CollectionChanged += OnRootsChanged;
            model.ExpansionChanged += OnExpansionChanged;
            model.RevealRequested += OnRevealRequested;
            model.ChildrenLoaded += OnChildrenLoaded;
            PostRestore();
        }
    }

    private void OnRootsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ClearExpansion();
        }
        _source?.Notify(e);
        PostRestore();
    }

    private void OnChildrenLoaded(FileNodeViewModel node)
    {
        // Arc observes child collections only while expanded. Empty/collapsed loads also need fresh leaf metadata.
        _source?.Notify(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        SyncSelection();
    }

    private void ClearExpansion()
    {
        _syncing = true;
        try
        {
            // Arc retains expansion keys across source changes. Release old node identities through its public API.
            foreach (var node in _expandedNodes.ToArray())
            {
                Collapse(node);
            }
            _expandedNodes.Clear();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileExplorerViewModel.SelectedNode) or nameof(FileExplorerViewModel.IsSearching))
        {
            SyncSelection();
        }
    }

    private void OnExpansionChanged(FileNodeViewModel node)
    {
        if (_syncing)
        {
            return;
        }
        _syncing = true;
        try
        {
            if (node.IsExpanded)
            {
                Expand(node);
            }
            else
            {
                Collapse(node);
            }
        }
        finally
        {
            _syncing = false;
        }
        SyncSelection();
    }

    private void OnRevealRequested(FileNodeViewModel node)
    {
        RestoreExpansion();
        SyncSelection();
        var model = _model;
        // Collection changes rebuild Arc rows at Background priority; scroll only after that rebuild has settled.
        Dispatcher.UIThread.Post(() =>
        {
            if (_attached && model == _model && model is { IsSearching: false } && ReferenceEquals(model.SelectedNode, node)
                && IsVisibleInModel(model.RootNodes, node))
            {
                ScrollIntoView(node);
            }
        }, DispatcherPriority.Background);
    }

    private void PostRestore()
    {
        if (_restorePosted)
        {
            return;
        }
        _restorePosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _restorePosted = false;
            if (_attached)
            {
                RestoreExpansion();
                SyncSelection();
            }
        }, DispatcherPriority.Background);
    }

    private void RestoreExpansion()
    {
        if (_model is null)
        {
            return;
        }
        _syncing = true;
        try
        {
            foreach (var root in _model.RootNodes)
            {
                Restore(root);
            }
        }
        finally
        {
            _syncing = false;
        }
        void Restore(FileNodeViewModel node)
        {
            if (!node.IsExpanded || node.IsPlaceholder)
            {
                return;
            }
            Expand(node);
            foreach (var child in node.Children)
            {
                Restore(child);
            }
        }
    }

    private void SyncSelection()
    {
        if (_syncing)
        {
            return;
        }
        _syncing = true;
        try
        {
            var model = _model;
            var selected = model is { IsSearching: false } ? model.SelectedNode : null;
            // Setting Arc selection to a hidden cached node silently opens its ancestors. Selection alone must
            // not undo an explicit collapse; retain it in the model and restore its highlight when visible again.
            SelectedItem = selected is not null && model is not null && IsVisibleInModel(model.RootNodes, selected)
                ? selected : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static bool IsVisibleInModel(IEnumerable<FileNodeViewModel> nodes, FileNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, target) || (node.IsExpanded && IsVisibleInModel(node.Children, target)))
            {
                return true;
            }
        }
        return false;
    }

    private void OnContextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
        {
            return;
        }
        // Tunnel before Arc's row handlers: context selection must never invoke a file or toggle a branch.
        e.Handled = true;
        if (e.Source is not Visual source || IsScrollbar(source))
        {
            return;
        }
        var row = FindTreeItem(source);
        if (row is not null)
        {
            RequestRowContext(row, e.GetPosition(this));
        }
        else if ((ReferenceEquals(source, this) || this.IsVisualAncestorOf(source)) && new Rect(Bounds.Size).Contains(e.GetPosition(this)))
        {
            ContextRequested?.Invoke(this, new ExplorerContextRequestedEventArgs(null, e.GetPosition(this), this));
        }
    }

    private void OnContextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Apps && !(e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        {
            return;
        }
        e.Handled = true;
        // Keyboard navigation deliberately moves focus without selection. Never fall back to SelectedItem.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused &&
            FindTreeItem(focused) is { } row && row.TranslatePoint(new Point(0, row.Bounds.Height), this) is { } position)
        {
            RequestRowContext(row, position);
        }
    }

    private void RequestRowContext(Control row, Point position)
    {
        if (_model is not { } model || !IsLiveRow(row) ||
            ControlAutomationPeer.CreatePeerForElement(row) is not ISelectionItemProvider selection)
        {
            return;
        }
        // ISelectionItemProvider is the public bridge from a pooled Arc row to its current data identity.
        // Names, DataContext, row indexes and implementation types cannot identify virtualized rows reliably.
        var previous = SelectedItem;
        _syncing = true;
        FileNodeViewModel? node;
        try
        {
            selection.Select();
            node = SelectedItem as FileNodeViewModel;
            if (node is null || node.IsPlaceholder || !IsVisibleInModel(model.RootNodes, node))
            {
                SelectedItem = previous;
                return;
            }
        }
        finally
        {
            _syncing = false;
        }
        model.SelectedNode = node;
        row.Focus(NavigationMethod.Pointer);
        ContextRequested?.Invoke(this, new ExplorerContextRequestedEventArgs(node, position, row));
    }

    private Control? FindTreeItem(Visual source)
    {
        for (Visual? visual = source; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            if (visual is Control control &&
                ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationControlType() == AutomationControlType.TreeItem)
            {
                return control;
            }
        }
        return null;
    }

    private bool IsLiveRow(Control row)
    {
        if (!row.IsEffectivelyVisible || !this.IsVisualAncestorOf(row) || row.Bounds.Width <= 0 || row.Bounds.Height <= 0 ||
            row.TranslatePoint(default, this) is not { } origin)
        {
            return false;
        }
        // Recycled and overscan rows can remain attached. Require intersection with the actual scroll viewport.
        var viewport = row.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault();
        return viewport is not null && viewport.TranslatePoint(default, this) is { } viewportOrigin &&
            new Rect(origin, row.Bounds.Size).Intersects(new Rect(viewportOrigin, viewport.Bounds.Size));
    }

    private bool IsScrollbar(Visual source) => source.GetVisualAncestors().Prepend(source)
        .TakeWhile(visual => !ReferenceEquals(visual, this)).Any(visual => visual is ScrollBar);

    private void UpdateMotion() => ArcMotion.SetLevel(this, MotionSettings.IsEnabled ? ArcMotionLevel.Full : ArcMotionLevel.Off);

    private static Geometry? IconFor(object item)
    {
        var node = (FileNodeViewModel)item;
        if (node.IsPlaceholder || (node.IsDirectory && node.Children.Count > 0))
        {
            return null; // Preserve Arc's animated folder / folder-open glyphs for branches.
        }
        var kind = node.Icon switch
        {
            LucideKind.Folder => LucideIconKind.Folder,
            LucideKind.FileText => LucideIconKind.FileText,
            LucideKind.FileCode => LucideIconKind.FileCode,
            LucideKind.Braces => LucideIconKind.Braces,
            LucideKind.Layers => LucideIconKind.Layers,
            LucideKind.Box => LucideIconKind.Box,
            LucideKind.GitBranch => LucideIconKind.GitBranch,
            LucideKind.Settings => LucideIconKind.Settings,
            LucideKind.Database => LucideIconKind.Database,
            LucideKind.Image => LucideIconKind.Image,
            LucideKind.SquareTerminal => LucideIconKind.SquareTerminal,
            _ => LucideIconKind.File,
        };
        return Lucide.Geometry(kind);
    }

    /// <summary>Forward root mutations and allow metadata invalidation without resetting Arc's source or focus.</summary>
    private sealed class TreeSource(IEnumerable<FileNodeViewModel> nodes) : IEnumerable<FileNodeViewModel>, INotifyCollectionChanged
    {
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        public IEnumerator<FileNodeViewModel> GetEnumerator() => nodes.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Notify(NotifyCollectionChangedEventArgs change) => CollectionChanged?.Invoke(this, change);
    }
}
