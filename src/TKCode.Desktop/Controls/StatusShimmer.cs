using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Desktop.Motion;

namespace TKCode.Desktop.Controls;

/// <summary>Arc's status text, following the application's motion policy only while attached.</summary>
public sealed class StatusShimmer : TextShimmer
{
    protected override Type StyleKeyOverride => typeof(TextShimmer);

    public StatusShimmer() => UpdateMotion();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionSettings.Changed += UpdateMotion;
        UpdateMotion();
        base.OnAttachedToVisualTree(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionSettings.Changed -= UpdateMotion;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateMotion() => ArcMotion.SetLevel(this,
        MotionSettings.IsEnabled ? ArcMotionLevel.Full : ArcMotionLevel.Off);
}
