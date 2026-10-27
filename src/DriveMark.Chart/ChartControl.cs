using System.Windows;
using System.Windows.Media;
using DriveMark.Core.Charts;

namespace DriveMark.Chart;

/// <summary>
/// 自绘图表控件。所有坐标轴、网格、曲线都由 <see cref="ChartRenderer"/> 绘制，
/// 不依赖任何第三方图表库（本机也无法联网装包）。
/// </summary>
public sealed class ChartControl : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model),
        typeof(ChartModel),
        typeof(ChartControl),
        new FrameworkPropertyMetadata(ChartModel.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme),
        typeof(ChartTheme),
        typeof(ChartControl),
        new FrameworkPropertyMetadata(ChartTheme.Light, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>要显示的图表模型。</summary>
    public ChartModel Model
    {
        get => (ChartModel)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>配色主题。</summary>
    public ChartTheme Theme
    {
        get => (ChartTheme)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var dpi = 1.0;
        try
        {
            dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch
        {
            // 离屏/无窗口环境下取默认值即可。
        }

        ChartRenderer.Render(dc, RenderSize, Model, Theme ?? ChartTheme.Light, dpi);
    }
}
