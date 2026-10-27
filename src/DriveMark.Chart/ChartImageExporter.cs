using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DriveMark.Core.Charts;

namespace DriveMark.Chart;

/// <summary>
/// 把图表离屏渲染成 PNG。用于：
/// 1) 无界面环境下的自动化视觉回归（CI / agent 自检）；
/// 2) 导出图表图片到报告。
/// 必须在 STA 线程调用。
/// </summary>
public static class ChartImageExporter
{
    /// <summary>渲染成位图。</summary>
    public static BitmapSource Render(ChartModel model, int width, int height, ChartTheme? theme = null, double dpi = 96)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "宽高必须为正数");

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            ChartRenderer.Render(dc, new Size(width, height), model, theme ?? ChartTheme.Light, dpi / 96d);

        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>渲染并保存为 PNG 文件。</summary>
    public static void SavePng(ChartModel model, string path, int width = 1200, int height = 700, ChartTheme? theme = null)
    {
        var bitmap = Render(model, width, height, theme);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
