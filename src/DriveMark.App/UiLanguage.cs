using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DriveMark.Core;

namespace DriveMark.App;

/// <summary>
/// 轻量级界面本地化：不依赖 resx，通过"中文原文 → 英文"字典在界面树上就地替换。
/// 语言选择保存在 %LOCALAPPDATA%\DriveMark\ui-language.txt（zh / en）。
/// </summary>
public static class UiLanguage
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveMark");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "ui-language.txt");

    public static bool IsEnglish { get; private set; }

    /// <summary>中文原文 → 英文。</summary>
    private static readonly Dictionary<string, string> ZhToEn = new(StringComparer.Ordinal)
    {
        // 顶栏
        ["数据来源"] = "Data source",
        ["打开日志目录…"] = "Open log folder…",
        ["横轴"] = "X axis",
        ["纵轴"] = "Y axis",
        ["图表"] = "Chart",
        ["绘图方式"] = "Plot mode",
        ["导出图片…"] = "Export image…",
        ["语言"] = "Language",
        ["关于"] = "About",
        // 简单设置
        ["选择测试"] = "Select test",
        ["测试项目"] = "Test profile",
        ["块大小"] = "Block size",
        ["文件大小"] = "File size",
        ["测试时长"] = "Duration",
        ["采样间隔"] = "Sampling interval",
        ["队列深度"] = "Queue depth",
        ["队列数"] = "Jobs",
        ["保存结果到 results 目录"] = "Save results to results folder",
        ["打开结果目录"] = "Open results folder",
        ["选好上面几项，点右下角「开始测试」即可。数值项既可选预设，也可直接输入（如时长 1800 秒、大小 4g、块大小 4k、队列深度 64）。"] =
            "Choose the options above, then click Start test (bottom right). Editable values accept presets or custom input (e.g. duration 1800, size 4g, block size 4k, queue depth 64).",
        // 高级设置
        ["其他设置（高级，一般不用改）"] = "Advanced settings (usually not needed)",
        ["RAW / 未格式化磁盘（默认只读）"] = "RAW / unformatted disk (read-only by default)",
        ["刷新"] = "Refresh",
        ["开始 RAW 测试"] = "Start RAW test",
        ["允许 RAW 写入（覆盖数据）"] = "Allow RAW writes (overwrites data)",
        ["保存 .fio"] = "Save .fio",
        ["复制 job"] = "Copy job",
        ["保存配置"] = "Save config",
        ["加载配置"] = "Load config",
        ["恢复默认"] = "Restore defaults",
        ["横轴自动"] = "Auto X axis",
        ["纵轴自动"] = "Auto Y axis",
        ["最小"] = "Min",
        ["最大"] = "Max",
        ["步长"] = "Step",
        ["job 文件"] = "job file",
        ["校验"] = "Validation",
        ["命令行"] = "Command line",
        ["运行日志"] = "Run log",
        // 高级：实时日志插件
        ["启用实时日志插件（默认开启）"] = "Enable realtime log plugin (on by default)",
        ["插件会读取 fio 运行中写出的 live 采样文件，实时显示全分辨率散点；关闭后测试中退回 fio 进度采样。fio 的普通日志仍会在结束后完整生成。"] =
            "The plugin tails fio's live sample files during the run to show full-resolution scatter points. When disabled, the realtime chart falls back to fio progress sampling. Normal fio logs are still written completely after the run.",
        // 高级：fio 日志平滑开关
        ["启用 fio 日志平滑（最小 100ms 窗口，默认关闭）"] = "Enable fio log smoothing (min 100 ms window, off by default)",
        ["默认关闭：带宽/IOPS 日志按所选采样间隔原样记录（大块 + 高队列时可能出现瞬时假点）。开启：fio 对带宽/IOPS 日志使用最小 100ms 窗口（延迟日志不受影响），可抑制假点。"] =
            "Off by default: bandwidth/IOPS logs follow the selected sampling interval (instantaneous spikes are possible with large blocks and deep queues). On: fio uses a minimum 100 ms window for bandwidth/IOPS logs (latency logs are unaffected) to suppress those spikes.",        // 图表区
        ["跟随最新"] = "Follow latest",
        ["显示范围"] = "X window",
        // 运行控制
        ["测试硬盘："] = "Target disk:",
        ["允许写入测试"] = "Allow write test",
        ["允许系统盘写入"] = "Allow system disk write",
        ["▶ 开始测试"] = "▶ Start test",
        ["■ 停止"] = "■ Stop",
        ["就绪"] = "Ready",
        // 底栏
        ["基于开源 fio（Flexible I/O Tester）开发"] = "Built on the open-source fio (Flexible I/O Tester)",
        // 动态状态（常见）
        ["准备就绪"] = "Ready",
        ["运行失败"] = "Run failed",
        ["完成"] = "Done",
        ["失败"] = "Failed",
        ["已取消"] = "Cancelled",
        ["正在停止…"] = "Stopping…",
        ["准备中（正在创建测试文件）…"] = "Preparing (creating test file)…",
        ["准备中（打开原始设备）…"] = "Preparing (opening raw device)…",
        ["测试已取消，测试数据已清理"] = "Test cancelled; test data cleaned up",
        ["已恢复默认设置"] = "Default settings restored",
        // 简单模式下拉/输入显示
        ["随机读（推荐）"] = "Random read (recommended)",
        ["随机写"] = "Random write",
        ["顺序读"] = "Sequential read",
        ["顺序写"] = "Sequential write",
        ["实时绘图"] = "Realtime chart",
        ["完成后绘图"] = "After test",
        ["折线图"] = "Line",
        ["散点图"] = "Scatter",
        ["自动"] = "Auto",
        // 数据集名称与图表标题
        ["示例数据：SSD 混合读写"] = "Sample: SSD mixed read/write",
        ["示例数据：HDD 顺序写"] = "Sample: HDD sequential write",
        ["示例：SSD 混合读写（4K）"] = "Sample: SSD mixed R/W (4K)",
        ["本次测试结果"] = "Last test result",
        ["合成示例数据（非实测）"] = "Synthetic sample data (not measured)",
        ["曲线点数："] = "Points: ",
        ["曲线点数：0"] = "Points: 0",        // 图表轴/序列（与 Core 生成的一致）
        ["时间 (s)"] = "Time (s)",
        ["累计传输量 (MiB)"] = "Transferred (MiB)",
        ["累计传输量 (GiB)"] = "Transferred (GiB)",
        ["吞吐量 (MiB/s)"] = "Throughput (MiB/s)",
        ["延迟 (µs)"] = "Latency (µs)",
        ["延迟 (ms)"] = "Latency (ms)",
        ["读 (read)"] = "Read",
        ["写 (write)"] = "Write",
        ["读延迟 (read)"] = "Read latency",
        ["写延迟 (write)"] = "Write latency",
        // ToolTips（主要项）
        ["实时绘图：边测边画（不生成图片文件）；完成后绘图：测试结束后用日志出图"] =
            "Realtime: draw while testing (no image files). After test: plot from logs when the run finishes.",
        ["选择测试类型（随机读不写盘）"] = "Choose the test type (random read does not write to disk)",
        ["默认 4 KB。可选预设，也可自己输入：4k、8192、1m"] = "Default 4 KB. Choose a preset or type e.g. 4k / 8192 / 1m",
        ["可选预设，也可自己输入：512m、4g；纯数字按 MB 计算（512 = 512 MB）"] =
            "Choose a preset or type e.g. 512m / 4g; a plain number is MB (512 = 512 MB)",
        ["可选预设，也可自己输入秒数：1800（= 1800 秒）、30m、1h"] = "Choose a preset or type seconds: 1800, 30m, 1h",
        ["默认 10 毫秒（约每秒 100 次）。可输入：10、50ms、1s"] = "Default 10 ms (about 100 samples/s). Accepts 10, 50ms, 1s",
        ["iodepth：同时发给硬盘的并发请求数。随机读/写常用 32，顺序读/写常用 8；也可自己输入 1~1024"] =
            "iodepth: concurrent requests sent to the disk (32 is common for random I/O, 8 for sequential; 1–1024 allowed)",
        ["numjobs：同时运行的 fio 任务（队列）个数。一般用 1；可输入 1~64"] = "numjobs: number of fio jobs (queues); usually 1 (1–64 allowed)",
        ["开启后每次测试的 job 文件 / 结果 JSON / 日志 / 摘要会保存到软件目录下的 results\\ 文件夹（可随时关闭）。"] =
            "Save each run's job file / result JSON / logs / summary under results\\ (can be disabled anytime).",
        ["在资源管理器中打开 results 目录"] = "Open the results folder in Explorer",
        ["fio 直接读写原始设备：不创建测试文件。默认只读；整盘（PhysicalDriveN）需要管理员权限。"] =
            "fio reads/writes the raw device directly (no test file). Read-only by default; whole disks (PhysicalDriveN) require administrator.",
        ["默认只读。勾选后会把当前测试项目（如随机写）真实写入 RAW 设备——直接覆盖设备数据且无法清理，仅对确认可清空的盘使用；开始前仍会二次确认。"] =
            "Read-only by default. When enabled, the current profile (e.g. random write) writes to the raw device — data is overwritten and cannot be recovered; use only on disks that can be wiped. A second confirmation is still shown.",
        ["刷新硬盘列表"] = "Refresh disk list",
        ["写测试会真实写入所选硬盘，测试数据结束后自动删除。"] = "Write tests really write to the selected disk; test data is removed afterwards.",
        ["默认禁止对系统盘写入。勾选后还需二次确认；程序只会在 盘符根\\DriveMark-test 下创建测试文件，绝不格式化或清空磁盘。"] =
            "Writing to the system disk is disabled by default. Enabling requires a second confirmation; files are only created under <drive>:\\DriveMark-test, never formatting or wiping the disk.",
        ["新的数据点到来时自动滚动到最新位置"] = "Automatically scroll to the newest data point",
        ["横轴显示的时间窗口；自动=显示全部数据"] = "Visible X-axis window; Auto shows all data",
        ["左右拖动查看长时间测试的不同区段"] = "Drag to inspect different sections of a long test",
    };

    private static readonly Dictionary<string, string> EnToZh =
        new(StringComparer.Ordinal);

    static UiLanguage()
    {
        foreach (var (zh, en) in ZhToEn)
        {
            if (!EnToZh.ContainsKey(en))
                EnToZh[en] = zh;
        }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var text = File.ReadAllText(SettingsFile).Trim();
                IsEnglish = text.Equals("en", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            IsEnglish = false;
        }

        AppText.Current = IsEnglish ? AppLanguage.English : AppLanguage.Chinese;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsFile, IsEnglish ? "en" : "zh");
        }
        catch
        {
            // 忽略写入失败（只影响记忆语言）。
        }
    }

    public static void SetLanguage(bool english)
    {
        IsEnglish = english;
        AppText.Current = english ? AppLanguage.English : AppLanguage.Chinese;
        Save();
    }

    /// <summary>把当前语言应用到整窗界面（含动态生成的 ComboBox 项与 ToolTip）。</summary>
    public static void ApplyToWindow(Window window)
    {
        if (window is null)
            return;

        window.Title = IsEnglish ? "DriveMark — Disk Benchmark" : "DriveMark — 硬盘测速";
        Walk(window);
    }

    /// <summary>
    /// 不直接改 ComboBox 模板生成的 TextBlock，而是给它设置带翻译转换器的 ItemTemplate。
    /// 这样选中项变化时绑定会重新求值，选中框不会停留在旧文字。
    /// </summary>

    private static void LocalizeComboBox(ComboBox combo)
    {
        // 带 DisplayMemberPath 的对象型下拉（测试项目 / 数据源 / 坐标轴 / 目标盘等）
        // 保持 WPF 默认模板：选中项变化会正常刷新显示。
        // 之前直接改模板里 TextBlock.Text 造成的“选中不变”就是在这里修掉的。
        if (!string.IsNullOrEmpty(combo.DisplayMemberPath))
            return;

        // 字符串型下拉：只替换 ItemTemplate，用翻译转换器显示；
        // 不碰模板元素，选中项变化时绑定会重新求值。
        var binding = new Binding(".") { Converter = TranslatedTextConverter.Instance };
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, binding);
        factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        combo.ItemTemplate = new DataTemplate { VisualTree = factory };

        // 可编辑下拉的显示文字在 TextBox 里，直接翻译不会碰模板元素。
        if (combo.IsEditable && !combo.IsKeyboardFocusWithin && !string.IsNullOrWhiteSpace(combo.Text))
        {
            var translated = Translate(combo.Text);
            if (!string.Equals(translated, combo.Text, StringComparison.Ordinal))
                combo.Text = translated;
        }
    }
    private sealed class TranslatedTextConverter : IValueConverter
    {
        public static readonly TranslatedTextConverter Instance = new();

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => Translate(value?.ToString() ?? string.Empty);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
    /// <summary>把单条中文/英文文案转换为当前语言（用于运行时状态文本）。</summary>
    public static string Translate(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (IsEnglish)
            return TranslateToEnglish(text);
        return TranslateToChinese(text);
    }

    private static void Walk(DependencyObject node)
    {
        // ComboBox 的选中项显示由模板生成的 TextBlock/ContentPresenter 承载。
        // 如果直接改写这些子元素的 Text/Content，会留下本地值，之后切换 SelectedItem
        // 时选中框仍显示旧文本（用户看到的“参数变了但下拉框显示不变”）。
        // 因此 ComboBox 只翻译 ToolTip，不进入其视觉子树。
        if (node is ComboBox comboBox)
        {
            if (comboBox.ToolTip is string comboTooltip)
                comboBox.ToolTip = Translate(comboTooltip);
            LocalizeComboBox(comboBox);
            return;
        }

        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
            count = 0;
        }

        for (var i = 0; i < count; i++)
            Walk(VisualTreeHelper.GetChild(node, i));

        switch (node)
        {
            case TextBlock textBlock:
                textBlock.Text = Translate(textBlock.Text);
                break;
            case ContentControl contentControl when contentControl.Content is string content:
                contentControl.Content = Translate(content);
                break;
            case HeaderedContentControl headered when headered.Header is string header:
                headered.Header = Translate(header);
                break;
            case ComboBox combo when combo.IsEditable:
                combo.Text = Translate(combo.Text);
                break;
        }

        if (node is FrameworkElement element && element.ToolTip is string tooltip)
            element.ToolTip = Translate(tooltip);
    }

    private static string TranslateToEnglish(string text)
    {
        if (ZhToEn.TryGetValue(text, out var exact))
            return exact;

        if (text.StartsWith("本次测试：", StringComparison.Ordinal))
            return "Test: " + TranslateToEnglish(text["本次测试：".Length..]);
        if (text.StartsWith("自定义日志：", StringComparison.Ordinal))
            return "Custom log: " + text["自定义日志：".Length..];
        if (text.StartsWith("示例数据：", StringComparison.Ordinal))
            return "Sample: " + TranslateToEnglish(text["示例数据：".Length..]);
        if (text.StartsWith("示例：", StringComparison.Ordinal))
            return "Sample: " + TranslateToEnglish(text["示例：".Length..]);
        if (text.StartsWith("实时曲线 · ", StringComparison.Ordinal))
            return "Realtime chart · " + TranslateToEnglish(text["实时曲线 · ".Length..]);

        text = text.Replace("可移动磁盘", "Removable")
                   .Replace("固定磁盘", "Fixed")
                   .Replace("可用", "free")
                   .Replace("总计", "total")
                   .Replace("盘「", "\"")
                   .Replace("」", "\"");

        var converted = Regex.Replace(text, @"(?<num>\d+(?:\.\d+)?)\s*毫秒", "${num} ms");
        converted = Regex.Replace(converted, @"(?<num>\d+(?:\.\d+)?)\s*秒", "${num} s");
        return converted;
    }

    private static string TranslateToChinese(string text)
    {
        if (EnToZh.TryGetValue(text, out var exact))
            return exact;

        if (text.StartsWith("Test: ", StringComparison.Ordinal))
            return "本次测试：" + TranslateToChinese(text["Test: ".Length..]);
        if (text.StartsWith("Custom log: ", StringComparison.Ordinal))
            return "自定义日志：" + text["Custom log: ".Length..];
        if (text.StartsWith("Sample: ", StringComparison.Ordinal))
            return "示例数据：" + TranslateToChinese(text["Sample: ".Length..]);
        if (text.StartsWith("Realtime chart · ", StringComparison.Ordinal))
            return "实时曲线 · " + TranslateToChinese(text["Realtime chart · ".Length..]);

        var converted = Regex.Replace(text, @"(?<num>\d+(?:\.\d+)?)\s*ms\b", "${num} 毫秒");
        converted = Regex.Replace(converted, @"(?<num>\d+(?:\.\d+)?)\s*s\b", "${num} 秒");
        return converted;
    }
}
