using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Config;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Motion;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>界面缩放（settings.json 的 uiScale）：档位、写回、等比排版，以及主窗口与弹层对它的响应。</summary>
public sealed class UiZoomTests
{
    [Fact]
    public void Step_WalksTheStops_AndStopsAtTheEnds()
    {
        Assert.Equal(1.15, UiScaleLevels.Step(1.0, 1));
        Assert.Equal(0.9, UiScaleLevels.Step(1.0, -1));
        Assert.Equal(1.5, UiScaleLevels.Step(1.5, 1));
        Assert.Equal(0.8, UiScaleLevels.Step(0.8, -1));
        // 手改成不在档位上的值：向两侧取最近的档位
        Assert.Equal(1.3, UiScaleLevels.Step(1.2, 1));
        Assert.Equal(1.15, UiScaleLevels.Step(1.2, -1));
        // 超过最大档位时放大不再变，缩小回到最大档位
        Assert.Equal(1.8, UiScaleLevels.Step(1.8, 1));
        Assert.Equal(1.5, UiScaleLevels.Step(1.8, -1));
        Assert.Equal(UiScaleLevels.Max, UiScaleLevels.Clamp(5));
        Assert.Equal(UiScaleLevels.Min, UiScaleLevels.Clamp(0.1));
        Assert.Equal(UiScaleLevels.Default, UiScaleLevels.Clamp(double.NaN));
    }

    [Fact]
    public void Setting_IsWrittenOnlyWhenNotDefault_AndStepsFollowTheStops()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("tkcode-ui-scale-").FullName, "settings.json");
        using var settings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, path);
        Assert.Equal(1.0, settings.UiScale);

        settings.SetUiScale(1.3);
        Assert.Equal(1.3, settings.UiScale);
        Assert.Equal(1.3, JsonNode.Parse(File.ReadAllText(path))!["uiScale"]!.GetValue<double>());

        settings.StepUiScale(-1);
        Assert.Equal(1.15, settings.UiScale);
        settings.StepUiScale(1);
        settings.StepUiScale(1);
        Assert.Equal(1.5, settings.UiScale);
        // 到头后不再写文件
        var written = File.GetLastWriteTimeUtc(path);
        settings.StepUiScale(1);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));

        // 默认值不落盘，保持 settings.json 干净
        settings.SetUiScale(1.0);
        Assert.Null(settings.Current.UiScale);
        Assert.Null(JsonNode.Parse(File.ReadAllText(path))!["uiScale"]);
    }

    [Fact]
    public void Parse_OutOfRange_WarnsAndTheServiceClamps_NonNumberIsAFileError()
    {
        var tooBig = SettingsFile.Parse("""{ "uiScale": 5 }""");
        Assert.Contains(tooBig.Warnings, w => w.StartsWith("uiScale:", StringComparison.Ordinal));
        Assert.Equal(5, tooBig.Settings!.UiScale);

        Assert.Empty(SettingsFile.Parse("""{ "uiScale": 1.15 }""").Warnings);
        Assert.NotEmpty(SettingsFile.Parse("""{ "uiScale": "big" }""").Errors);

        var path = Path.Combine(Directory.CreateTempSubdirectory("tkcode-ui-scale-").FullName, "settings.json");
        File.WriteAllText(path, """{ "uiScale": 5 }""");
        using var settings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, path);
        Assert.Equal(UiScaleLevels.Max, settings.UiScale);
    }

    [Fact]
    public async Task ZoomHost_LaysOutInTheScaledSpace_ThenEnlargesTheResult()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            try
            {
                var fixedChild = new Border { Width = 100, Height = 40, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var fillChild = new Border { Background = Brushes.Gray };
                var fixedHost = new ZoomHost { Child = fixedChild };
                var fillHost = new ZoomHost { Child = fillChild };
                var window = new Window { Width = 400, Height = 200, Content = new Grid { Children = { fixedHost, fillHost } } };
                window.Show();

                UiZoom.Scale = 1.0;
                window.UpdateLayout();
                Assert.Null(fillHost.LayoutTransform);
                Assert.Equal(new Size(400, 200), fillChild.Bounds.Size);

                UiZoom.Scale = 2.0;
                window.UpdateLayout();
                // 固定尺寸的内容放大到两倍；撑满的内容在“缩小一半的可用空间”里排版，放大后恰好填满窗口
                Assert.Equal(new Size(200, 80), fixedHost.DesiredSize);
                Assert.Equal(new Size(200, 100), fillChild.Bounds.Size);
                Assert.Equal(2.0, ((ScaleTransform)fillHost.LayoutTransform!).ScaleX);

                UiZoom.Scale = 1.0;
                window.UpdateLayout();
                Assert.Null(fillHost.LayoutTransform);
                Assert.Equal(new Size(100, 40), fixedHost.DesiredSize);
                window.Close();
            }
            finally
            {
                UiZoom.Scale = 1.0;
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MainWindow_FollowsTheSetting_ShortcutsAndPopovers()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        var settingsPath = Path.Combine(home, "settings.json");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            try
            {
                var vm = provider.GetRequiredService<MainWindowViewModel>();
                UiZoom.Follow(provider.GetRequiredService<UserSettingsService>());
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
                window.Show();
                await Settle();

                var host = Assert.IsType<ZoomHost>(window.Content);
                var sidebarHeader = window.FindControl<StackPanel>("SidebarHeader")!;
                var insetAt100 = sidebarHeader.Margin.Left;
                Assert.Equal(880, window.MinWidth);
                Assert.Null(host.LayoutTransform);
                HeadlessApp.Save(window, "zoom-100");

                // 每个弹层都要把内容包进 ZoomHost：弹层是独立窗口，窗口根部的缩放覆盖不到
                var popovers = window.GetLogicalDescendants().OfType<Popover>().ToList();
                Assert.True(popovers.Count >= 8, $"found only {popovers.Count} popovers");
                Assert.All(popovers, p => Assert.IsType<ZoomHost>(p.Surface));

                // ⌘+（⇧⌘= 与 ⌘= 都算）放大一档
                window.KeyPress(Key.OemPlus, CommandModifier | RawInputModifiers.Shift, PhysicalKey.Equal, "+");
                await Settle();
                Assert.Equal(1.15, UiZoom.Scale);
                Assert.Equal(1.15, JsonNode.Parse(File.ReadAllText(settingsPath))!["uiScale"]!.GetValue<double>());
                Assert.Equal(1.15, ((ScaleTransform)host.LayoutTransform!).ScaleX);
                Assert.Equal(880 * 1.15, window.MinWidth, 6);
                Assert.Equal(560 * 1.15, window.MinHeight, 6);
                var content = (Control)host.Child!;
                // 内容按缩小后的可用空间排版（布局取整会差不到 1 个单位）
                Assert.InRange(content.Bounds.Width, host.Bounds.Width / 1.15 - 1.5, host.Bounds.Width / 1.15 + 1.5);
                Assert.InRange(content.Bounds.Height, host.Bounds.Height / 1.15 - 1.5, host.Bounds.Height / 1.15 + 1.5);
                if (OperatingSystem.IsMacOS())
                {
                    // 红绿灯位置不随缩放：避让宽度在缩放后的坐标里要缩小，放大回来后仍是同一个物理位置
                    Assert.Equal(insetAt100, sidebarHeader.Margin.Left * 1.15, 6);
                    Assert.Equal(44 * 1.15, window.ExtendClientAreaTitleBarHeightHint, 6);
                }

                window.KeyPress(Key.OemPlus, CommandModifier, PhysicalKey.Equal, "=");
                await Settle();
                Assert.Equal(1.3, UiZoom.Scale);
                HeadlessApp.Save(window, "zoom-130");

                // 设置页的档位：当前档位高亮，点击其它档位写回文件
                await vm.OpenSettingsCommand.ExecuteAsync(null);
                await Settle();
                var choices = vm.Settings.UiScales;
                Assert.Equal(UiScaleLevels.Stops.Count, choices.Count);
                Assert.Equal("130%", Assert.Single(choices, c => c.IsSelected).Label);
                HeadlessApp.Save(window, "zoom-130-settings");
                vm.Settings.ChooseUiScaleCommand.Execute(choices[0]);
                await Settle();
                Assert.Equal(0.8, UiZoom.Scale);
                Assert.Equal("80%", Assert.Single(vm.Settings.UiScales, c => c.IsSelected).Label);
                vm.CloseSettingsCommand.Execute(null);

                // 命令面板：放大 / 缩小 / 重置三个条目，执行后关闭面板
                vm.Palette.Open();
                var zoomIn = Assert.Single(vm.Palette.Items, i => i.Title == Strings.Get(TKCodeLangResourceKind.ZoomIn));
                Assert.Single(vm.Palette.Items, i => i.Title == Strings.Get(TKCodeLangResourceKind.ZoomOut));
                Assert.Single(vm.Palette.Items, i => i.Title == Strings.Get(TKCodeLangResourceKind.ZoomReset));
                await zoomIn.Execute();
                await Settle();
                Assert.Equal(0.9, UiZoom.Scale);
                Assert.False(vm.Palette.IsOpen);
                window.KeyPress(Key.OemMinus, CommandModifier, PhysicalKey.Minus, "-");
                await Settle();
                Assert.Equal(0.8, UiZoom.Scale);

                // 已是最小档位，再缩小不变
                window.KeyPress(Key.OemMinus, CommandModifier, PhysicalKey.Minus, "-");
                await Settle();
                Assert.Equal(0.8, UiZoom.Scale);

                // ⌘0 恢复 100%，字段从 settings.json 删除，窗口外壳回到原值
                window.KeyPress(Key.D0, CommandModifier, PhysicalKey.Digit0, "0");
                await Settle();
                Assert.Equal(1.0, UiZoom.Scale);
                Assert.Null(JsonNode.Parse(File.ReadAllText(settingsPath))!["uiScale"]);
                Assert.Null(host.LayoutTransform);
                Assert.Equal(880, window.MinWidth);
                Assert.Equal(insetAt100, sidebarHeader.Margin.Left, 6);

                // 外部编辑 settings.json 同样生效
                File.WriteAllText(settingsPath, """{ "uiScale": 1.5 }""");
                await WaitUntil(() => UiZoom.Scale == 1.5);
                Assert.Equal(1.5, ((ScaleTransform)host.LayoutTransform!).ScaleX);
                window.Close();
            }
            finally
            {
                UiZoom.Scale = 1.0;
            }
            // 返回值让 Dispatch 选中 Func<Task<T>> 重载并等待完成，否则 async lambda 会被当作 async void
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Popover_OnTheOverlayLayer_OpensScaledAndBelowItsTrigger()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            try
            {
                var trigger = new Button { Content = "全部项目", Margin = new Thickness(40), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var menu = new Border { Classes = { "menu" }, Width = 200, Height = 120 };
                var popover = new Popover
                {
                    PlacementTarget = trigger, Placement = PlacementMode.BottomEdgeAlignedLeft, VerticalOffset = 6,
                    Child = new ZoomHost { Child = menu }, ShouldUseOverlayLayer = true,
                };
                var window = new Avalonia.Controls.Window { Width = 600, Height = 500, Background = Brushes.Black, Content = new Panel { Children = { trigger, popover } } };
                window.Show();

                UiZoom.Scale = 1.5;
                Dispatcher.UIThread.RunJobs();
                popover.IsOpen = true;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                // 菜单仍按 200×120 排版，渲染时放大到 300×180，位置贴着触发按钮下沿
                Assert.Equal(new Size(200, 120), menu.Bounds.Size);
                var size = ((ZoomHost)popover.Surface!).Bounds.Size;
                Assert.Equal(300, size.Width, 1);
                Assert.Equal(180, size.Height, 1);
                var origin = menu.TranslatePoint(default, window)!.Value;
                var triggerBottom = trigger.TranslatePoint(new Point(0, trigger.Bounds.Height), window)!.Value.Y;
                Assert.True(origin.Y >= triggerBottom, $"menu top {origin.Y} should be below the trigger bottom {triggerBottom}");
                popover.IsOpen = false;
                window.Close();
            }
            finally
            {
                UiZoom.Scale = 1.0;
            }
        }, TestContext.Current.CancellationToken);
    }

    private static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        await Settle();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
