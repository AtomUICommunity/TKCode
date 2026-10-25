using System.Diagnostics;
using Avalonia.Threading;
using TKCode.Desktop.ViewModels;

namespace TKCode.MemoryHarness;

/// <summary>
/// 打开设置页的卡顿基准：一个后台线程每 2ms 往 UI 线程投一个探测消息，消息排队的时间就是 UI 线程当时被占用的时间。
/// 依次记录：首次打开、逐个分区、关闭后再次打开；每阶段输出最长阻塞、超过 16 / 50 / 100ms 的次数与超出部分的总和。
/// </summary>
internal static class SettingsScenario
{
    public static async Task RunAsync(MainWindowViewModel vm)
    {
        for (var i = 0; i < 100 && vm.Conversation.Picker.Selected is null; i++)
        {
            await Task.Delay(100);
        }
        using var monitor = new StallMonitor();
        monitor.Start();
        await Task.Delay(1000);
        monitor.Take();

        await Phase("open settings (first, General)", monitor, async () => await vm.OpenSettingsCommand.ExecuteAsync(null));
        foreach (var nav in vm.Settings.Nav.ToList())
        {
            await Phase($"section {nav.Section} (first visit)", monitor, () =>
            {
                vm.Settings.NavigateCommand.Execute(nav);
                return Task.CompletedTask;
            });
        }
        for (var round = 1; round <= 3; round++)
        {
            vm.CloseSettingsCommand.Execute(null);
            await Task.Delay(1000);
            monitor.Take();
            await Phase($"reopen #{round} (section {vm.Settings.Section})", monitor, async () => await vm.OpenSettingsCommand.ExecuteAsync(null));
        }
        // 厂商页逐个选择（模型行数不同）
        await Phase("section Providers (revisit)", monitor, () =>
        {
            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav.Single(n => n.Section == SettingsSection.Providers));
            return Task.CompletedTask;
        });
        foreach (var item in vm.Settings.ConfiguredList.ToList())
        {
            await Phase($"select provider {item.Id}", monitor, () =>
            {
                vm.Settings.SelectProviderCommand.Execute(item);
                return Task.CompletedTask;
            });
        }
    }

    private static async Task Phase(string name, StallMonitor monitor, Func<Task> action)
    {
        monitor.Take();
        var wall = Stopwatch.StartNew();
        await action();
        var command = wall.Elapsed;
        await Task.Delay(1500);
        var (max, over16, over50, over100, excess) = monitor.Take();
        Console.WriteLine($"{name,-44} cmd {command.TotalMilliseconds,7:F0} ms | max stall {max,6:F0} ms | >16ms x{over16,-3} >50ms x{over50,-3} >100ms x{over100,-3} | stalled {excess,6:F0} ms");
    }
}

internal sealed class StallMonitor : IDisposable
{
    private readonly List<double> _latencies = [];
    private readonly Lock _gate = new();
    private readonly Thread _thread;
    private volatile bool _stop;

    public StallMonitor() => _thread = new Thread(Loop) { IsBackground = true, Name = "stall-probe" };

    public void Start() => _thread.Start();

    private void Loop()
    {
        while (!_stop)
        {
            var posted = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() =>
            {
                var latency = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                lock (_gate)
                {
                    _latencies.Add(latency);
                }
            }, DispatcherPriority.Input);
            Thread.Sleep(2);
        }
    }

    public (double Max, int Over16, int Over50, int Over100, double Excess) Take()
    {
        double[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _latencies];
            _latencies.Clear();
        }
        // 一次长阻塞会让排队的每条探测消息都带上同样的延迟：只统计「延迟比前一条大」的上升沿，避免重复计数
        double max = 0, excess = 0;
        int over16 = 0, over50 = 0, over100 = 0;
        var previous = 0.0;
        foreach (var latency in snapshot)
        {
            max = Math.Max(max, latency);
            if (latency > 16 && latency > previous)
            {
                over16++;
                excess += latency - Math.Max(previous, 16);
                over50 += latency > 50 ? 1 : 0;
                over100 += latency > 100 ? 1 : 0;
            }
            previous = latency;
        }
        return (max, over16, over50, over100, excess);
    }

    public void Dispose() => _stop = true;
}
