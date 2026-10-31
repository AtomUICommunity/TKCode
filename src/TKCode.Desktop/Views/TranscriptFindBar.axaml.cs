using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StreamMark.Avalonia.Transcript;
using TKCode.Arc;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.Views;

/// <summary>会话内查找条：查找逻辑在 <see cref="TranscriptFinder"/>，这里只管输入、按键与显示。</summary>
public partial class TranscriptFindBar : UserControl
{
    private TranscriptFinder? _finder;
    private Func<bool>? _hasEarlier;
    private Func<Task>? _loadAll;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public TranscriptFindBar()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Run();
        };
        Field.PropertyChanged += (_, e) =>
        {
            if (e.Property == SearchField.TextProperty && IsVisible)
            {
                _debounce.Stop();
                _debounce.Start();
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        PreviousButton.Click += (_, _) => Previous();
        NextButton.Click += (_, _) => Next();
        CloseButton.Click += (_, _) => Close();
        LoadAllButton.Click += async (_, _) => await LoadAllAsync();
    }

    /// <summary>查找条关闭（宿主把焦点还给输入框）。</summary>
    public event EventHandler? Closed;

    public bool IsOpen => IsVisible;

    /// <param name="hasEarlier">是否还有没加载的更早历史。</param>
    /// <param name="loadAll">加载全部更早的历史。</param>
    public void Attach(TranscriptView view, Func<bool> hasEarlier, Func<Task> loadAll)
    {
        _finder = new TranscriptFinder(view);
        _finder.Changed += UpdateCount;
        _hasEarlier = hasEarlier;
        _loadAll = loadAll;
    }

    public void Open()
    {
        IsVisible = true;
        LoadedOnlyRow.IsVisible = _hasEarlier?.Invoke() == true;
        Dispatcher.UIThread.Post(() =>
        {
            Field.Focus();
            if (Field.Text is { Length: > 0 })
            {
                Run();
            }
        }, DispatcherPriority.Input);
    }

    public void Close()
    {
        if (!IsVisible)
        {
            return;
        }
        _debounce.Stop();
        IsVisible = false;
        _finder?.Clear();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Next()
    {
        if (_finder is { Count: > 0 })
        {
            _finder.Next();
        }
    }

    public void Previous()
    {
        if (_finder is { Count: > 0 })
        {
            _finder.Previous();
        }
    }

    private void Run()
    {
        _finder?.Find(Field.Text ?? "");
    }

    private async Task LoadAllAsync()
    {
        if (_loadAll is null)
        {
            return;
        }
        await _loadAll();
        LoadedOnlyRow.IsVisible = _hasEarlier?.Invoke() == true;
        Run();
    }

    private void UpdateCount()
    {
        if (_finder is null || _finder.Query.Length == 0)
        {
            CountText.Text = "";
            return;
        }
        CountText.Text = _finder.Count == 0
            ? Strings.Get(TKCodeLangResourceKind.FindNoResults)
            : $"{_finder.Current + 1}/{_finder.Count}";
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                // 输入后还没到防抖时间就按 ↵：先查找
                if (_debounce.IsEnabled)
                {
                    _debounce.Stop();
                    Run();
                }
                else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    Previous();
                }
                else
                {
                    Next();
                }
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }
}
