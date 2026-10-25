using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;
using TKCode.Desktop.Motion;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 跟随界面缩放的 Arc 模态框：把 <see cref="DialogSurface"/> 包进 <see cref="ZoomHost"/>，经 <see cref="ModalPresence"/> 显示在窗口的覆盖层上。
/// 尺寸不超过窗口（按 uiScale 换算），动效跟随设置；<see cref="IsBusy"/> 期间 Esc、遮罩、关闭按钮与关闭窗口都不会关闭它。
/// 创建项目、移除确认与文件树的新建 / 重命名共用（docs/design/projects.md §9.3）。
/// </summary>
public sealed class ScaledModal
{
    private readonly DialogSurface _surface;
    private readonly ZoomHost _zoom;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ModalPresence? _presence;
    private Window? _owner;

    public ScaledModal(DialogSurface surface)
    {
        _surface = surface;
        _zoom = new ZoomHost
        {
            Child = surface,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(16),
        };
        surface.CloseRequested += (_, _) => Cancel();
    }

    /// <summary>提交进行中：不允许以任何方式关闭，等待明确的结果。</summary>
    public bool IsBusy { get; set; }

    /// <summary>模态框关闭（含窗口关闭）后完成。</summary>
    public Task Completion => _closed.Task;

    /// <summary>显示；窗口的覆盖层不可用时立即完成。</summary>
    public void Show(Window owner, IInputElement? initialFocus = null)
    {
        _owner = owner;
        UpdateMotion();
        UpdateSize();
        MotionSettings.Changed += UpdateMotion;
        UiZoom.Changed += UpdateSize;
        owner.SizeChanged += OnOwnerSizeChanged;
        owner.Closing += OnOwnerClosing;
        owner.Closed += OnOwnerClosed;
        _presence = new ModalPresence(_zoom, owner) { InitialFocus = initialFocus };
        _presence.Dismissing += (_, e) => e.Cancel = IsBusy;
        _presence.Closed += (_, _) => Complete();
        if (!_presence.Open())
        {
            Complete();
        }
    }

    /// <summary>完成后关闭（不经过 <see cref="IsBusy"/> 检查）。</summary>
    public void Close() => _presence?.Close();

    /// <summary>用户取消（取消按钮、关闭按钮）；提交进行中时忽略。</summary>
    public void Cancel()
    {
        if (!IsBusy)
        {
            _presence?.Dismiss(DismissReason.Programmatic);
        }
    }

    /// <summary>
    /// 不超过窗口（按 uiScale 换算，两侧各留 16）。本地 MaxWidth 会盖过 Arc 主题按 <see cref="DialogSurface.Size"/> 设的宽度，
    /// 所以这里取两者中较小的一个（预设宽度见 <see cref="DialogSurface.SizeProperty"/> 的说明）。
    /// </summary>
    private void UpdateSize()
    {
        if (_owner is not { } owner)
        {
            return;
        }
        var preset = _surface.Size switch
        {
            ControlSize.Small => 360,
            ControlSize.Large => 560,
            _ => 440,
        };
        _surface.MaxWidth = Math.Max(100, Math.Min(preset, owner.ClientSize.Width / UiZoom.Scale - 32));
        _surface.MaxHeight = Math.Max(100, owner.ClientSize.Height / UiZoom.Scale - 32);
    }

    private void UpdateMotion() => ArcMotion.SetLevel(_zoom, MotionSettings.IsEnabled ? ArcMotionLevel.Full : ArcMotionLevel.Off);

    private void OnOwnerSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateSize();

    private void OnOwnerClosing(object? sender, WindowClosingEventArgs e)
    {
        // 原生操作开始后等待明确的结果，而不是报告取消
        if (IsBusy)
        {
            e.Cancel = true;
        }
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Complete();

    private void Complete()
    {
        MotionSettings.Changed -= UpdateMotion;
        UiZoom.Changed -= UpdateSize;
        if (_owner is { } owner)
        {
            owner.SizeChanged -= OnOwnerSizeChanged;
            owner.Closing -= OnOwnerClosing;
            owner.Closed -= OnOwnerClosed;
            _owner = null;
        }
        _presence?.Dispose();
        _presence = null;
        _closed.TrySetResult();
    }
}
