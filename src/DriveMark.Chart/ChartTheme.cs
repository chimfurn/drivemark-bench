using System.Windows.Media;

namespace DriveMark.Chart;

/// <summary>图表配色与字体。集中管理，方便换主题。</summary>
public sealed class ChartTheme
{
    public string FontFamily { get; init; } = "Microsoft YaHei";

    public Brush Background { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)));
    public Brush PlotBackground { get; init; } = Freeze(new SolidColorBrush(Colors.White));
    public Brush PlotBorder { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE5, 0xEA)));
    public Brush GridMajor { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xEF)));
    public Brush GridMinor { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)));
    public Brush AxisLine { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));
    public Brush TickLabel { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    public Brush AxisTitle { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)));
    public Brush Title { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)));
    public Brush Subtitle { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    public Brush EmptyText { get; init; } = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));

    public Color ReadColor { get; init; } = Color.FromRgb(0x25, 0x63, 0xEB);   // 蓝
    public Color WriteColor { get; init; } = Color.FromRgb(0xDC, 0x26, 0x26);  // 红
    public Color OtherColor { get; init; } = Color.FromRgb(0x05, 0x96, 0x69);  // 绿

    public double TitleSize { get; init; } = 19;
    public double SubtitleSize { get; init; } = 12;
    public double AxisTitleSize { get; init; } = 12.5;
    public double TickLabelSize { get; init; } = 11;
    public double LegendSize { get; init; } = 12;
    public double LineThickness { get; init; } = 2.2;

    public static ChartTheme Light { get; } = new();

    public Color ColorFor(Core.Charts.SeriesRole role) => role switch
    {
        Core.Charts.SeriesRole.Read => ReadColor,
        Core.Charts.SeriesRole.Write => WriteColor,
        _ => OtherColor,
    };

    public Brush BrushFor(Core.Charts.SeriesRole role) =>
        role switch
        {
            Core.Charts.SeriesRole.Read => ReadBrush,
            Core.Charts.SeriesRole.Write => WriteBrush,
            _ => OtherBrush,
        };

    private Brush? _readBrush;
    private Brush? _writeBrush;
    private Brush? _otherBrush;

    private Brush ReadBrush => _readBrush ??= Freeze(new SolidColorBrush(ReadColor));
    private Brush WriteBrush => _writeBrush ??= Freeze(new SolidColorBrush(WriteColor));
    private Brush OtherBrush => _otherBrush ??= Freeze(new SolidColorBrush(OtherColor));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
