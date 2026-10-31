namespace TKCode.Desktop.ViewModels;

public enum AppNoticeKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>界面右下角的短提示（主窗口用 Arc 的 Toast 显示）；可带一个操作按钮。</summary>
public sealed record AppNotice(string Title, AppNoticeKind Kind, string? ActionLabel = null, Action? Action = null);
