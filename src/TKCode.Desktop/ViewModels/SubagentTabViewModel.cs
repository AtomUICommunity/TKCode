using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 右侧工作区的子 Agent 标签（docs/design/subagent-view.md §6.3）：用一个只读的 <see cref="SessionViewModel"/> 承载它的转录，
/// 与主会话使用同一套转录模板，只是没有输入框。打开时取历史与进行中的事件，此后接收 subagent/event，关闭时停止转发。
/// </summary>
public sealed partial class SubagentTabViewModel : WorkbenchTabViewModel, IDisposable
{
    private readonly AgentHostConnection _host;
    private readonly ILogger? _logger;
    private List<SubagentEventNotification>? _pending = [];
    private bool _disposed;

    public SubagentTabViewModel(AgentHostConnection host, SessionViewModel parent, ToolCallBlockViewModel call, string? name, string? model,
        Action<string>? openFile = null, ILogger? logger = null) : base(WorkbenchTabKind.Subagent)
    {
        _host = host;
        _logger = logger;
        SessionId = parent.SessionId;
        ToolCallId = call.ToolCallId;
        Name = name ?? Strings.Get(TKCodeLangResourceKind.SubagentDefaultName);
        Model = model;
        Transcript = new SessionViewModel(new SessionDto(call.ToolCallId, parent.Cwd, model ?? "", "", new SessionOptionsDto(), parent.Runtime))
        {
            IsSubagent = true,
        };
        Transcript.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsBusy));
            }
        };
        if (openFile is not null)
        {
            Transcript.OpenRequested += openFile;
        }
        host.SubagentEvent += OnEvent;
        _ = LoadAsync();
    }

    /// <summary>父会话。</summary>
    public string SessionId { get; }

    /// <summary>派生子 Agent 的工具调用。</summary>
    public string ToolCallId { get; }

    public SessionViewModel Transcript { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Name { get; private set; }

    [ObservableProperty]
    public partial string? Model { get; private set; }

    public override string Title => Name;

    public override LucideKind Icon => LucideKind.Bot;

    public bool IsRunning => Transcript.IsRunning;

    public override bool IsBusy => IsRunning;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public bool IsEmpty => !IsLoading && Error is null && Transcript.Items.Count == 0;

    private async Task LoadAsync()
    {
        try
        {
            var server = await _host.GetServerAsync();
            var result = await server.OpenSubagentAsync(SessionId, ToolCallId);
            if (_disposed)
            {
                return;
            }
            Name = result.Name;
            Model = result.Model ?? Model;
            Transcript.LoadHistory(result.History);
            foreach (var e in result.Replay)
            {
                Transcript.Apply(e);
            }
            Transcript.SetRunning(result.IsRunning);
            // 打开请求返回之前到达的事件都在快照之后（Host 在同一把锁内取快照并开始转发）
            foreach (var n in _pending!.OrderBy(n => n.Seq))
            {
                Transcript.Apply(n.Event);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Open subagent {ToolCallId} failed", ToolCallId);
            Error = Strings.Format(TKCodeLangResourceKind.SubagentLoadFailedFormat, ex.Message);
        }
        finally
        {
            _pending = null;
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void OnEvent(SubagentEventNotification n)
    {
        if (n.SessionId != SessionId || n.ToolCallId != ToolCallId)
        {
            return;
        }
        if (_pending is { } pending)
        {
            pending.Add(n);
            return;
        }
        Transcript.Apply(n.Event);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _host.SubagentEvent -= OnEvent;
        _ = CloseAsync();
    }

    private async Task CloseAsync()
    {
        try
        {
            var server = await _host.GetServerAsync();
            await server.CloseSubagentAsync(SessionId, ToolCallId);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Close subagent {ToolCallId} failed", ToolCallId);
        }
    }
}
