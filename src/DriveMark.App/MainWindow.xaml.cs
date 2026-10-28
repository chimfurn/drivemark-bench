using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DriveMark.Chart;
using DriveMark.Core;
using DriveMark.Core.Charts;
using DriveMark.Core.Execution;
using DriveMark.Core.Options;
using DriveMark.Core.Parsing;
using DriveMark.Plugins.Realtime;
using Microsoft.Win32;

namespace DriveMark.App;

/// <summary>
/// 主窗口（面向普通用户的精简界面）：
/// 左侧只保留"测试项目 / 文件大小 / 测试时长 / 采样间隔"四项，其余 fio 参数收进「其他设置」折叠区；
/// 右侧为散点图（横轴/纵轴）；底部为自由选盘 + 开始/停止 + 实时进度。
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, ChartDataSet> _datasets = new(StringComparer.Ordinal);
    private readonly List<DatasetChoice> _datasetChoices = [];
    private readonly Dictionary<ComboBox, (Func<string, (string Value, string Display)?> Parse, Action<string> Apply)> _simpleCombos = [];
    private readonly FioJobConfig _config;
    private readonly RealtimeLogPlugin _realtimeLogPlugin = new();
    private readonly FioRunner _runner = new();
    private readonly LiveChartBuffer _liveBuffer = new();
    private FioOptionEditor _editor = null!;
    private CancellationTokenSource? _runCancellation;
    private bool _isRunning;
    private bool _automationMode;
    private bool _suppressSimple;

    /// <summary>绘图方式：实时绘图（边测边画，不生成图片）或完成后绘图（读日志出图）。</summary>
    private enum PlotMode
    {
        Realtime,
        AfterRun,
    }

    private PlotMode _plotMode = PlotMode.Realtime;
    private XAxisDimension _currentXDimension = XAxisDimension.ElapsedSeconds;
    private YAxisMetric _currentYMetric = YAxisMetric.ThroughputMebibytesPerSecond;
    private bool _hasLiveData;

    // 实时绘图的"全分辨率"数据：直接来自 fio 正在写的日志（bw/iops/lat），
    // 与完成后的日志一一对应，不再受进度行 250ms 限制。
    private readonly List<FioLogSample> _liveBandwidthSamples = [];
    private readonly List<FioLogSample> _liveIopsSamples = [];
    private readonly List<FioLogSample> _liveLatencySamples = [];
    private readonly HashSet<(FioLogKind Kind, long TimeMs, double Value, int Direction, long BlockSize, long Priority)> _liveSampleKeys = [];
    private bool _hasLiveLogData;

    private double? _viewportWindowSeconds;
    private bool _suppressViewport;
    private int _progressEventCount;
    private string? _lastProgressLine;

    private sealed record DatasetChoice(string Key, string Label);
    private sealed record AxisChoice(string Label, object Value);
    private sealed record RawChoice(string Label, RawDeviceInfo Device);

    public MainWindow()
    {
        _config = SimpleTestProfiles.CreateDefaultConfig();
        InitializeComponent();
        UiLanguage.Load();
        ApplyAdaptiveWindowSize();

        LoadBuiltInDatasets();
        SetupAxisCombos();
        SetupChartControls();
        SetupSimpleSettings();
        SetupTargets();

        _editor = new FioOptionEditor(_config);
        _editor.Changed += (_, _) => UpdateValidationAndPreview();
        _editor.BuildInto(ParamsHost);

        WireEvents();

        // 界面语言：中/英两个固定按钮 + 自动翻译钩子。
        LanguageZhButton.Click += (_, _) => SetUiLanguage(false);
        LanguageEnButton.Click += (_, _) => SetUiLanguage(true);
        AboutButton.Click += (_, _) => ShowAbout();
        FioNoticeText.MouseLeftButtonUp += (_, _) => ShowAbout();
        FioNoticeText.Cursor = Cursors.Hand;
        AdvancedExpander.Expanded += (_, _) =>
        {
            if (UiLanguage.IsEnglish)
            {
                UiLanguage.ApplyToWindow(this);
                ApplyNamedControlLanguage();
            }
        };
        HookAutoTranslate(StatusText);
        HookAutoTranslate(LiveRunText);
        Loaded += (_, _) => ApplyLanguageCore();

        InitializeRunControls();
        RefreshRawDevices();
        UpdateAxisBoxState();
        UpdateValidationAndPreview();
        UpdateChart();
        StatusText.Text = "准备就绪";
        UiLanguage.ApplyToWindow(this);
    }


    private void SetUiLanguage(bool english)
    {
        UiLanguage.SetLanguage(english);
        ApplyLanguageCore();
    }

    /// <summary>应用当前语言，并刷新依赖语言的文字（状态提示、图表等）。</summary>
    private void ApplyLanguageCore()
    {
        AppText.Current = UiLanguage.IsEnglish ? AppLanguage.English : AppLanguage.Chinese;
        UiLanguage.ApplyToWindow(this);
        ApplyNamedControlLanguage();
        ApplySimpleNumericSettings();
        UpdateValidationAndPreview();
        UpdateChart();

        // 高级区/动态生成的内容可能在该次布局后才实例化，布局完成后再应用一次。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UiLanguage.ApplyToWindow(this);
            ApplyNamedControlLanguage();
            UpdateLayout();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    /// <summary>供截图/自动化使用：--lang en / --lang zh。</summary>
    public void SetUiLanguageForAutomation(string language)
    {
        SetUiLanguage(language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>显式本地化高级区里动态实例化的控件（树遍历可能错过未模板化的元素）。</summary>
    private void ApplyNamedControlLanguage()
    {
        SmoothFioLogCheck.Content = UiLanguage.Translate("启用 fio 日志平滑（最小 100ms 窗口，默认关闭）");
        SmoothFioLogCheck.ToolTip = UiLanguage.Translate(
            "默认关闭：带宽/IOPS 日志按所选采样间隔原样记录（大块 + 高队列时可能出现瞬时假点）。开启：fio 对带宽/IOPS 日志使用最小 100ms 窗口（延迟日志不受影响），可抑制假点。");
        RealtimePluginCheck.Content = UiLanguage.Translate("启用实时日志插件（默认开启）");
        RealtimePluginCheck.ToolTip = UiLanguage.Translate(
            "插件会读取 fio 运行中写出的 live 采样文件，实时显示全分辨率散点；关闭后测试中退回 fio 进度采样。fio 的普通日志仍会在结束后完整生成。");
        UpdateLayout();
    }
    private static void HookAutoTranslate(TextBlock block)
    {
        try
        {
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .AddValueChanged(block, (_, _) =>
                {
                    if (!UiLanguage.IsEnglish)
                        return;
                    var translated = UiLanguage.Translate(block.Text);
                    if (!string.Equals(translated, block.Text, StringComparison.Ordinal))
                        block.Text = translated;
                });
        }
        catch
        {
            // 自动翻译失败不影响主流程。
        }
    }

    private void ShowAbout()
    {
        var english = UiLanguage.IsEnglish;
        var text = english
            ? "DriveMark 1.2.2 — disk benchmark GUI\n\n" +
              "DriveMark is a graphical front-end for the open-source fio (Flexible I/O Tester).\n\n" +
              "• fio version: fio-3.43 (bundled, with optional DriveMark patches)\n" +
              "• fio author: Jens Axboe and contributors\n" +
              "• fio license: GPL-2.0 (full source and license in the FIO folder)\n" +
              "• fio project: https://github.com/axboe/fio\n\n" +
              "• optional realtime log plugin: enabled by default in Advanced settings\n" +
              "• optional 100 ms log-smoothing patch: disabled by default\n\n" +
              "DriveMark only provides the UI, parameter validation, result plotting and automation; " +
              "the actual I/O tests are performed by fio."
            : "DriveMark 1.2.2 — 硬盘测速 GUI\n\n" +
              "本软件是基于开源 fio（Flexible I/O Tester）开发的图形界面。\n\n" +
              "• fio 版本：fio-3.43（内置，含可选 DriveMark 补丁）\n" +
              "• fio 作者：Jens Axboe 及贡献者\n" +
              "• fio 许可证：GPL-2.0（完整源码与许可证见软件目录 FIO\\）\n" +
              "• fio 项目：https://github.com/axboe/fio\n\n" +
              "• 可选实时日志插件：高级设置中默认开启\n" +
              "• 可选 100ms 日志平滑补丁：默认关闭\n\n" +
              "DriveMark 本身只负责界面、参数校验、结果绘图与自动化调用；实际 I/O 测试由 fio 完成。";

        MessageBox.Show(this, text, english ? "About DriveMark" : "关于 DriveMark",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- 初始化

    private void LoadBuiltInDatasets()
    {
        var demoDir = WorkspaceLocator.DemoLogsDirectory();

        AddDataset("ssd-mixed", "示例数据：SSD 混合读写", FioLogDataSetLoader.FromFiles(
            "示例：SSD 混合读写（4K）",
            "合成示例数据（非实测）",
            Path.Combine(demoDir, "ssd-mixed_bw.1.log"),
            Path.Combine(demoDir, "ssd-mixed_iops.1.log"),
            isSynthetic: true));

        AddDataset("hdd-write", "示例数据：HDD 顺序写", FioLogDataSetLoader.FromFiles(
            "示例：HDD 顺序写（1M）",
            "合成示例数据（非实测）",
            Path.Combine(demoDir, "hdd-write_bw.1.log"),
            Path.Combine(demoDir, "hdd-write_iops.1.log"),
            isSynthetic: true));

        DatasetCombo.ItemsSource = null;
        DatasetCombo.ItemsSource = _datasetChoices;
        DatasetCombo.DisplayMemberPath = nameof(DatasetChoice.Label);
        if (_datasetChoices.Count > 0)
            DatasetCombo.SelectedIndex = 0;
    }

    private void AddDataset(string key, string label, ChartDataSet data)
    {
        // 记住当前选中项，重建列表后恢复，避免"数据来源"下拉框变空白。
        var previousKey = (DatasetCombo.SelectedItem as DatasetChoice)?.Key;

        _datasets[key] = data;
        _datasetChoices.RemoveAll(choice => choice.Key == key);
        _datasetChoices.Add(new DatasetChoice(key, label));
        DatasetCombo.ItemsSource = null;
        DatasetCombo.ItemsSource = _datasetChoices;
        DatasetCombo.DisplayMemberPath = nameof(DatasetChoice.Label);

        if (previousKey is not null)
        {
            var restore = _datasetChoices.FirstOrDefault(choice => choice.Key == previousKey);
            if (restore is not null)
                DatasetCombo.SelectedItem = restore;
        }
    }

    private void SetupAxisCombos()
    {
        XAxisCombo.ItemsSource = new List<AxisChoice>
        {
            new("时间 (s)", XAxisDimension.ElapsedSeconds),
            new("累计传输量", XAxisDimension.TransferredMebibytes),
        };
        XAxisCombo.DisplayMemberPath = nameof(AxisChoice.Label);
        XAxisCombo.SelectedIndex = 0;

        YAxisCombo.ItemsSource = new List<AxisChoice>
        {
            new("IOPS", YAxisMetric.Iops),
            new("吞吐量 (MiB/s)", YAxisMetric.ThroughputMebibytesPerSecond),
            new("延迟 (µs/ms)", YAxisMetric.Latency),
        };
        YAxisCombo.DisplayMemberPath = nameof(AxisChoice.Label);
        YAxisCombo.SelectedIndex = 1;
    }

    private void SetupChartControls()
    {
        PlotModeCombo.ItemsSource = new List<string>
        {
            "实时绘图",
            "完成后绘图",
        };
        PlotModeCombo.SelectedIndex = 0;
        _plotMode = PlotMode.Realtime;

        ViewportRangeCombo.ItemsSource = new List<string>
        {
            "自动",
            "10 秒",
            "30 秒",
            "60 秒",
            "120 秒",
            "300 秒",
        };
        ViewportRangeCombo.SelectedIndex = 0;
        _viewportWindowSeconds = null;
    }


    /// <summary>按屏幕工作区自动缩小窗口，避免高 DPI/小屏下窗口超出屏幕导致无法拖动缩放。</summary>
    private void ApplyAdaptiveWindowSize()
    {
        var workArea = SystemParameters.WorkArea;
        const double designWidth = 1400;
        const double designHeight = 880;

        var width = Math.Min(designWidth, workArea.Width * 0.94);
        var height = Math.Min(designHeight, workArea.Height * 0.94);

        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    private void SetupSimpleSettings()
    {
        _suppressSimple = true;

        SimpleProfileCombo.ItemsSource = SimpleTestProfiles.All;
        SimpleProfileCombo.DisplayMemberPath = nameof(SimpleTestProfile.Name);
        SimpleProfileCombo.SelectedIndex = 0;

        SimpleBlockSizeCombo.ItemsSource = SimpleTestProfiles.BlockSizePresets.Select(preset => preset.Label).ToList();
        SimpleSizeCombo.ItemsSource = SimpleTestProfiles.SizePresets.Select(preset => preset.Label).ToList();
        SimpleDurationCombo.ItemsSource = SimpleTestProfiles.DurationPresets.Select(preset => preset.Label).ToList();
        SimpleSamplingCombo.ItemsSource = SimpleTestProfiles.SamplingPresets.Select(preset => preset.Label).ToList();
        SimpleQueueDepthCombo.ItemsSource = SimpleTestProfiles.QueueDepthPresets.Select(preset => preset.Label).ToList();
        SimpleJobCountCombo.ItemsSource = SimpleTestProfiles.JobCountPresets.Select(preset => preset.Label).ToList();

        SetComboText(SimpleBlockSizeCombo,
            SimpleInputParser.FormatBlockSize(_config.Get("bs") ?? SimpleTestProfiles.DefaultBlockSize));
        SetComboText(SimpleSizeCombo,
            SimpleInputParser.FormatSize(_config.Get("size") ?? SimpleTestProfiles.DefaultSize));
        SetComboText(SimpleDurationCombo,
            SimpleInputParser.FormatDuration(_config.Get("runtime") ?? SimpleTestProfiles.DefaultRuntime));
        var sampling = int.TryParse(_config.Get("log_avg_msec"), out var samplingMs)
            ? samplingMs
            : SimpleTestProfiles.DefaultSamplingMilliseconds;
        SetComboText(SimpleSamplingCombo, SimpleInputParser.FormatSampling(sampling));
        SetComboText(SimpleQueueDepthCombo,
            _config.Get("iodepth") ?? SimpleTestProfiles.DefaultQueueDepth);
        SetComboText(SimpleJobCountCombo,
            _config.Get("numjobs") ?? SimpleTestProfiles.DefaultJobCount);

        _suppressSimple = false;
    }

    private void SetupTargets() => RefreshTargets();

    private void RefreshTargets()
    {
        var previous = SelectedTarget?.RootPath;
        var candidates = TestTargetGuard.EnumerateCandidates();

        TargetCombo.ItemsSource = null;
        TargetCombo.ItemsSource = candidates;
        TargetCombo.DisplayMemberPath = nameof(TestTargetInfo.Display);

        if (candidates.Count == 0)
        {
            StartTestButton.IsEnabled = false;
            TargetNote.Text = "没有可用硬盘";
            return;
        }

        StartTestButton.IsEnabled = !_isRunning;

        // 默认目标：优先 E 盘（本机测试习惯），否则取默认候选盘（非系统盘、空间最大）。
        var preferred = (previous is not null
                            ? candidates.FirstOrDefault(candidate =>
                                string.Equals(candidate.RootPath, previous, StringComparison.OrdinalIgnoreCase))
                            : null)
                        ?? candidates.FirstOrDefault(candidate =>
                            string.Equals(candidate.DriveLetter, "E", StringComparison.OrdinalIgnoreCase))
                        ?? candidates[0];

        TargetCombo.SelectedItem = preferred;
        UpdateTargetNote();
    }

    private TestTargetInfo? SelectedTarget => TargetCombo.SelectedItem as TestTargetInfo;

    /// <summary>结果保存目录：软件目录下的 results\（可选项，见「保存结果到 results 目录」）。</summary>
    private static string ResultsDirectory =>
        Path.Combine(WorkspaceLocator.FindRoot() ?? AppContext.BaseDirectory, "results");

    private void UpdateTargetNote()
    {
        if (SelectedTarget is not { } target)
        {
            TargetNote.Text = string.Empty;
            SystemWriteCheck.IsEnabled = false;
            SystemWriteCheck.IsChecked = false;
            return;
        }

        TargetNote.Text = !target.IsSystemDrive
            ? string.Empty
            : SystemWriteCheck.IsChecked == true
                ? "系统盘：写入需二次确认（仅写 DriveMark-test）"
                : "系统盘：默认只读";

        // 只有选中系统盘时才允许勾选"允许系统盘写入"。
        SystemWriteCheck.IsEnabled = target.IsSystemDrive;
        if (!target.IsSystemDrive)
            SystemWriteCheck.IsChecked = false;
    }

    // ---------------------------------------------------------------- 事件

    private void WireEvents()
    {
        DatasetCombo.SelectionChanged += (_, _) => UpdateChart();
        XAxisCombo.SelectionChanged += (_, _) => UpdateChart();
        YAxisCombo.SelectionChanged += (_, _) => UpdateChart();

        PlotModeCombo.SelectionChanged += (_, _) => OnPlotModeChanged();
        ViewportRangeCombo.SelectionChanged += (_, _) => OnViewportRangeChanged();
        ViewportSlider.ValueChanged += (_, _) => OnViewportSliderChanged();
        FollowLatestCheck.Checked += (_, _) => { if (!_suppressViewport) UpdateChart(); };
        FollowLatestCheck.Unchecked += (_, _) => { if (!_suppressViewport) UpdateChart(); };

        AutoXCheck.Checked += (_, _) => { UpdateAxisBoxState(); UpdateChart(); };
        AutoXCheck.Unchecked += (_, _) => { UpdateAxisBoxState(); UpdateChart(); };
        AutoYCheck.Checked += (_, _) => { UpdateAxisBoxState(); UpdateChart(); };
        AutoYCheck.Unchecked += (_, _) => { UpdateAxisBoxState(); UpdateChart(); };

        foreach (var box in new[] { XMinBox, XMaxBox, XStepBox, YMinBox, YMaxBox, YStepBox })
            box.TextChanged += (_, _) => UpdateChart();

        ExportPngButton.Click += (_, _) => ExportChartPng();
        OpenResultsButton.Click += (_, _) => OpenResultsDirectory();
        RefreshRawDevicesButton.Click += (_, _) => RefreshRawDevices();
        RawRunButton.Click += async (_, _) => await ExecuteRawRunAsync();
        AdvancedExpander.Expanded += (_, _) => RefreshRawDevices();
        SystemWriteCheck.Checked += (_, _) => UpdateTargetNote();
        SystemWriteCheck.Unchecked += (_, _) => UpdateTargetNote();
        OpenLogsButton.Click += (_, _) => OpenLogDirectory();
        RefreshTargetsButton.Click += (_, _) => RefreshTargets();
        TargetCombo.SelectionChanged += (_, _) => UpdateTargetNote();

        SimpleProfileCombo.SelectionChanged += (_, _) => OnSimpleProfileChanged();

        // 简单模式的四个可编辑数值框：
        // 1) 选中预设 → 立即按“下拉项文本”应用（不依赖 Text/焦点事件时序，避免"点了没反应"）；
        // 2) 自己输入（回车/失焦）→ 解析 Text 应用；
        // 3) 下拉打开期间不处理，避免改写控件把下拉关掉。
        _simpleCombos[SimpleBlockSizeCombo] = (
            text => SimpleInputParser.TryParseBlockSize(text, out var blockSize, out var blockDisplay)
                ? (blockSize, blockDisplay)
                : null,
            value => _config.TrySet("bs", value));

        _simpleCombos[SimpleSizeCombo] = (
            text => SimpleInputParser.TryParseSize(text, out var size, out var sizeDisplay)
                ? (size, sizeDisplay)
                : null,
            value => _config.TrySet("size", value));

        _simpleCombos[SimpleDurationCombo] = (
            text => SimpleInputParser.TryParseDuration(text, out var runtime, out var runtimeDisplay)
                ? (runtime, runtimeDisplay)
                : null,
            value => _config.TrySet("runtime", value));

        _simpleCombos[SimpleSamplingCombo] = (
            text => SimpleInputParser.TryParseSampling(text, out var sampling, out var samplingDisplay)
                ? (sampling.ToString(), samplingDisplay)
                : null,
            value => SimpleTestProfiles.ApplySampling(_config, int.Parse(value)));

        _simpleCombos[SimpleQueueDepthCombo] = (
            text => SimpleInputParser.TryParseCount(text, 1, 1024, out var depth, out var depthDisplay)
                ? (depth, depthDisplay)
                : null,
            value => _config.TrySet("iodepth", value));

        _simpleCombos[SimpleJobCountCombo] = (
            text => SimpleInputParser.TryParseCount(text, 1, 64, out var jobs, out var jobsDisplay)
                ? (jobs, jobsDisplay)
                : null,
            value => _config.TrySet("numjobs", value));

        foreach (var (comboBox, spec) in _simpleCombos)
        {
            var combo = comboBox;

            combo.SelectionChanged += (_, _) =>
            {
                if (_suppressSimple || combo.SelectedItem is not string presetText)
                    return;

                // 直接用下拉项文本应用，保证"选中即生效"。
                if (spec.Parse(presetText) is { } parsed)
                {
                    spec.Apply(parsed.Value);

                    // 可编辑 ComboBox 在部分 DPI/焦点组合下会延迟刷新显示；
                    // 选中预设后显式同步文本并重绘，避免“值生效了但选项显示不变”。
                    if (!string.Equals(combo.Text, presetText, StringComparison.Ordinal))
                        combo.Text = presetText;
                    combo.SelectedItem = presetText;
                    combo.InvalidateVisual();

                    RefreshSimpleEditors();
                    StatusText.Text = "准备就绪";
                }
            };

            combo.DropDownClosed += (_, _) => ApplySimpleNumericSettings();
            combo.LostFocus += (_, _) =>
            {
                if (!combo.IsDropDownOpen)
                    ApplySimpleNumericSettings();
            };
            combo.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                ApplySimpleNumericSettings();
                e.Handled = true;
            };
        }

        SaveJobButton.Click += (_, _) => SaveJobFile();
        CopyJobButton.Click += (_, _) => CopyJobToClipboard();
        SaveConfigButton.Click += (_, _) => SaveConfiguration();
        LoadConfigButton.Click += (_, _) => LoadConfiguration();
        ResetParamsButton.Click += (_, _) => ResetToDefault();
    }

    /// <summary>切换测试项目：应用 rw/队列深度，并把块大小同步成该项目的推荐值。</summary>
    private void OnSimpleProfileChanged()
    {
        if (_suppressSimple || SimpleProfileCombo.SelectedItem is not SimpleTestProfile profile)
            return;

        SimpleTestProfiles.ApplyProfile(_config, profile);

        _suppressSimple = true;
        SetComboText(SimpleBlockSizeCombo,
            SimpleInputParser.FormatBlockSize(
                profile.Values.GetValueOrDefault("bs", SimpleTestProfiles.DefaultBlockSize)));
        SetComboText(SimpleQueueDepthCombo,
            profile.Values.GetValueOrDefault("iodepth", SimpleTestProfiles.DefaultQueueDepth));
        _suppressSimple = false;

        ApplySimpleNumericSettings();
    }

    /// <summary>应用「块大小 / 文件大小 / 测试时长 / 采样间隔」（自定义输入路径：回车/失焦）。</summary>
    private void ApplySimpleNumericSettings()
    {
        if (_suppressSimple)
            return;

        var allValid = true;
        foreach (var (combo, spec) in _simpleCombos)
            allValid &= TryApplyCombo(combo, spec.Parse, spec.Apply);

        RefreshSimpleEditors();

        var fineSamplingHint = int.TryParse(_config.Get("log_avg_msec"), out var samplingMs) && samplingMs <= 50
            ? UiLanguage.IsEnglish
                ? $"Sampling {samplingMs} ms: latency curve is dense, but fio's throughput/IOPS logs may be sparsified by internal scheduling (the chart uses actual log points)."
                : $"采样 {samplingMs}ms：延迟曲线会很密，但 fio 的吞吐量/IOPS 日志可能被内部调度稀疏化（图表按实际日志点绘制）。"
            : string.Empty;

        StatusText.Text = !allValid
            ? UiLanguage.IsEnglish
                ? "Invalid value: duration in seconds (e.g. 1800), size like 4g/512, block size like 4k/4096, sampling like 10 or 10ms, queue depth/jobs positive integers (e.g. 32, 1)."
                : "数值格式不对：时长输入秒数（如 1800），大小可输入 4g/512，块大小可输入 4k/4096，" +
                  "采样间隔如 10 或 10ms，队列深度/队列数输入正整数（如 32、1）。"
            : fineSamplingHint.Length > 0 ? fineSamplingHint : (UiLanguage.IsEnglish ? "Ready" : "准备就绪");
    }

    /// <summary>参数变化后刷新高级编辑区/校验/预览。</summary>
    private void RefreshSimpleEditors()
    {
        _editor.Refresh();
        UpdateValidationAndPreview();
    }

    /// <summary>解析并应用一个可编辑下拉框；失败时把边框标红。</summary>
    private bool TryApplyCombo(ComboBox combo, Func<string, (string Value, string Display)?> parser, Action<string> apply)
    {
        var parsed = parser(combo.Text ?? string.Empty);
        if (parsed is null)
        {
            combo.BorderBrush = Brushes.Firebrick;
            return false;
        }

        apply(parsed.Value.Value);

        // 仅在显示需要归一化时才改写文本（例如输入 4g → 4 GB），避免无谓的选择事件抖动。
        if (!string.Equals(combo.Text, parsed.Value.Display, StringComparison.Ordinal))
        {
            _suppressSimple = true;
            SetComboText(combo, parsed.Value.Display);
            _suppressSimple = false;
        }

        combo.ClearValue(Control.BorderBrushProperty);
        combo.InvalidateVisual();
        return true;
    }

    private static void SetComboText(ComboBox combo, string text)
    {
        var index = combo.Items.IndexOf(text);
        if (index >= 0)
        {
            combo.SelectedIndex = index;
        }
        else
        {
            combo.SelectedIndex = -1;
            combo.Text = text;
        }

        combo.InvalidateVisual();
    }

    // ---------------------------------------------------------------- 图表

    private bool _chartUpdateQueued;

    /// <summary>把短时间内的多次图表刷新合并成一次，避免日志/进度回调把 UI 线程淹没。</summary>
    private void RequestChartUpdate()
    {
        if (_chartUpdateQueued)
            return;

        _chartUpdateQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _chartUpdateQueued = false;
            UpdateChart();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
    private void UpdateChart()
    {
        if (XAxisCombo.SelectedItem is not AxisChoice xChoice ||
            YAxisCombo.SelectedItem is not AxisChoice yChoice)
            return;

        _currentXDimension = (XAxisDimension)xChoice.Value;
        _currentYMetric = (YAxisMetric)yChoice.Value;

        // 界面只保留散点图（折线图已移除）。
        const ChartRenderStyle style = ChartRenderStyle.Scatter;

        ChartModel model;
        string sourceLabel;

        if (_plotMode == PlotMode.Realtime && (_hasLiveLogData || _hasLiveData || _isRunning))
        {
            model = BuildLiveChartModel(style);
            sourceLabel = _hasLiveLogData
                ? UiLanguage.IsEnglish
                    ? $"Realtime chart · full log sampling: bw {_liveBandwidthSamples.Count} / " +
                      $"iops {_liveIopsSamples.Count} / lat {_liveLatencySamples.Count} points (no image files)"
                    : $"实时绘图 · 全量日志采样：bw {_liveBandwidthSamples.Count} / " +
                      $"iops {_liveIopsSamples.Count} / lat {_liveLatencySamples.Count} 点（不生成图片文件）"
                : UiLanguage.IsEnglish
                    ? "Realtime chart · progress sampling while testing (~4 points/s; fio writes logs at the end, then all log points are shown)"
                    : $"实时绘图 · 测试中为进度采样（约每秒 4 点；fio 日志在测试结束后才写盘，完成后自动切换全部日志点）";
        }
        else if (DatasetCombo.SelectedItem is DatasetChoice datasetChoice &&
                 _datasets.TryGetValue(datasetChoice.Key, out var data))
        {
            model = ApplyViewport(BuildDatasetModel(data, style));
            sourceLabel = SeriesBuilder.Describe(data);
        }
        else
        {
            return;
        }

        model = LocalizeChartModel(model);
        ChartView.Model = model;

        var xRange = model.ResolveX();
        var yRange = model.ResolveY();
        var styleLabel = UiLanguage.Translate("散点图");
        var modeLabel = UiLanguage.Translate(_plotMode == PlotMode.Realtime ? "实时绘图" : "完成后绘图");

        ChartSummary.Text = $"{model.XTitle}  ×  {model.YTitle}    ·    {styleLabel}    ·    {modeLabel}";

        // 明确展示"图表里实际画了多少个点"，避免与日志总行数混淆。
        var pointSummary = model.Series.Count == 0
            ? UiLanguage.Translate("曲线点数：0")
            : UiLanguage.Translate("曲线点数：") + string.Join(" / ", model.Series.Select(s => $"{s.Name} {s.Count}"));

        ChartDetail.Text = UiLanguage.IsEnglish
            ? $"{sourceLabel}{Environment.NewLine}" +
              $"{pointSummary}{Environment.NewLine}" +
              $"X {model.FormatX(xRange.Min)} – {model.FormatX(xRange.Max)} (step {model.FormatX(xRange.Step)})   " +
              $"Y {model.FormatY(yRange.Min)} – {model.FormatY(yRange.Max)} (step {model.FormatY(yRange.Step)})"
            : $"{sourceLabel}{Environment.NewLine}" +
              $"{pointSummary}{Environment.NewLine}" +
              $"横轴 {model.FormatX(xRange.Min)} – {model.FormatX(xRange.Max)}（步长 {model.FormatX(xRange.Step)}）   " +
              $"纵轴 {model.FormatY(yRange.Min)} – {model.FormatY(yRange.Max)}（步长 {model.FormatY(yRange.Step)}）";
    }

    private static ChartModel LocalizeChartModel(ChartModel model)
    {
        if (!UiLanguage.IsEnglish)
            return model;

        return model with
        {
            Title = UiLanguage.Translate(model.Title),
            Subtitle = model.Subtitle is null ? null : UiLanguage.Translate(model.Subtitle),
            EmptyMessage = model.EmptyMessage is null ? null : UiLanguage.Translate(model.EmptyMessage),
        };
    }

    /// <summary>实时模式：根据进度缓冲构建模型（日志尚未就绪时的兜底）。</summary>
    private ChartModel BuildLiveModel(ChartRenderStyle style) =>
        _liveBuffer.Build(
            _currentXDimension,
            _currentYMetric,
            style,
            title: $"实时曲线 · {ChartModel.YAxisTitle(_currentYMetric)}",
            subtitle: "实时绘图 · 测试中为进度采样（约每秒 4 点），完成后显示全部日志点");

    /// <summary>实时模式：优先用 fio 日志的全分辨率采样；日志还没出现时退回进度点。</summary>
    private ChartModel BuildLiveChartModel(ChartRenderStyle style) =>
        (_hasLiveLogData ? BuildLiveLogModel(style) : null) ?? ApplyViewport(BuildLiveModel(style));

    /// <summary>
    /// 全分辨率实时曲线：数据来自 fio 运行中已刷出的日志（通常测试结束后才完整写盘），
    /// 与"完成后绘图"使用同一批日志点，因此点数一致。
    /// </summary>
    private ChartModel? BuildLiveLogModel(ChartRenderStyle style)
    {
        var samples = _currentYMetric switch
        {
            YAxisMetric.Iops => _liveIopsSamples,
            YAxisMetric.Latency => _liveLatencySamples,
            _ => _liveBandwidthSamples,
        };
        if (samples.Count == 0)
            return null;

        var span = samples[^1].TimeMs / 1000d;

        // 长测试默认给 60 秒窗口（与完成后绘图一致）。
        if (_viewportWindowSeconds is null && span > 120)
        {
            _suppressViewport = true;
            ViewportRangeCombo.SelectedIndex = 3;
            _suppressViewport = false;
            _viewportWindowSeconds = 60;
        }

        double? start = null;
        double? end = null;
        if (_viewportWindowSeconds is > 0)
        {
            var window = _viewportWindowSeconds.Value;
            var maxOffset = Math.Max(0, span - window);
            var follow = FollowLatestCheck.IsChecked == true;
            var offset = follow ? maxOffset : Math.Clamp(ViewportSlider.Value, 0, maxOffset);
            start = offset;
            end = offset + window;
            UpdateViewportUi(span, enabled: maxOffset > 0.001, maxOffset, offset);
        }
        else
        {
            UpdateViewportUi(span, enabled: false);
        }

        // 时间横轴：只把窗口内的采样交给渲染（数据仍全量保留），长测试也不会卡。
        if (_currentXDimension == XAxisDimension.ElapsedSeconds && start is not null && end is not null)
        {
            var windowStart = start.Value;
            var windowEnd = end.Value;
            var windowed = new ChartDataSet(
                "实时绘图", "实时日志采样（全分辨率）", false,
                FilterSamplesByTime(_liveBandwidthSamples, windowStart, windowEnd),
                FilterSamplesByTime(_liveIopsSamples, windowStart, windowEnd))
            {
                LatencySamples = FilterSamplesByTime(_liveLatencySamples, windowStart, windowEnd),
            };

            var model = SeriesBuilder.Build(
                windowed,
                _currentXDimension,
                _currentYMetric,
                ReadAxisSpec(AutoXCheck, XMinBox, XMaxBox, XStepBox),
                ReadAxisSpec(AutoYCheck, YMinBox, YMaxBox, YStepBox),
                renderStyle: style,
                zeroBasedTimeAxis: false);

            return model with { XAxis = AxisSpec.Manual(windowStart, windowEnd) };
        }

        // 累计传输量横轴需要全量数据做积分。
        var full = new ChartDataSet(
            "实时绘图", "实时日志采样（全分辨率）", false,
            _liveBandwidthSamples.ToList(), _liveIopsSamples.ToList())
        {
            LatencySamples = _liveLatencySamples.ToList(),
        };

        var fullModel = SeriesBuilder.Build(
            full,
            _currentXDimension,
            _currentYMetric,
            ReadAxisSpec(AutoXCheck, XMinBox, XMaxBox, XStepBox),
            ReadAxisSpec(AutoYCheck, YMinBox, YMaxBox, YStepBox),
            renderStyle: style,
            zeroBasedTimeAxis: false);

        return ApplyViewport(fullModel);
    }

    private static IReadOnlyList<FioLogSample> FilterSamplesByTime(
        IReadOnlyList<FioLogSample> samples, double startSeconds, double endSeconds)
    {
        var startMs = (long)Math.Floor(startSeconds * 1000);
        var endMs = (long)Math.Ceiling(endSeconds * 1000);
        var result = new List<FioLogSample>(samples.Count);
        foreach (var sample in samples)
        {
            if (sample.TimeMs < startMs || sample.TimeMs > endMs)
                continue;
            result.Add(sample);
        }
        return result;
    }

    /// <summary>完成后绘图：用日志数据集构建；时间轴从第一个数据点起算（启动等待不计入）。</summary>
    private ChartModel BuildDatasetModel(ChartDataSet data, ChartRenderStyle style) =>
        SeriesBuilder.Build(
            data,
            _currentXDimension,
            _currentYMetric,
            ReadAxisSpec(AutoXCheck, XMinBox, XMaxBox, XStepBox),
            ReadAxisSpec(AutoYCheck, YMinBox, YMaxBox, YStepBox),
            renderStyle: style,
            zeroBasedTimeAxis: _currentXDimension == XAxisDimension.ElapsedSeconds);

    /// <summary>按"显示范围 + 滑动条"裁剪 X 轴，并同步滚动条状态。</summary>
    private ChartModel ApplyViewport(ChartModel model)
    {
        var span = model.Series
            .Where(series => series.Points.Count > 0)
            .SelectMany(series => series.Points)
            .Select(point => point.X)
            .DefaultIfEmpty(0)
            .Max();

        // 长时间数据默认给一个合适的窗口（超过 120 秒自动用 60 秒窗口）。
        if (_viewportWindowSeconds is null && span > 120)
        {
            _suppressViewport = true;
            ViewportRangeCombo.SelectedIndex = 3; // 60 秒
            _suppressViewport = false;
            _viewportWindowSeconds = 60;
        }

        if (_viewportWindowSeconds is not > 0)
        {
            UpdateViewportUi(span, enabled: false);
            return model;
        }

        var window = _viewportWindowSeconds.Value;
        var maxOffset = Math.Max(0, span - window);
        if (maxOffset <= 0.001)
        {
            UpdateViewportUi(span, enabled: false);
            return model;
        }

        var follow = FollowLatestCheck.IsChecked == true;
        var offset = follow ? maxOffset : Math.Clamp(ViewportSlider.Value, 0, maxOffset);
        var start = offset;
        var end = offset + window;

        var filtered = model.Series
            .Select(series => series with
            {
                Points = series.Points.Where(point => point.X >= start - 1e-9 && point.X <= end + 1e-9).ToList(),
            })
            .ToList();

        UpdateViewportUi(span, enabled: true, maxOffset, offset);
        return model with { XAxis = AxisSpec.Manual(start, end), Series = filtered };
    }

    private void UpdateViewportUi(double span, bool enabled, double maxOffset = 0, double offset = 0)
    {
        _suppressViewport = true;
        ViewportSlider.IsEnabled = enabled;
        ViewportSlider.Maximum = Math.Max(0.001, maxOffset);
        ViewportSlider.Value = Math.Clamp(offset, 0, Math.Max(0.001, maxOffset));
        _suppressViewport = false;
    }

    private void OnPlotModeChanged()
    {
        if (_suppressViewport)
            return;

        _plotMode = PlotModeCombo.SelectedIndex == 1 ? PlotMode.AfterRun : PlotMode.Realtime;

        if (_plotMode == PlotMode.AfterRun)
        {
            var lastRun = _datasetChoices.FirstOrDefault(choice => choice.Key == "last-run");
            if (lastRun is not null)
                DatasetCombo.SelectedItem = lastRun;
        }

        UpdateChart();
    }

    private void OnViewportRangeChanged()
    {
        if (_suppressViewport)
            return;

        _viewportWindowSeconds = ViewportRangeCombo.SelectedIndex switch
        {
            1 => 10,
            2 => 30,
            3 => 60,
            4 => 120,
            5 => 300,
            _ => null,
        };

        UpdateChart();
    }

    private void OnViewportSliderChanged()
    {
        if (_suppressViewport)
            return;

        // 手动拖动 = 不再自动跟随最新数据
        _suppressViewport = true;
        FollowLatestCheck.IsChecked = false;
        _suppressViewport = false;

        UpdateChart();
    }

    private static AxisSpec? ReadAxisSpec(CheckBox auto, TextBox minBox, TextBox maxBox, TextBox stepBox)
    {
        if (auto.IsChecked == true)
            return null;

        static double? Parse(TextBox box) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        return new AxisSpec
        {
            AutoScale = false,
            ManualMin = Parse(minBox),
            ManualMax = Parse(maxBox),
            ManualStep = Parse(stepBox),
        };
    }

    private void UpdateAxisBoxState()
    {
        var xAuto = AutoXCheck.IsChecked == true;
        var yAuto = AutoYCheck.IsChecked == true;
        XMinBox.IsEnabled = XMaxBox.IsEnabled = XStepBox.IsEnabled = !xAuto;
        YMinBox.IsEnabled = YMaxBox.IsEnabled = YStepBox.IsEnabled = !yAuto;
    }

    private void ExportChartPng()
    {
        try
        {
            var model = ChartView.Model;
            if (model is null || !model.HasData)
            {
                MessageBox.Show(this, "当前没有可导出的数据。", "无可导出内容", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "PNG 图片 (*.png)|*.png",
                FileName = "fio-chart.png",
                Title = "导出图表",
            };
            if (dialog.ShowDialog(this) != true)
                return;

            var width = Math.Max(800, (int)ChartView.ActualWidth);
            var height = Math.Max(480, (int)ChartView.ActualHeight);
            ChartImageExporter.SavePng(model, dialog.FileName, width, height);
            StatusText.Text = $"已导出：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>供离屏自动化：直接加载一个包含 fio 日志的目录。</summary>
    public void LoadLogDirectoryForAutomation(string directory)
    {
        var data = FioLogDataSetLoader.FromDirectory(directory);
        if (data.TotalSamples == 0)
            return;

        var key = "user:" + directory;
        AddDataset(key, "自定义日志：" + Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)),
            data with { IsSynthetic = false });
        DatasetCombo.SelectedItem = _datasetChoices.First(choice => choice.Key == key);
        UpdateChart();
    }
    private void OpenLogDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "选择包含 fio bw / iops 日志的目录" };
        if (dialog.ShowDialog(this) != true)
            return;

        var data = FioLogDataSetLoader.FromDirectory(dialog.FolderName);
        if (data.TotalSamples == 0)
        {
            MessageBox.Show(this,
                "该目录里没有找到可识别的 fio 日志（*_bw.*.log / *_iops.*.log）。",
                "没有可用日志", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var key = "user:" + dialog.FolderName;
        AddDataset(key, "自定义日志：" + Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar)),
            data with { IsSynthetic = false });
        DatasetCombo.SelectedItem = _datasetChoices.First(choice => choice.Key == key);
        UpdateChart();
    }

    /// <summary>
    /// 打开 results 结果目录（依赖系统资源管理器）。
    ///
    /// 策略按权限分流，避免踩系统限制：
    /// - 普通模式：直接启动 explorer.exe（最常用；不经过 ShellExecute）；
    /// - 管理员模式：Shell.Application COM（不会以提权令牌新建 explorer，规避 0xc0000142）；
    /// - 都失败：复制路径 + 提示手动打开（不会卡住、不弹系统错误码）。
    /// 注：若项目放在带沙箱权限的目录，Windows 可能禁止资源管理器打开该目录，建议放到普通目录再试。
    /// </summary>
    private void OpenResultsDirectory()
    {
        string directory;
        try
        {
            directory = ResultsDirectory;
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开结果目录", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var mode = TryOpenResultsInExplorer(directory);
        WriteOpenResultsLog(directory, mode);

        if (mode != "failed")
            return;

        // 兜底：复制路径并提示手动打开（不弹系统错误码、不阻塞界面）。
        try
        {
            Clipboard.SetText(directory);
        }
        catch
        {
            // 剪贴板被占用时忽略。
        }

        MessageBox.Show(this,
            "无法自动打开资源管理器（系统限制了窗口打开通道）。\n\n" +
            $"结果目录：{directory}\n\n" +
            "路径已复制到剪贴板，请粘贴到资源管理器地址栏打开。",
            "无法打开结果目录", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>尝试用资源管理器打开；返回 explorer / com / shell / failed。</summary>
    private static string TryOpenResultsInExplorer(string directory)
    {
        if (RawDeviceProbe.IsElevated)
        {
            if (TryShellComExplore(directory))
                return "com(elevated)";
            if (TryStartExplorerProcess(directory))
                return "explorer(elevated)";
            return "failed";
        }

        if (TryStartExplorerProcess(directory))
            return "explorer";
        if (TryShellComExplore(directory))
            return "com";
        if (TryShellExecuteEx(directory))
            return "shell";
        return "failed";
    }

    /// <summary>直接启动 explorer.exe（UseShellExecute=false，不经过 ShellExecute）。</summary>
    private static bool TryStartExplorerProcess(string directory)
    {
        try
        {
            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!File.Exists(explorer))
                explorer = "explorer.exe";

            Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = "\"" + directory + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用 Shell.Application COM 的 Explore 打开文件夹（管理员模式下更稳）。</summary>
    private static bool TryShellComExplore(string directory)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            var shell = shellType is null ? null : Activator.CreateInstance(shellType);
            if (shell is null)
                return false;

            shell.GetType().InvokeMember(
                "Explore",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [directory]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ShellExecuteEx（NO_UI：失败时不弹系统错误框）。</summary>
    private static bool TryShellExecuteEx(string directory)
    {
        try
        {
            var info = new ShellExecuteInfo
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<ShellExecuteInfo>(),
                fMask = 0x100 | 0x400, // NOASYNC | FLAG_NO_UI
                lpVerb = "open",
                lpFile = directory,
                nShow = 1,
            };
            return ShellExecuteExW(ref info);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteOpenResultsLog(string directory, string mode)
    {
        try
        {
            var logDirectory = Path.Combine(
                WorkspaceLocator.FindRoot() ?? AppContext.BaseDirectory, "runtime");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "open-results.log"),
                $"{DateTime.Now:O} mode={mode} elevated={RawDeviceProbe.IsElevated} dir={directory}" +
                Environment.NewLine);
        }
        catch
        {
            // 诊断日志写失败不影响功能。
        }
    }

    /// <summary>供离屏自动化：尝试打开结果目录并返回方式（explorer / com / shell / failed）。</summary>
    public string OpenResultsDirectoryForAutomation()
    {
        var directory = ResultsDirectory;
        Directory.CreateDirectory(directory);
        var mode = TryOpenResultsInExplorer(directory);
        WriteOpenResultsLog(directory, mode);
        return mode;
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);
    // ---------------------------------------------------------------- fio 参数配置
    private void ResetToDefault()
    {
        var defaults = SimpleTestProfiles.CreateDefaultConfig();
        _config.ResetAll();
        foreach (var (name, value) in defaults.Values)
            _config.TrySet(name, value);
        _config.JobName = defaults.JobName;

        _suppressSimple = true;
        SimpleProfileCombo.SelectedIndex = 0;
        SetComboText(SimpleBlockSizeCombo,
            SimpleInputParser.FormatBlockSize(defaults.Get("bs") ?? SimpleTestProfiles.DefaultBlockSize));
        SetComboText(SimpleSizeCombo,
            SimpleInputParser.FormatSize(defaults.Get("size") ?? SimpleTestProfiles.DefaultSize));
        SetComboText(SimpleDurationCombo,
            SimpleInputParser.FormatDuration(defaults.Get("runtime") ?? SimpleTestProfiles.DefaultRuntime));
        SetComboText(SimpleSamplingCombo,
            SimpleInputParser.FormatSampling(SimpleTestProfiles.DefaultSamplingMilliseconds));
        _suppressSimple = false;

        _editor.Refresh();
        UpdateValidationAndPreview();
        StatusText.Text = "已恢复默认设置";
    }

    private void UpdateValidationAndPreview()
    {
        var validation = _config.Validate();

        JobPreview.Text = _config.BuildJobFile("由 DriveMark 生成。");
        CommandPreview.Text = "fio " + string.Join(" ", _config.BuildCommandLineArguments("job.fio"));

        ValidationHost.Children.Clear();
        if (validation.Issues.Count == 0)
        {
            ValidationHost.Children.Add(MakeIssueText("参数无问题。", Brushes.SeaGreen));
        }
        else
        {
            foreach (var issue in validation.Issues.OrderBy(i => i.Severity))
                ValidationHost.Children.Add(MakeIssueText(issue.ToString(), BrushFor(issue.Severity)));
        }

        ValidationTab.Header = $"校验 ({validation.Issues.Count})";
    }

    private static TextBlock MakeIssueText(string text, Brush brush) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = brush,
        FontSize = 11,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private static Brush BrushFor(ValidationSeverity severity) => severity switch
    {
        ValidationSeverity.Error => Brushes.Firebrick,
        ValidationSeverity.Warning => Brushes.DarkOrange,
        _ => Brushes.SlateGray,
    };

    private void SaveJobFile()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "fio job 文件 (*.fio)|*.fio|所有文件 (*.*)|*.*",
            FileName = _config.JobName + ".fio",
            Title = "保存 fio job 文件",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, JobPreview.Text);
            StatusText.Text = $"已保存 job 文件：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyJobToClipboard()
    {
        try
        {
            Clipboard.SetText(JobPreview.Text);
            StatusText.Text = "job 文件内容已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void SaveConfiguration()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "DriveMark 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = "fio-params.json",
            Title = "保存参数配置",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, _config.ToJson());
            StatusText.Text = $"已保存配置：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadConfiguration()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "DriveMark 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            Title = "加载参数配置",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var loaded = FioJobConfig.FromJson(File.ReadAllText(dialog.FileName));
            _config.ResetAll();
            foreach (var (name, value) in loaded.Values)
                _config.TrySet(name, value);
            _config.JobName = loaded.JobName;
            _editor.Refresh();
            UpdateValidationAndPreview();
            StatusText.Text = $"已加载配置：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- 测试运行

    private void InitializeRunControls()
    {
        _runner.ProgressChanged += (_, progress) =>
            Dispatcher.BeginInvoke(() =>
            {
                UpdateLiveProgress(progress);
                OnLiveProgress(progress);
            });
        _runner.LogSamplesReceived += (_, e) =>
            Dispatcher.BeginInvoke(() => OnLiveLogSamples(e.Samples));
        _runner.OutputReceived += (_, line) =>
            Dispatcher.BeginInvoke(() => AppendRunLog(line));

        StartTestButton.Click += async (_, _) => await ExecuteRunAsync();
        StopTestButton.Click += (_, _) => StopRun();

        Closing += (_, e) =>
        {
            if (!_isRunning)
                return;

            var choice = MessageBox.Show(this,
                "测试正在进行，关闭窗口会中止测试并清理测试数据。确定关闭？",
                "测试进行中", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _runCancellation?.Cancel();
        };
    }

    private async Task ExecuteRunAsync()
    {
        if (_isRunning)
            return;

        if (SelectedTarget is not { } target)
        {
            MessageBox.Show(this, "请先选择要测试的硬盘。", "未选择硬盘", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var config = _config.Clone();
        var rw = config.EffectiveValue("rw") ?? "read";
        var isWrite = FioValueParser.IsWriteMode(rw);
        var allowSystemWrite = isWrite && target.IsSystemDrive && SystemWriteCheck.IsChecked == true;

        // 系统盘写入默认关闭：需要显式勾选"允许系统盘写入"。
        if (isWrite && target.IsSystemDrive && !allowSystemWrite)
        {
            if (!_automationMode)
            {
                MessageBox.Show(this,
                    $"系统盘（{target.DriveLetter}:）默认禁止写入测试。\n\n" +
                    "如确需测试系统盘，请勾选底部的「允许系统盘写入」，并再次确认。\n" +
                    $"程序只会写入 {target.DriveLetter}:\\DriveMark-test\\<运行号>\\，绝不会格式化或清空磁盘。",
                    "系统盘写入默认关闭", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        if (isWrite && AllowWriteCheck.IsChecked != true)
        {
            if (!_automationMode)
            {
                MessageBox.Show(this,
                    "当前测试项目会写入硬盘。\n\n确认目标硬盘可以写入后，请勾选底部的「允许写入测试」再开始。",
                    "需要确认写入", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        if (isWrite && allowSystemWrite && !_automationMode)
        {
            var confirmSystem = MessageBox.Show(this,
                $"⚠ 即将对系统盘 {target.DriveLetter}: 执行写入测试。\n\n" +
                $"测试文件只会创建在 {target.DriveLetter}:\\DriveMark-test\\<运行号>\\ 下，结束后自动删除；\n" +
                "程序绝不会格式化、清空或删除任何其它文件。\n\n是否确认继续？",
                "确认系统盘写入测试", MessageBoxButton.YesNo, MessageBoxImage.Stop);
            if (confirmSystem != MessageBoxResult.Yes)
                return;
        }
        else if (isWrite && !_automationMode)
        {
            var confirm = MessageBox.Show(this,
                $"即将对 {target.DriveLetter}: 盘做写入测试：\n\n" +
                $"rw={rw}  bs={config.EffectiveValue("bs")}  size={config.EffectiveValue("size")}\n\n" +
                "测试数据只写入该盘的 DriveMark-test 目录，结束后自动删除。是否继续？",
                "确认写入测试", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var saveResults = SaveResultsCheck.IsChecked == true;
        var runRoot = saveResults ? ResultsDirectory : null;

        _isRunning = true;
        StartTestButton.IsEnabled = false;
        StopTestButton.IsEnabled = true;
        RunProgressBar.Value = 0;
        RunLogText.Clear();
        LastRunText.Text = string.Empty;

        // 实时绘图：清空上一轮的曲线，进入"等待数据"状态。
        _liveBuffer.Clear();
        _hasLiveData = false;
        _liveBandwidthSamples.Clear();
        _liveIopsSamples.Clear();
        _liveLatencySamples.Clear();
        _liveSampleKeys.Clear();
        _hasLiveLogData = false;
        if (_plotMode == PlotMode.Realtime)
            UpdateChart();
        AppendRunLog($"=== 开始测试 {DateTime.Now:HH:mm:ss} ===");
        AppendRunLog($"目标：{target.Describe()}");
        AppendRunLog($"参数：rw={rw} bs={config.EffectiveValue("bs")} iodepth={config.EffectiveValue("iodepth")} " +
                     $"size={config.EffectiveValue("size")} runtime={config.EffectiveValue("runtime")} " +
                     $"采样={config.EffectiveValue("log_avg_msec")}ms");
        if (saveResults)
            AppendRunLog($"结果目录：{ResultsDirectory}");
        LiveRunText.Text = "准备中（正在创建测试文件）…";
        StatusText.Text = $"正在测试 {target.DriveLetter}: 盘…";

        _runCancellation = new CancellationTokenSource();
        try
        {
            var request = new FioRunRequest
            {
                Config = config,
                TargetRoot = target.RootPath,
                AllowWrites = isWrite && AllowWriteCheck.IsChecked == true,
                AllowSystemDriveWrite = allowSystemWrite,
                DeleteTestDataAfterRun = true,
                RunRootDirectory = runRoot,
                // 实时绘图需要更密的进度行（否则曲线点太稀）。
                StatusIntervalMs = _plotMode == PlotMode.Realtime ? 250 : 1000,
                EnableFioLogSmoothing = SmoothFioLogCheck.IsChecked == true,
                RealtimeLogPlugin = RealtimePluginCheck.IsChecked == true ? _realtimeLogPlugin : null,
            };

            var result = await _runner.RunAsync(request, _runCancellation.Token);
            PresentRunResult(result);
        }
        catch (Exception ex)
        {
            AppendRunLog("运行失败：" + ex.Message);
            LiveRunText.Text = "运行失败";
            StatusText.Text = "测试未能启动：" + ex.Message;
            if (!_automationMode)
                MessageBox.Show(this, ex.Message, "测试未能启动", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isRunning = false;
            _runCancellation?.Dispose();
            _runCancellation = null;
            StopTestButton.IsEnabled = false;
            RefreshTargets();
        }
    }

    // ---------------------------------------------------------------- RAW 只读测试

    /// <summary>刷新 RAW / 物理磁盘列表（未格式化磁盘只能做只读测试）。</summary>
    private void RefreshRawDevices()
    {
        var previousPath = (RawDeviceCombo.SelectedItem as RawChoice)?.Device.DevicePath;

        var devices = RawDeviceProbe.Enumerate();
        var choices = devices
            .Select(device => new RawChoice(device.Describe(), device))
            .ToList();

        RawDeviceCombo.ItemsSource = choices;
        RawDeviceCombo.DisplayMemberPath = nameof(RawChoice.Label);

        var restore = previousPath is null
            ? null
            : choices.FirstOrDefault(choice =>
                string.Equals(choice.Device.DevicePath, previousPath, StringComparison.OrdinalIgnoreCase));
        if (restore is not null)
            RawDeviceCombo.SelectedItem = restore;
        else if (choices.Count > 0)
            RawDeviceCombo.SelectedIndex = 0;

        RawDeviceNote.Text = choices.Count == 0
            ? "未发现 RAW 卷或物理磁盘。RAW 盘需先接入电脑；整盘测试需要管理员权限。"
            : (RawDeviceProbe.IsElevated
                ? "当前有管理员权限：可对整盘做 RAW 测试。"
                : "当前为普通权限：整盘 RAW 需要『启动DriveMark（管理员）.bat』。") +
              " 默认只读；只有勾选「允许 RAW 写入（覆盖数据）」并二次确认后才会写盘（数据不可恢复）。";
    }

    /// <summary>执行一次 RAW 测试（默认只读；勾选「允许 RAW 写入」后按当前测试项目做写测试）。</summary>
    private async Task ExecuteRawRunAsync()
    {
        if (_isRunning)
            return;

        if (RawDeviceCombo.SelectedItem is not RawChoice choice)
        {
            MessageBox.Show(this, "请先选择一个 RAW 卷或物理磁盘。", "未选择 RAW 设备",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var config = _config.Clone();
        var profileRw = config.EffectiveValue("rw") ?? "read";
        var profileIsWrite = FioValueParser.IsWriteMode(profileRw);
        var allowRawWrites = RawWriteCheck.IsChecked == true && profileIsWrite;

        // 未允许写入（或当前测试项目本身是只读）→ 强制只读。
        if (!allowRawWrites)
            config.TrySet("rw", "read");

        if (!_automationMode)
        {
            if (allowRawWrites)
            {
                var writeConfirm = MessageBox.Show(this,
                    $"⚠ 你已允许 RAW 写入，即将把测试数据直接写到设备：\n\n" +
                    $"{choice.Device.DevicePath}\n{choice.Device.Describe()}\n" +
                    $"写入模式：{profileRw}\n\n" +
                    "这会永久覆盖设备上原有数据，且测试数据无法清理/恢复；\n" +
                    "该盘之后需要重新分区格式化才能正常使用。\n\n确认继续？",
                    "⚠ 确认 RAW 写入（不可恢复）", MessageBoxButton.YesNo, MessageBoxImage.Stop,
                    MessageBoxResult.No);
                if (writeConfirm != MessageBoxResult.Yes)
                    return;
            }
            else
            {
                var confirm = MessageBox.Show(this,
                    $"即将对以下设备做 RAW 只读测试：\n\n{choice.Device.DevicePath}\n{choice.Device.Describe()}\n\n" +
                    "fio 只会读取设备，不会创建测试文件，不会写入、格式化或清空磁盘。\n\n是否继续？",
                    "确认 RAW 只读测试", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }
        }

        var saveResults = SaveResultsCheck.IsChecked == true;
        var request = new FioRunRequest
        {
            Config = config,
            TargetRoot = TestTargetGuard.SystemDriveRoot, // RAW 模式不使用该字段
            RawDevicePath = choice.Device.DevicePath,
            AllowRawWrites = allowRawWrites,
            AllowWrites = false,
            AllowSystemDriveWrite = false,
            DeleteTestDataAfterRun = false,
            RunRootDirectory = saveResults ? ResultsDirectory : null,
            StatusIntervalMs = _plotMode == PlotMode.Realtime ? 250 : 1000,
            EnableFioLogSmoothing = SmoothFioLogCheck.IsChecked == true,
            RealtimeLogPlugin = RealtimePluginCheck.IsChecked == true ? _realtimeLogPlugin : null,
        };

        _isRunning = true;
        StartTestButton.IsEnabled = false;
        StopTestButton.IsEnabled = true;
        RunProgressBar.Value = 0;
        RunLogText.Clear();
        LastRunText.Text = string.Empty;

        _liveBuffer.Clear();
        _hasLiveData = false;
        _liveBandwidthSamples.Clear();
        _liveIopsSamples.Clear();
        _liveLatencySamples.Clear();
        _liveSampleKeys.Clear();
        _hasLiveLogData = false;
        if (_plotMode == PlotMode.Realtime)
            UpdateChart();

        var modeLabel = allowRawWrites ? "写入（覆盖数据，不可清理）" : "只读";
        AppendRunLog($"=== RAW {modeLabel}测试 {DateTime.Now:HH:mm:ss} ===");
        AppendRunLog($"设备：{choice.Device.DevicePath}");
        AppendRunLog($"信息：{choice.Device.Describe()}");
        if (allowRawWrites)
        {
            AppendRunLog($"模式：RAW 写入 {profileRw}（直接覆盖设备数据，无法清理/恢复）");
            AppendRunLog("警告：该盘测试后需要重新分区格式化才能正常使用。");
        }
        else
        {
            AppendRunLog("模式：RAW 只读（不创建测试文件、不写入/格式化磁盘）");
            if (RawWriteCheck.IsChecked == true)
                AppendRunLog("说明：当前测试项目为只读模式，本次仍按只读执行。");
        }
        if (saveResults)
            AppendRunLog($"结果目录：{ResultsDirectory}");
        LiveRunText.Text = "准备中（打开原始设备）…";
        StatusText.Text = allowRawWrites
            ? "正在对 RAW 设备做写入测试（数据不可恢复）…"
            : "正在对 RAW 设备做只读测试…";

        _runCancellation = new CancellationTokenSource();
        try
        {
            var result = await _runner.RunAsync(request, _runCancellation.Token);
            PresentRunResult(result);
        }
        catch (Exception ex)
        {
            AppendRunLog("运行失败：" + ex.Message);
            LiveRunText.Text = "运行失败";
            StatusText.Text = "RAW 测试未能启动：" + ex.Message;
            if (!_automationMode)
                MessageBox.Show(this, ex.Message, "RAW 测试未能启动", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isRunning = false;
            _runCancellation?.Dispose();
            _runCancellation = null;
            StopTestButton.IsEnabled = false;
            StartTestButton.IsEnabled = true;
            RefreshTargets();
            RefreshRawDevices();
        }
    }

    private void PresentRunResult(FioRunResult result)
    {
        RunProgressBar.Value = result.Succeeded ? 100 : RunProgressBar.Value;
        AppendRunLog(string.Empty);
        foreach (var line in result.BuildMetricLines())
            AppendRunLog(line);
        AppendRunLog($"退出码 {result.ExitCode} · 耗时 {result.Duration.TotalSeconds:F1} 秒 · " +
                     $"测试数据 {Units.Bytes(result.TestDataBytes)}，清理{(result.CleanupSucceeded ? "成功" : "失败")}");
        foreach (var log in result.LogFiles)
            AppendRunLog("日志：" + log);
        AppendRunLog($"运行目录：{result.RunDirectory}");

        if (result.ErrorMessage is not null)
            AppendRunLog("错误：" + result.ErrorMessage);

        // 失败时把 fio 的 stderr 也打出来（硬盘断开、权限不足等都会体现在这里）。
        if (!result.Succeeded && result.StderrTail.Count > 0)
        {
            AppendRunLog("fio 错误输出（最后 15 行）：");
            foreach (var line in result.StderrTail.TakeLast(15))
                AppendRunLog("  " + line);
        }

        if (result.DataSet.TotalSamples > 0)
        {
            AddDataset("last-run", "本次测试结果", result.DataSet with { Name = "本次测试：" + result.DataSet.Name });

            // 完成后绘图模式才自动切到日志版；实时模式保留刚画好的实时曲线。
            if (_plotMode == PlotMode.AfterRun)
                DatasetCombo.SelectedItem = _datasetChoices.First(choice => choice.Key == "last-run");

            UpdateChart();
        }

        var read = result.Json?.FirstRead;
        var write = result.Json?.FirstWrite;
        var parts = new List<string>();
        if (read is not null)
            parts.Add($"读 {Units.Iops(read.Iops)} IOPS / {Units.MebibytesPerSecond(read.MebibytesPerSecond)}");
        if (write is not null)
            parts.Add($"写 {Units.Iops(write.Iops)} IOPS / {Units.MebibytesPerSecond(write.MebibytesPerSecond)}");

        LastRunText.Text = result.Succeeded
            ? string.Join("   ", parts)
            : result.Cancelled ? "已取消" : "测试失败";

        LiveRunText.Text = result.Succeeded ? "完成" : result.Cancelled ? "已取消" : "失败";

        if (result.Succeeded)
        {
            StatusText.Text = $"测试完成（{result.Duration.TotalSeconds:F1} 秒），结果已显示在图表中";
        }
        else if (result.Cancelled)
        {
            StatusText.Text = "测试已取消，测试数据已清理";
        }
        else
        {
            var reason = result.TimedOut
                ? "测试超时（目标盘可能响应缓慢或已断开）"
                : $"测试失败：{result.ErrorMessage ?? "fio 异常退出"}";
            StatusText.Text = reason + " · 详情见「运行日志」";

            // 失败时明确提示，尤其是硬盘被拔掉/断开的情况。
            if (!_automationMode && !result.Cancelled)
            {
                MessageBox.Show(this,
                    $"测试未能完成：\n\n{result.ErrorMessage ?? "fio 异常退出"}\n\n" +
                    "常见原因：目标硬盘被拔出/断开、磁盘被其它程序占用、权限不足或磁盘空间不足。\n" +
                    "请检查硬盘连接后重试（未完成的测试数据会在下次运行前自动清理）。",
                    "测试失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void UpdateLiveProgress(FioRunProgress progress)
    {
        if (progress.Sample.Percent is { } percent)
            RunProgressBar.Value = percent;

        var parts = new List<string> { $"{progress.Elapsed.TotalSeconds:F1}s" };
        if (progress.Sample.ReadMebibytesPerSecond is { } readMib)
            parts.Add($"读 {readMib:F0} MiB/s");
        if (progress.Sample.WriteMebibytesPerSecond is { } writeMib)
            parts.Add($"写 {writeMib:F0} MiB/s");
        if (progress.Sample.ReadIops is { } readIops)
            parts.Add($"读 {Units.Iops(readIops)} IOPS");
        if (progress.Sample.WriteIops is { } writeIops)
            parts.Add($"写 {Units.Iops(writeIops)} IOPS");
        if (progress.Sample.Eta is { } eta)
            parts.Add("ETA " + Units.Duration(eta));

        LiveRunText.Text = string.Join("  ·  ", parts);
    }

    /// <summary>实时日志采样：如果 fio 在运行中已经 flush 出日志行，就追加到曲线；fio 通常要到测试结束才写完整日志。</summary>
    private void OnLiveLogSamples(IReadOnlyList<FioLogTailSample> samples)
    {
        if (_plotMode != PlotMode.Realtime || samples.Count == 0)
            return;

        foreach (var item in samples)
        {
            // 实时插件和普通日志 tail 可能同时送来同一批采样，按内容去重。
            var key = (item.Kind, item.Sample.TimeMs, item.Sample.Value,
                item.Sample.Direction, item.Sample.BlockSize, item.Sample.Priority);
            if (!_liveSampleKeys.Add(key))
                continue;

            switch (item.Kind)
            {
                case FioLogKind.Bandwidth:
                    _liveBandwidthSamples.Add(item.Sample);
                    break;
                case FioLogKind.Iops:
                    _liveIopsSamples.Add(item.Sample);
                    break;
                case FioLogKind.Latency:
                    _liveLatencySamples.Add(item.Sample);
                    break;
            }
        }

        _hasLiveLogData = true;
        RequestChartUpdate();
    }

    /// <summary>实时绘图：把一条进度采样追加到曲线（启动/布局阶段没有速率的行会被忽略）。</summary>
    private void OnLiveProgress(FioRunProgress progress)
    {
        _progressEventCount++;
        _lastProgressLine = progress.RawLine;

        if (_plotMode != PlotMode.Realtime)
            return;

        // 已有全分辨率日志数据后，进度点只用于状态栏，不再参与绘图。
        if (_hasLiveLogData)
            return;

        if (!LiveChartBuffer.SupportsMetric(_currentYMetric))
            return;

        // 进度到 100% 的那条通常是"收尾残窗"（速率骤降），画上去会在曲线末尾留下假掉点。
        if (progress.Sample.Percent is >= 99.999)
            return;

        if (!_liveBuffer.Add(progress.Elapsed, progress.Sample))
            return;

        _hasLiveData = true;
        RequestChartUpdate();
    }

    /// <summary>诊断：实时绘图的计数信息（供自动化/排查使用）。</summary>
    public string GetLiveDiagnosticsForAutomation() =>
        $"progressEvents={_progressEventCount}, livePoints={_liveBuffer.Count}, " +
        $"hasLive={_hasLiveData}, logBw={_liveBandwidthSamples.Count}, " +
        $"logIops={_liveIopsSamples.Count}, logLat={_liveLatencySamples.Count}, " +
        $"hasLog={_hasLiveLogData}, mode={_plotMode}, yMetric={_currentYMetric}, " +
        $"lastLine={_lastProgressLine ?? "(无)"}";

    private void StopRun()
    {
        if (!_isRunning)
            return;

        StopTestButton.IsEnabled = false;
        LiveRunText.Text = "正在停止…";
        _runCancellation?.Cancel();
    }

    private void AppendRunLog(string line)
    {
        RunLogText.AppendText(line + Environment.NewLine);
        RunLogText.ScrollToEnd();
    }

    // ---------------------------------------------------------------- 供离屏截图/自动化使用

    /// <summary>按名称选择内置数据集。</summary>
    public void SelectDataset(string key)
    {
        var choice = _datasetChoices.FirstOrDefault(item => item.Key == key);
        if (choice is not null)
            DatasetCombo.SelectedItem = choice;
    }

    /// <summary>程序化设置坐标轴（供自动化验证界面联动）。</summary>
    public void ApplyViewSettings(
        XAxisDimension? xDimension,
        YAxisMetric? yMetric,
        double? xMin = null, double? xMax = null, double? xStep = null,
        double? yMin = null, double? yMax = null, double? yStep = null)
    {
        if (xDimension is { } x)
            XAxisCombo.SelectedIndex = x == XAxisDimension.ElapsedSeconds ? 0 : 1;
        if (yMetric is { } y)
        {
            YAxisCombo.SelectedIndex = y switch
            {
                YAxisMetric.Iops => 0,
                YAxisMetric.Latency => 2,
                _ => 1,
            };
        }

        void Apply(CheckBox autoCheck, TextBox min, TextBox max, TextBox step,
            double? minValue, double? maxValue, double? stepValue)
        {
            var anyManual = minValue is not null || maxValue is not null || stepValue is not null;
            if (anyManual)
                autoCheck.SetValue(ToggleButton.IsCheckedProperty, false);
            if (minValue is not null) min.Text = minValue.Value.ToString(CultureInfo.InvariantCulture);
            if (maxValue is not null) max.Text = maxValue.Value.ToString(CultureInfo.InvariantCulture);
            if (stepValue is not null) step.Text = stepValue.Value.ToString(CultureInfo.InvariantCulture);
        }

        Apply(AutoXCheck, XMinBox, XMaxBox, XStepBox, xMin, xMax, xStep);
        Apply(AutoYCheck, YMinBox, YMaxBox, YStepBox, yMin, yMax, yStep);
        UpdateAxisBoxState();
        UpdateChart();
    }


    /// <summary>供离屏自动化：设置绘图方式（实时绘图 / 完成后绘图）。</summary>
    public void SetPlotModeForAutomation(bool afterRun)
    {
        _suppressViewport = true;
        PlotModeCombo.SelectedIndex = afterRun ? 1 : 0;
        _suppressViewport = false;
        _plotMode = afterRun ? PlotMode.AfterRun : PlotMode.Realtime;

        if (afterRun)
        {
            var lastRun = _datasetChoices.FirstOrDefault(choice => choice.Key == "last-run");
            if (lastRun is not null)
                DatasetCombo.SelectedItem = lastRun;
        }

        UpdateChart();
    }

    /// <summary>供离屏自动化：平移视口到指定起点（秒），并取消"跟随最新"。</summary>
    public void SetViewportOffsetForAutomation(double offset)
    {
        _suppressViewport = true;
        FollowLatestCheck.IsChecked = false;
        ViewportSlider.Value = offset;
        _suppressViewport = false;
        UpdateChart();
    }

    /// <summary>供离屏自动化：设置横轴显示范围（秒；null=自动）。</summary>
    public void SetViewportForAutomation(double? seconds)
    {
        var index = seconds switch
        {
            10 => 1,
            30 => 2,
            60 => 3,
            120 => 4,
            300 => 5,
            _ => 0,
        };

        _suppressViewport = true;
        ViewportRangeCombo.SelectedIndex = index;
        _suppressViewport = false;
        _viewportWindowSeconds = index == 0 ? null : seconds;

        UpdateChart();
    }

    /// <summary>按名称应用高级预设（供自动化截图展示）。</summary>
    public void ApplyPresetByName(string presetName)
    {
        var preset = FioPresets.Find(presetName);
        if (preset is null)
            return;

        _config.ResetAll();
        foreach (var (name, value) in preset.Values)
            _config.TrySet(name, value);
        _config.JobName = "fio-gui-job";
        _editor.Refresh();
        UpdateValidationAndPreview();
    }

    /// <summary>
    /// 供离屏截图自动化使用：以只读方式在 E 盘真实跑一次测试。
    /// （开发者自检：使用默认可用盘，只读测试。）
    /// </summary>
    public async Task RunTestForAutomationAsync()
    {
        _automationMode = true;
        try
        {
            var target = TestTargetGuard.GetDefaultCandidate()
                         ?? throw new InvalidOperationException("自动化自检需要至少一个可用盘");
            SelectTargetForAutomation(target.RootPath);

            AllowWriteCheck.IsChecked = false;
            await ExecuteRunAsync();
        }
        finally
        {
            _automationMode = false;
        }
    }

    /// <summary>程序化展开「其他设置」（供自动化截图/验证）。</summary>
    public void ExpandAdvancedForAutomation() => AdvancedExpander.IsExpanded = true;

    /// <summary>供离屏自动化：对指定原始设备执行测试（开发者自检用；可显式允许写入）。</summary>
    public async Task RunRawTestForAutomationAsync(string devicePath, bool allowRawWrites = false)
    {
        _automationMode = true;
        try
        {
            AdvancedExpander.IsExpanded = true;
            RawWriteCheck.IsChecked = allowRawWrites;

            var choices = RawDeviceProbe.Enumerate()
                .Select(device => new RawChoice(device.Describe(), device))
                .ToList();
            var match = choices.FirstOrDefault(choice =>
                string.Equals(choice.Device.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                var info = RawDeviceProbe.Find(devicePath)
                           ?? throw new InvalidOperationException($"不是合法的原始设备路径：{devicePath}");
                match = new RawChoice("指定设备：" + devicePath, info);
                choices.Insert(0, match);
            }

            RawDeviceCombo.ItemsSource = choices;
            RawDeviceCombo.DisplayMemberPath = nameof(RawChoice.Label);
            RawDeviceCombo.SelectedItem = match;

            await ExecuteRawRunAsync();
        }
        finally
        {
            _automationMode = false;
        }
    }

    /// <summary>程序化选择测试硬盘。</summary>
    public void SelectTargetForAutomation(string rootPath)
    {
        if (TargetCombo.ItemsSource is not IEnumerable<TestTargetInfo> items)
            return;

        var match = items.FirstOrDefault(candidate =>
            string.Equals(candidate.RootPath, rootPath, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            TargetCombo.SelectedItem = match;
    }

    /// <summary>供离屏自动化：选择简单模式的测试项目（按名称）。</summary>
    public void SelectSimpleProfileForAutomation(string profileName)
    {
        if (SimpleProfileCombo.ItemsSource is not IEnumerable<SimpleTestProfile> profiles)
            return;

        var match = profiles.FirstOrDefault(profile =>
            profile.Name.Contains(profileName, StringComparison.Ordinal));
        if (match is not null)
            SimpleProfileCombo.SelectedItem = match;
    }

    /// <summary>供离屏自动化：设置「允许写入测试 / 允许系统盘写入」勾选状态。</summary>
    public void SetWriteOptionsForAutomation(bool allowWrites, bool allowSystemWrite)
    {
        AllowWriteCheck.IsChecked = allowWrites;
        SystemWriteCheck.IsChecked = allowSystemWrite;
    }

    /// <summary>供离屏自动化：设置「保存结果到 results 目录」。</summary>
    public void SetSaveResultsForAutomation(bool save) => SaveResultsCheck.IsChecked = save;

    /// <summary>供离屏自动化：直接设置 rw（例如 randwrite，用于 RAW 写入测试）。</summary>
    public void SetRwForAutomation(string rw)
    {
        _config.TrySet("rw", rw);
        _editor.Refresh();
        UpdateValidationAndPreview();
    }

    /// <summary>
    /// 回归自检：验证"可自由输入"之后仍能正常选择预设。
    /// 模拟真实路径：打开下拉 → 选择预设 → 关闭下拉 → 焦点移走；
    /// 并覆盖"先自定义输入，再选预设"这一曾经失效的场景。
    /// </summary>
    public string RunSimplePresetRegression()
    {
        var lines = new List<string>();

        void Snapshot(string step) => lines.Add(
            $"{step} | bs={_config.Get("bs")} size={_config.Get("size")} runtime={_config.Get("runtime")} " +
            $"log={_config.Get("log_avg_msec")} | 块='{SimpleBlockSizeCombo.Text}' 大小='{SimpleSizeCombo.Text}' " +
            $"时长='{SimpleDurationCombo.Text}' 采样='{SimpleSamplingCombo.Text}'");

        // 1) 直接选择预设：文件大小 1 GB（索引 3）
        SelectPreset(SimpleSizeCombo, 3);
        Snapshot("1. 选择文件大小预设 1GB");

        // 2) 自定义输入：时长 1800 秒
        SimpleDurationCombo.Text = "1800";
        MoveFocusAway(SimpleDurationCombo);
        Snapshot("2. 自定义输入时长 1800");

        // 3) 自定义之后再选预设：时长 30 秒（索引 2）—— 之前就是这一步失效
        SelectPreset(SimpleDurationCombo, 2);
        Snapshot("3. 自定义后选择时长预设 30 秒");

        // 4) 选择块大小预设 64 KB（索引 5）
        SelectPreset(SimpleBlockSizeCombo, 5);
        Snapshot("4. 选择块大小预设 64KB");

        // 5) 再次选择文件大小预设 4 GB（索引 5）
        SelectPreset(SimpleSizeCombo, 5);
        Snapshot("5. 选择文件大小预设 4GB");

        return string.Join(Environment.NewLine, lines);
    }

    private static void SelectPreset(ComboBox combo, int index)
    {
        combo.Focus();
        combo.IsDropDownOpen = true;
        combo.SelectedIndex = index;
        combo.IsDropDownOpen = false;
        MoveFocusAway(combo);
    }

    private static void MoveFocusAway(ComboBox combo)
    {
        combo.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    /// <summary>供离屏自动化使用：设置简单模式的自定义数值（如 1800 秒 / 4g / 4k）。</summary>
    public void ApplySimpleSettingsForAutomation(
        string? blockSize = null, string? size = null, string? duration = null, string? sampling = null,
        string? iodepth = null, string? numjobs = null)
    {
        _suppressSimple = true;
        if (blockSize is not null) SetComboText(SimpleBlockSizeCombo, blockSize);
        if (size is not null) SetComboText(SimpleSizeCombo, size);
        if (duration is not null) SetComboText(SimpleDurationCombo, duration);
        if (sampling is not null) SetComboText(SimpleSamplingCombo, sampling);
        if (iodepth is not null) SetComboText(SimpleQueueDepthCombo, iodepth);
        if (numjobs is not null) SetComboText(SimpleJobCountCombo, numjobs);
        _suppressSimple = false;

        ApplySimpleNumericSettings();
    }

    /// <summary>供离屏自动化使用：缩小测试规模，避免自动截图任务耗时过长。</summary>
    public void ConfigureRunForAutomation(string? size, string? runtime, string? rampTime)
    {
        if (!string.IsNullOrWhiteSpace(size))
            _config.TrySet("size", size);
        if (!string.IsNullOrWhiteSpace(runtime))
            _config.TrySet("runtime", runtime);
        if (!string.IsNullOrWhiteSpace(rampTime))
            _config.TrySet("ramp_time", rampTime);

        // 同步回简单模式下拉框，保证界面显示与实际运行的参数一致。
        _suppressSimple = true;
        if (_config.Get("size") is { } sizeValue)
            SetComboText(SimpleSizeCombo, SimpleInputParser.FormatSize(sizeValue));
        if (_config.Get("runtime") is { } runtimeValue)
            SetComboText(SimpleDurationCombo, SimpleInputParser.FormatDuration(runtimeValue));
        _suppressSimple = false;

        _editor.Refresh();
        UpdateValidationAndPreview();
    }
}
