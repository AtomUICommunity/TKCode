namespace TKCode.Desktop.Services.Notifications;

/// <summary>
/// 决定什么时候发系统通知、合并连续到达的通知、记录未读的会话（docs/design/notifications.md §4）。
/// 不依赖 Avalonia：时间来自 <see cref="TimeProvider"/>，计时器回调经注入的 post 回到 UI 线程，平台通知在 <see cref="ISystemNotifier"/> 后面。
/// 除构造外的所有成员都在 UI 线程调用。
/// </summary>
public sealed class NotificationCenter : IDisposable
{
    /// <summary>同一会话在这段时间内的后续通知合并为一条（最新的那条），提示音在这段时间内也只响一次。</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(2);

    /// <summary>「发送测试通知」使用的会话 id（点击它只会显示窗口）。</summary>
    public const string TestSessionId = "tkcode-test";

    private readonly ISystemNotifier _notifier;
    private readonly Func<NotificationPreferences> _preferences;
    private readonly IAppPresence _presence;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly HashSet<string> _unread = new(StringComparer.Ordinal);
    /// <summary>在系统通知中心里还留着通知的会话（打开会话时撤回）。</summary>
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastDelivery = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastSound;
    private bool _disposed;

    /// <param name="post">把计时器回调与平台点击回调送回 UI 线程；测试中直接执行。</param>
    public NotificationCenter(ISystemNotifier notifier, Func<NotificationPreferences> preferences, IAppPresence presence,
        TimeProvider? time = null, Action<Action>? post = null)
    {
        _notifier = notifier;
        _preferences = preferences;
        _presence = presence;
        _time = time ?? TimeProvider.System;
        _post = post ?? (action => action());
        _notifier.Activated += OnActivated;
    }

    /// <summary>未读的会话数变化（托盘据此更新提示文字与图标）。</summary>
    public event Action<int>? UnreadChanged;

    /// <summary>用户点击了某个会话的通知（已在 UI 线程）。</summary>
    public event Action<string>? OpenRequested;

    public int UnreadCount => _unread.Count;

    public bool IsUnread(string sessionId) => _unread.Contains(sessionId);

    /// <summary>会话在后台：窗口不在前台，或显示的不是它。</summary>
    public bool IsBackground(string sessionId) => !_presence.IsForeground || _presence.VisibleSessionId != sessionId;

    /// <summary>某个会话需要注意：记未读，并按设置决定是否发通知（§4.2）。</summary>
    public void Report(SessionAttention attention)
    {
        if (_disposed || attention.Kind == AttentionKind.TurnAborted)
        {
            return;
        }
        var background = IsBackground(attention.SessionId);
        if (background && _unread.Add(attention.SessionId))
        {
            UnreadUpdated();
        }
        var preferences = _preferences();
        var when = attention.NeedsInput ? preferences.NeedsInput : preferences.TurnCompleted;
        if (when == NotifyWhen.Never || (when == NotifyWhen.Background && !background))
        {
            return;
        }
        if (attention.Kind == AttentionKind.TurnCompleted && attention.Duration < preferences.MinTurnDuration)
        {
            return;
        }
        var notification = new SystemNotification(attention.SessionId, NotificationText.Title(attention), NotificationText.Body(attention), preferences.Sound);
        Enqueue(notification, when);
    }

    /// <summary>
    /// 窗口激活 / 失活、显示 / 隐藏、切换会话、开关设置页之后调用：用户正看着的会话不再未读，撤回它的通知，丢弃待发的合并通知。
    /// </summary>
    public void PresenceChanged()
    {
        if (_presence.IsForeground && _presence.VisibleSessionId is { } visible)
        {
            Seen(visible);
        }
    }

    /// <summary>会话被删除：不再计入未读，撤回它的通知。</summary>
    public void Forget(string sessionId)
    {
        Seen(sessionId);
        _lastDelivery.Remove(sessionId);
    }

    /// <summary>设置页的「发送测试通知」：不看档位与前后台，用来确认系统权限。</summary>
    public void SendTest(string title, string body)
    {
        var notification = new SystemNotification(TestSessionId, title, body, _preferences().Sound);
        _lastDelivery[TestSessionId] = _time.GetUtcNow();
        if (notification.Sound)
        {
            _lastSound = _time.GetUtcNow();
        }
        _notifier.Show(notification);
    }

    private void Seen(string sessionId)
    {
        CancelPending(sessionId);
        if (_delivered.Remove(sessionId))
        {
            _notifier.Withdraw(sessionId);
        }
        if (_unread.Remove(sessionId))
        {
            UnreadUpdated();
        }
    }

    private void UnreadUpdated()
    {
        _notifier.SetBadge(_unread.Count);
        UnreadChanged?.Invoke(_unread.Count);
    }

    /// <summary>先发后合并：距上一次投递不到 <see cref="CoalesceWindow"/> 时只保留最新一条，到时间再投递。</summary>
    private void Enqueue(SystemNotification notification, NotifyWhen when)
    {
        var now = _time.GetUtcNow();
        if (_lastDelivery.TryGetValue(notification.SessionId, out var last) && now - last < CoalesceWindow)
        {
            if (_pending.TryGetValue(notification.SessionId, out var pending))
            {
                pending.Notification = notification;
                pending.When = when;
                return;
            }
            var created = new Pending(notification, when);
            created.Timer = _time.CreateTimer(_ => _post(() => Flush(created)), null, last + CoalesceWindow - now, Timeout.InfiniteTimeSpan);
            _pending[notification.SessionId] = created;
            return;
        }
        Deliver(notification);
    }

    private void Flush(Pending pending)
    {
        var sessionId = pending.Notification.SessionId;
        if (_disposed || !_pending.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, pending))
        {
            return;
        }
        _pending.Remove(sessionId);
        pending.Timer?.Dispose();
        // 这期间用户切到了这个会话：「仅后台」的通知不再需要
        if (pending.When == NotifyWhen.Background && !IsBackground(sessionId))
        {
            return;
        }
        // 合并后补发的这条替换刚才那条，不再出声
        Deliver(pending.Notification, allowSound: false);
    }

    private void Deliver(SystemNotification notification, bool allowSound = true)
    {
        var now = _time.GetUtcNow();
        var sound = allowSound && notification.Sound && (_lastSound is not { } lastSound || now - lastSound >= CoalesceWindow);
        if (sound)
        {
            _lastSound = now;
        }
        _lastDelivery[notification.SessionId] = now;
        _delivered.Add(notification.SessionId);
        _notifier.Show(notification with { Sound = sound });
    }

    private void CancelPending(string sessionId)
    {
        if (_pending.Remove(sessionId, out var pending))
        {
            pending.Timer?.Dispose();
        }
    }

    private void OnActivated(string sessionId) => _post(() =>
    {
        if (!_disposed)
        {
            OpenRequested?.Invoke(sessionId);
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var pending in _pending.Values)
        {
            pending.Timer?.Dispose();
        }
        _pending.Clear();
        _notifier.Activated -= OnActivated;
        _notifier.Dispose();
    }

    private sealed class Pending(SystemNotification notification, NotifyWhen when)
    {
        public SystemNotification Notification { get; set; } = notification;
        public NotifyWhen When { get; set; } = when;
        public ITimer? Timer { get; set; }
    }
}
