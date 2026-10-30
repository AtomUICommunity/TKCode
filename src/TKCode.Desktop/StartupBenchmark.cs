using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;

namespace TKCode.Desktop;

/// <summary>发布产物的首帧基准：保留正常服务初始化，以无头 Skia 渲染主窗口，不等待 Host。</summary>
internal static class StartupBenchmark
{
    public static int Run(string[] args, long started)
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseTKCodeArc()
            .SetupWithoutStarting();

        var vm = App.Services!.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
        try
        {
            window.Show();
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("No startup frame rendered.");
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var welcome = window.FindControl<TextBlock>("WelcomeQuip");
            if (welcome is not { IsEffectivelyVisible: true } || string.IsNullOrWhiteSpace(welcome.Text) ||
                window.FindControl<ComposerView>("EmptyComposer") is not { IsEffectivelyVisible: true })
            {
                throw new InvalidOperationException("Startup frame is missing the welcome text or composer.");
            }
            var output = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?[6..];
            if (output is not null)
            {
                using var file = File.Create(output);
                frame.Save(file, new PngBitmapEncoderOptions());
            }
            Console.WriteLine($"STARTUP_FRAME_MS={elapsed.ToString("F3", CultureInfo.InvariantCulture)}");
            Console.Out.Flush();
            return 0;
        }
        finally
        {
            window.Close();
        }
    }
}
