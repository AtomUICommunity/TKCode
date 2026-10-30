using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace TKCode.Desktop.Tests;

/// <summary>以真实的 TKCode App（主题、调色板、语言目录）在无头平台上启动，Skia 渲染以便截图。</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // 截图测试默认关闭动效（结果确定）；MotionTests 用手动时钟单独打开
        TKCode.Desktop.Motion.MotionSettings.ForceDisabled = true;
        return AppBuilder.Configure<App>()
                  .UseSkia()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                  .UseTKCodeArc();
    }

    /// <summary>在手动动效时钟下运行：打开动效，结束时把残留动画推进完并恢复关闭。</summary>
    public static void WithMotion(Action body)
    {
        TKCode.Desktop.Motion.MotionSettings.ForceDisabled = false;
        TKCode.Desktop.Motion.MotionTicker.IsManual = true;
        try
        {
            body();
        }
        finally
        {
            TKCode.Desktop.Motion.MotionTicker.FinishAll();
            TKCode.Desktop.Motion.MotionTicker.ResetManual();
            TKCode.Desktop.Motion.MotionTicker.IsManual = false;
            TKCode.Desktop.Motion.MotionSettings.ForceDisabled = true;
        }
    }

    /// <summary>推进动效时钟并完成布局与渲染。</summary>
    public static void Step(TopLevel topLevel, double milliseconds)
    {
        TKCode.Desktop.Motion.MotionTicker.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        topLevel.UpdateLayout();
    }

    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    public static HeadlessUnitTestSession Session => Shared.Value;

    /// <summary>截图目录：环境变量 TKCODE_SNAPSHOT_DIR，默认测试输出目录下的 snapshots/。</summary>
    public static string SnapshotPath(string name)
    {
        var dir = Environment.GetEnvironmentVariable("TKCODE_SNAPSHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "snapshots");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".png");
    }

    public static void Save(TopLevel topLevel, string name)
    {
        using var frame = topLevel.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        using var file = File.Create(SnapshotPath(name));
        frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    public static Bitmap Capture(TopLevel topLevel) =>
        topLevel.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
}
