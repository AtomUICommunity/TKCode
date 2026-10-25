using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Config;
using TKCode.Desktop.Services;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;
using TKCode.Desktop.Motion;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 界面缩放的当前比例（settings.json 的 uiScale）。<see cref="ZoomHost"/> 订阅它；需要按比例换算窗口外壳尺寸的代码也订阅它。
/// 与 <c>MotionSettings</c> 一样是进程内的静态状态：弹层内容在独立的可视树里，沿用静态值比沿树继承更直接。
/// </summary>
public static class UiZoom
{
    private static double _scale = UiScaleLevels.Default;

    public static event Action? Changed;

    public static double Scale
    {
        get => _scale;
        set
        {
            value = UiScaleLevels.Clamp(value);
            if (Math.Abs(_scale - value) > 1e-9)
            {
                _scale = value;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>跟随 settings.json 的 uiScale：立即同步一次，此后设置页、快捷键或外部编辑改了文件都会同步。</summary>
    public static void Follow(UserSettingsService settings)
    {
        Scale = settings.UiScale;
        settings.Changed += () => Scale = settings.UiScale;
    }
}

/// <summary>
/// 把内容按 <see cref="UiZoom.Scale"/> 等比缩放：先按缩放后的可用空间排版，再整体放大，所以文字保持矢量清晰、布局不会错位。
/// 主窗口的内容和每个菜单 / 选择器弹层的内容各包一层——弹层是独立窗口，外层的缩放覆盖不到。
/// 同一层也把 <see cref="MotionSettings"/> 传给 Arc 控件（<see cref="ArcMotion.LevelProperty"/> 沿树继承，Arc 弹层也从这里继承）：
/// 减少动态效果、系统设置或测试关闭动效时，Arc 的开关、复选框、收起 / 展开等同样直接跳到终态。
/// </summary>
public sealed class ZoomHost : LayoutTransformControl
{
    protected override Type StyleKeyOverride => typeof(LayoutTransformControl);

    public ZoomHost()
    {
        Apply();
        ApplyMotion();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UiZoom.Changed += Apply;
        MotionSettings.Changed += ApplyMotion;
        Apply();
        ApplyMotion();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UiZoom.Changed -= Apply;
        MotionSettings.Changed -= ApplyMotion;
        base.OnDetachedFromVisualTree(e);
    }

    private void ApplyMotion() => ArcMotion.SetLevel(this, MotionSettings.IsEnabled ? ArcMotionLevel.Full : ArcMotionLevel.Off);

    private void Apply()
    {
        var scale = UiZoom.Scale;
        LayoutTransform = UiScaleLevels.IsDefault(scale) ? null : new ScaleTransform(scale, scale);
        // Arc 的弹层画在窗口覆盖层里（在这层缩放之外）：继承这个值，它们按同样的比例排版
        ArcZoom.SetScale(this, scale);
    }
}
