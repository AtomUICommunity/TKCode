using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TKCode.Desktop.Services.Notifications;

/// <summary>
/// 未读角标的图片（docs/design/notifications.md §4.5）：Windows 任务栏的覆盖图标（红底白字的数字），
/// 以及托盘图标右上角的红点。用 Avalonia 绘制，三个平台一致。
/// </summary>
internal static class BadgeRenderer
{
    private static readonly Color Red = Color.FromRgb(0xE5, 0x48, 0x4D);

    /// <summary>角标上的文字：超过 9 显示 9+（覆盖图标只有 16 像素，三位数看不清）。</summary>
    public static string CountText(int count) => count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>任务栏覆盖图标：<paramref name="size"/> 像素见方的红色圆形 + 白色数字，PNG 编码。</summary>
    public static byte[] OverlayPng(int count, int size = 32)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var center = new Point(size / 2.0, size / 2.0);
            context.DrawEllipse(new SolidColorBrush(Red), new Pen(Brushes.White, size / 16.0), center, size / 2.0 - size / 32.0, size / 2.0 - size / 32.0);
            var text = new FormattedText(CountText(count), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), size * (count > 9 ? 0.5 : 0.62), Brushes.White);
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>托盘图标：应用图标，有未读时右上角加红点。</summary>
    public static Bitmap TrayIcon(bool dot, int size = 64)
    {
        using var logo = new Bitmap(AssetLoader.Open(new Uri("avares://TKCode/Assets/app-logo.png")));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawImage(logo, new Rect(0, 0, size, size));
            if (dot)
            {
                var radius = size * 0.2;
                var center = new Point(size - radius - size * 0.02, radius + size * 0.02);
                context.DrawEllipse(new SolidColorBrush(Red), new Pen(Brushes.White, size / 24.0), center, radius, radius);
            }
        }
        return bitmap;
    }
}
