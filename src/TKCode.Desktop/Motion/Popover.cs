using Avalonia.Media;
using TKCode.Arc.Primitives;

namespace TKCode.Desktop.Motion;

/// <summary>
/// TKCode 的弹层：Arc 的 <see cref="ArcPopup"/>（关闭会等退出动效播完，进出场方向随放置边自动确定）。
/// 外观（背景、描边、阴影）由内容里的 Border.menu 负责，所以关掉弹层自带的阴影。
/// </summary>
public class Popover : ArcPopup
{
    public Popover()
    {
        // 与触发器的间距由各处的 HorizontalOffset / VerticalOffset 明确给出，不再额外加 Arc 的默认间距
        MarginToAnchor = 0;
        PopupRootShadow = default(BoxShadows);
        OverlayHostShadow = default(BoxShadows);
    }
}
