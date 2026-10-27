using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DriveMark.Core;
using DriveMark.Core.Charts;

namespace DriveMark.Chart;

/// <summary>
/// 纯绘制型图表渲染器：把 <see cref="ChartModel"/> 画进任意 <see cref="DrawingContext"/>。
/// 因为不依赖控件生命周期，所以既能给界面用（ChartControl），也能离屏导出 PNG 做回归验证。
///
/// 坐标轴全部自绘：刻度由 <see cref="AxisResolver"/> 计算（支持自动/自定义范围与步长），
/// 标签格式由 <see cref="ChartModel.XFormatter"/> / <see cref="ChartModel.YFormatter"/> 决定。
/// </summary>
public static class ChartRenderer
{
    private const double Pad = 20;
    private const double LegendHeight = 24;

    /// <summary>渲染图表。</summary>
    /// <param name="dc">目标绘制上下文。</param>
    /// <param name="size">可用尺寸。</param>
    /// <param name="model">图表模型。</param>
    /// <param name="theme">主题。</param>
    /// <param name="pixelsPerDip">DPI 缩放（离屏导出取 1.0）。</param>
    public static void Render(DrawingContext dc, Size size, ChartModel? model, ChartTheme theme, double pixelsPerDip = 1.0)
    {
        model ??= ChartModel.Empty;
        if (size.Width < 60 || size.Height < 60)
            return;

        dc.DrawRectangle(theme.Background, null, new Rect(size));

        // ---------- 1. 顶部标题区 ----------
        var y = Pad;
        if (!string.IsNullOrWhiteSpace(model.Title))
        {
            var title = Text(model.Title!, theme.TitleSize, theme.Title, theme, pixelsPerDip, FontWeights.SemiBold);
            title.MaxTextWidth = size.Width - Pad * 2;
            title.MaxLineCount = 1;
            title.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(title, new Point(Pad, y));
            y += title.Height + 2;
        }

        if (!string.IsNullOrWhiteSpace(model.Subtitle))
        {
            var subtitle = Text(model.Subtitle!, theme.SubtitleSize, theme.Subtitle, theme, pixelsPerDip);
            subtitle.MaxTextWidth = size.Width - Pad * 2;
            subtitle.MaxLineCount = 1;
            subtitle.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(subtitle, new Point(Pad, y));
            y += subtitle.Height + 2;
        }

        if (y > Pad)
            y += 8;

        // ---------- 2. 图例（右上，若空间够） ----------
        var hasLegend = model.Series.Count > 0;
        var legendTop = y;
        if (hasLegend)
            y += LegendHeight;

        // ---------- 3. 计算绘图区 ----------
        var xRange = model.ResolveX();
        var yRange = model.ResolveY();

        var yLabels = yRange.Ticks
            .Select(t => Text(model.FormatY(t), theme.TickLabelSize, theme.TickLabel, theme, pixelsPerDip))
            .ToList();
        var yLabelWidth = yLabels.Count == 0 ? 24 : yLabels.Max(t => t.Width);

        // 左侧给竖排 Y 轴标题单独留一条通道，避免和刻度数字重叠。
        var yTitleLane = string.IsNullOrWhiteSpace(model.YTitle)
            ? 0
            : Text(model.YTitle!, theme.AxisTitleSize, theme.AxisTitle, theme, pixelsPerDip, FontWeights.SemiBold).Height + 10;

        // 底部按实际字高测量：刻度标签与轴标题各占一行，避免重叠。
        var xTickLabelHeight = Text("0", theme.TickLabelSize, theme.TickLabel, theme, pixelsPerDip).Height;
        var xLabelHeight = xTickLabelHeight + 10;
        var xTitleHeight = string.IsNullOrWhiteSpace(model.XTitle) ? 0 : theme.AxisTitleSize + 10;

        var plotLeft = Pad + yTitleLane + yLabelWidth + 10;
        var plotRight = size.Width - Pad - 18;
        var plotTop = y + 10;
        var plotBottom = size.Height - Pad - xLabelHeight - xTitleHeight;

        var plot = new Rect(plotLeft, plotTop, Math.Max(0, plotRight - plotLeft), Math.Max(0, plotBottom - plotTop));

        if (plot.Width < 30 || plot.Height < 30)
        {
            DrawEmptyMessage(dc, new Rect(Pad, Pad, size.Width - Pad * 2, size.Height - Pad * 2), theme, pixelsPerDip,
                AppText.T("窗口太小，无法绘制图表", "Window too small to draw the chart"));
            return;
        }

        // ---------- 4. 绘图区背景 ----------
        var plotRectGeo = new RectangleGeometry(plot, 4, 4);
        dc.DrawGeometry(theme.PlotBackground, new Pen(theme.PlotBorder, 1), plotRectGeo);

        // ---------- 5. 无数据：只画背景与提示 ----------
        if (!model.HasData)
        {
            DrawEmptyMessage(dc, plot, theme, pixelsPerDip,
                model.EmptyMessage ?? AppText.T("没有可绘制的数据", "No data to plot"));
            DrawLegend(dc, model, theme, size, legendTop, pixelsPerDip);
            return;
        }

        // ---------- 6. 网格线 ----------
        foreach (var tick in yRange.Ticks)
        {
            var yy = MapY(tick, yRange, plot);
            if (yy < plot.Top - 0.5 || yy > plot.Bottom + 0.5)
                continue;
            dc.DrawLine(new Pen(theme.GridMajor, 1), new Point(plot.Left, yy), new Point(plot.Right, yy));
        }

        foreach (var tick in xRange.Ticks)
        {
            var xx = MapX(tick, xRange, plot);
            if (xx < plot.Left - 0.5 || xx > plot.Right + 0.5)
                continue;
            dc.DrawLine(new Pen(theme.GridMajor, 1), new Point(xx, plot.Top), new Point(xx, plot.Bottom));
        }

        // ---------- 7. 曲线（裁剪在绘图区内） ----------
        dc.PushClip(new RectangleGeometry(plot));
        foreach (var series in model.Series)
        {
            if (model.RenderStyle == ChartRenderStyle.Scatter)
                DrawScatterSeries(dc, series, xRange, yRange, plot, theme);
            else
                DrawSeries(dc, series, xRange, yRange, plot, theme);
        }
        dc.Pop();

        // 折线图额外画终点标记（散点图本身就是点，不需要）。
        // 终点标记画在裁剪区之外：这样贴着边界的点也能显示完整圆点，
        // 同时超出轴范围的点直接跳过（绝不"夹到边缘"假装有数据）。
        if (model.RenderStyle == ChartRenderStyle.Line)
        {
            foreach (var series in model.Series)
                DrawEndpointMarker(dc, series, xRange, yRange, plot, theme);
        }

        // ---------- 8. 坐标轴 ----------
        DrawAxes(dc, model, xRange, yRange, plot, theme, size, pad: Pad, xLabelHeight, xTitleHeight, yTitleLane, pixelsPerDip);

        // ---------- 9. 标题/图例置顶 ----------
        DrawLegend(dc, model, theme, size, legendTop, pixelsPerDip);
    }

    private static void DrawAxes(
        DrawingContext dc, ChartModel model, AxisRange xRange, AxisRange yRange, Rect plot,
        ChartTheme theme, Size size, double pad, double xLabelHeight, double xTitleHeight, double yTitleLane,
        double pixelsPerDip)
    {
        var axisPen = new Pen(theme.AxisLine, 1.2);

        // Y 轴与刻度
        dc.DrawLine(axisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        foreach (var tick in yRange.Ticks)
        {
            var yy = MapY(tick, yRange, plot);
            if (yy < plot.Top - 0.5 || yy > plot.Bottom + 0.5)
                continue;
            dc.DrawLine(axisPen, new Point(plot.Left - 4, yy), new Point(plot.Left, yy));
            var label = Text(model.FormatY(tick), theme.TickLabelSize, theme.TickLabel, theme, pixelsPerDip);
            dc.DrawText(label, new Point(plot.Left - 8 - label.Width, yy - label.Height / 2));
        }

        // X 轴与刻度
        dc.DrawLine(axisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        foreach (var tick in xRange.Ticks)
        {
            var xx = MapX(tick, xRange, plot);
            if (xx < plot.Left - 0.5 || xx > plot.Right + 0.5)
                continue;
            dc.DrawLine(axisPen, new Point(xx, plot.Bottom), new Point(xx, plot.Bottom + 4));
            var label = Text(model.FormatX(tick), theme.TickLabelSize, theme.TickLabel, theme, pixelsPerDip);
            // 贴边时把标签往里收，避免超出画布。
            var lx = Math.Clamp(xx - label.Width / 2, 2, size.Width - label.Width - 2);
            dc.DrawText(label, new Point(lx, plot.Bottom + 7));
        }

        // X 轴标题（底部居中）
        if (!string.IsNullOrWhiteSpace(model.XTitle))
        {
            var ft = Text(model.XTitle, theme.AxisTitleSize, theme.AxisTitle, theme, pixelsPerDip, FontWeights.SemiBold);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, plot.Bottom + xLabelHeight + 2));
        }

        // Y 轴标题（左侧竖排，占独立通道，不压刻度数字）
        if (!string.IsNullOrWhiteSpace(model.YTitle))
        {
            var ft = Text(model.YTitle, theme.AxisTitleSize, theme.AxisTitle, theme, pixelsPerDip, FontWeights.SemiBold);
            var cx = pad + yTitleLane / 2;
            var cy = plot.Top + plot.Height / 2;
            dc.PushTransform(new RotateTransform(-90, cx, cy));
            dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
            dc.Pop();
        }
    }

    private static void DrawSeries(DrawingContext dc, ChartSeries series, AxisRange xRange, AxisRange yRange, Rect plot, ChartTheme theme)
    {
        if (series.Points.Count == 0)
            return;

        var color = theme.ColorFor(series.Role);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, theme.LineThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();

        // 面积填充（渐变到透明），让曲线更易读。
        if (series.Points.Count > 1)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                var first = series.Points[0];
                ctx.BeginFigure(new Point(MapX(first.X, xRange, plot), plot.Bottom), true, true);
                foreach (var p in series.Points)
                    ctx.LineTo(new Point(MapX(p.X, xRange, plot), MapY(p.Y, yRange, plot)), true, false);
                var last = series.Points[^1];
                ctx.LineTo(new Point(MapX(last.X, xRange, plot), plot.Bottom), true, false);
            }
            area.Freeze();

            var fill = new LinearGradientBrush(
                Color.FromArgb(46, color.R, color.G, color.B),
                Color.FromArgb(2, color.R, color.G, color.B),
                new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        // 折线
        if (series.Points.Count == 1)
        {
            var only = series.Points[0];
            var px = MapX(only.X, xRange, plot);
            var py = MapY(only.Y, yRange, plot);
            if (plot.Contains(px, py))
                dc.DrawEllipse(brush, null, new Point(px, py), 4, 4);
            return;
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(MapX(series.Points[0].X, xRange, plot), MapY(series.Points[0].Y, yRange, plot)), false, false);
            for (var i = 1; i < series.Points.Count; i++)
            {
                var p = series.Points[i];
                ctx.LineTo(new Point(MapX(p.X, xRange, plot), MapY(p.Y, yRange, plot)), true, false);
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, pen, line);

        // 数据点稀疏时画点，避免AppText.T("只有一根线", "Only one line")看不出采样密度。
        if (series.Points.Count <= 40)
        {
            foreach (var p in series.Points)
            {
                var px = MapX(p.X, xRange, plot);
                var py = MapY(p.Y, yRange, plot);
                if (plot.Contains(px, py))
                    dc.DrawEllipse(brush, null, new Point(px, py), 2.2, 2.2);
            }
        }
    }

    /// <summary>散点图：只画数据点，不画连线与面积，适合观察离散程度/抖动。</summary>
    private static void DrawScatterSeries(
        DrawingContext dc, ChartSeries series, AxisRange xRange, AxisRange yRange, Rect plot, ChartTheme theme)
    {
        if (series.Points.Count == 0)
            return;

        var color = theme.ColorFor(series.Role);
        var brush = new SolidColorBrush(Color.FromArgb(215, color.R, color.G, color.B));
        brush.Freeze();

        // 点越密半径越小，避免糊成一片。
        var radius = series.Points.Count > 2000 ? 1.7 : series.Points.Count > 500 ? 2.2 : 2.8;

        foreach (var point in series.Points)
        {
            var x = MapX(point.X, xRange, plot);
            var y = MapY(point.Y, yRange, plot);
            if (x < plot.Left - 2 || x > plot.Right + 2 || y < plot.Top - 2 || y > plot.Bottom + 2)
                continue;
            dc.DrawEllipse(brush, null, new Point(x, y), radius, radius);
        }
    }

    /// <summary>画曲线终点标记；超出当前轴范围时不画（避免误导）。</summary>
    private static void DrawEndpointMarker(        DrawingContext dc, ChartSeries series, AxisRange xRange, AxisRange yRange, Rect plot, ChartTheme theme)
    {
        if (series.Points.Count == 0)
            return;

        var end = series.Points[^1];
        var px = MapX(end.X, xRange, plot);
        var py = MapY(end.Y, yRange, plot);
        if (!plot.Contains(px, py))
            return;

        var color = theme.ColorFor(series.Role);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var whitePen = new Pen(Brushes.White, 1.5);
        whitePen.Freeze();
        dc.DrawEllipse(brush, whitePen, new Point(px, py), 4, 4);
    }

    private static void DrawLegend(DrawingContext dc, ChartModel model, ChartTheme theme, Size size, double top, double pixelsPerDip)
    {
        if (model.Series.Count == 0)
            return;

        double x = Pad;
        const double rowHeight = 20;

        foreach (var series in model.Series)
        {
            var color = theme.ColorFor(series.Role);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var pen = new Pen(brush, theme.LineThickness);
            pen.Freeze();

            var labelText = series.Points.Count > 0
                ? $"{series.Name} · {AppText.T("平均", "avg")} {model.FormatY(series.MeanY)}{(string.IsNullOrEmpty(model.YUnit) ? "" : " " + model.YUnit)}"
                : series.Name;
            var ft = Text(labelText, theme.LegendSize, theme.TickLabel, theme, pixelsPerDip);

            if (x + 30 + ft.Width > size.Width - Pad)
                break; // 放不下就不画，避免溢出

            var cy = top + rowHeight / 2;
            dc.DrawLine(pen, new Point(x, cy), new Point(x + 18, cy));
            dc.DrawEllipse(brush, null, new Point(x + 9, cy), 2.6, 2.6);
            dc.DrawText(ft, new Point(x + 24, cy - ft.Height / 2));
            x += 24 + ft.Width + 18;
        }
    }

    private static void DrawEmptyMessage(DrawingContext dc, Rect area, ChartTheme theme, double pixelsPerDip, string message)
    {
        var ft = Text(message, 14, theme.EmptyText, theme, pixelsPerDip);
        ft.TextAlignment = TextAlignment.Center;
        ft.MaxTextWidth = Math.Max(60, area.Width - 20);
        var cy = area.Top + area.Height / 2;
        // TextAlignment=Center 已经会在 MaxTextWidth 范围内居中，这里只需给出左边界，
        // 不能再按 ft.Width 二次居中（否则文字会整体偏右）。
        dc.DrawText(ft, new Point(area.Left + 10, cy - ft.Height / 2));
    }

    // ---------------------------------------------------------------- 坐标映射

    private static double MapX(double value, AxisRange range, Rect plot) =>
        plot.Left + range.Normalize(value) * plot.Width;

    private static double MapY(double value, AxisRange range, Rect plot) =>
        plot.Bottom - range.Normalize(value) * plot.Height;

    // ---------------------------------------------------------------- 文本

    private static FormattedText Text(
        string content, double size, Brush brush, ChartTheme theme, double pixelsPerDip,
        FontWeight? weight = null)
    {
        var typeface = new Typeface(
            new FontFamily(theme.FontFamily),
            FontStyles.Normal,
            weight ?? FontWeights.Normal,
            FontStretches.Normal);

        return new FormattedText(
            content,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            pixelsPerDip);
    }
}
