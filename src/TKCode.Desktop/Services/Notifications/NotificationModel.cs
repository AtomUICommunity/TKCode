using TKCode.Config;

namespace TKCode.Desktop.Services.Notifications;

/// <summary>通知档位（settings.json 的 notifications.turnCompleted / needsInput）。</summary>
public enum NotifyWhen
{
    Never,

    /// <summary>只在会话处于后台时（窗口不在前台，或显示的不是这个会话；docs/design/notifications.md §4.3）。</summary>
    Background,

    Always,
}

/// <summary>通知相关的设置（已套用默认值）。</summary>
public sealed record NotificationPreferences(NotifyWhen TurnCompleted, NotifyWhen NeedsInput, bool Sound, TimeSpan MinTurnDuration)
{
    /// <summary>成功结束的回合至少运行多久才通知（Cherry Studio 的 30 秒）。</summary>
    public static readonly TimeSpan DefaultMinTurnDuration = TimeSpan.FromSeconds(30);

    public static NotificationPreferences Default { get; } = new(NotifyWhen.Background, NotifyWhen.Background, true, DefaultMinTurnDuration);

    public static NotificationPreferences From(NotificationSettings? settings) => new(
        Parse(settings?.TurnCompleted),
        Parse(settings?.NeedsInput),
        settings?.Sound != false,
        settings?.MinTurnSeconds is { } seconds ? TimeSpan.FromSeconds(Math.Max(0, seconds)) : DefaultMinTurnDuration);

    /// <summary>settings.json 的取值；不认识的值按默认的 background 处理（SettingsFile 已给出警告）。</summary>
    public static NotifyWhen Parse(string? value) => value switch
    {
        "never" => NotifyWhen.Never,
        "always" => NotifyWhen.Always,
        _ => NotifyWhen.Background,
    };

    public static string ToValue(NotifyWhen when) => when switch
    {
        NotifyWhen.Never => "never",
        NotifyWhen.Always => "always",
        _ => "background",
    };
}

public enum AttentionKind
{
    /// <summary>回合正常结束。</summary>
    TurnCompleted,

    /// <summary>回合以错误结束。</summary>
    TurnFailed,

    /// <summary>用户停止了回合：不通知，也不算未读。</summary>
    TurnAborted,

    /// <summary>工具审批（tool/approve）。</summary>
    Approval,

    /// <summary>Agent 提问（input/request）。</summary>
    Question,

    /// <summary>计划审批（plan/review）。</summary>
    PlanReview,

    /// <summary>MCP elicitation 表单。</summary>
    Elicitation,
}

/// <summary>某个会话需要用户注意的一件事。</summary>
/// <param name="Title">会话标题；没有时为项目名（由调用方决定）。</param>
/// <param name="Detail">
/// TurnCompleted：最后一段助手正文；TurnFailed：错误信息；Approval：命令 / 路径 / 工具名；Elicitation：MCP 服务器名。其余为 null。
/// </param>
/// <param name="Duration">回合的运行时长（只对回合事件有意义）。</param>
public sealed record SessionAttention(string SessionId, string Title, AttentionKind Kind, string? Detail = null, TimeSpan Duration = default)
{
    /// <summary>属于「需要你处理」，否则属于「回合结束」。</summary>
    public bool NeedsInput => Kind is AttentionKind.Approval or AttentionKind.Question or AttentionKind.PlanReview or AttentionKind.Elicitation;
}

/// <summary>交给系统显示的一条通知。同一会话的通知在系统通知中心里互相替换。</summary>
/// <param name="Sound">这一条要不要出声（已按提示音设置与节流决定）。</param>
public sealed record SystemNotification(string SessionId, string Title, string Body, bool Sound);

/// <summary>
/// 平台的系统通知（docs/design/notifications.md §5）。所有方法都在 UI 线程调用；实现不得抛出异常，失败只写日志。
/// </summary>
public interface ISystemNotifier : IDisposable
{
    /// <summary>显示（或替换同一会话的上一条）。<see cref="SystemNotification.Sound"/> 为 true 时由实现负责出声：系统通知自己会响就不再另外播放。</summary>
    void Show(SystemNotification notification);

    /// <summary>从系统通知中心撤回该会话的通知（用户已经打开了它）。</summary>
    void Withdraw(string sessionId);

    /// <summary>未读的会话数：macOS Dock 角标、Windows 任务栏覆盖图标；不支持的平台什么也不做。</summary>
    void SetBadge(int count);

    /// <summary>用户点击了某个会话的通知。可能在任意线程触发。</summary>
    event Action<string>? Activated;
}

/// <summary>用户此刻在看什么（docs/design/notifications.md §4.3）。</summary>
public interface IAppPresence
{
    /// <summary>主窗口可见、未最小化、是活动窗口。</summary>
    bool IsForeground { get; }

    /// <summary>主区域显示的会话；设置页打开或空状态时为 null。</summary>
    string? VisibleSessionId { get; }
}

/// <summary>不支持的平台与测试：什么也不做。</summary>
public sealed class NullNotifier : ISystemNotifier
{
    public void Show(SystemNotification notification)
    {
    }

    public void Withdraw(string sessionId)
    {
    }

    public void SetBadge(int count)
    {
    }

    public event Action<string>? Activated
    {
        add { }
        remove { }
    }

    public void Dispose()
    {
    }
}
