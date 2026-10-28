using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DriveMark.Core.Charts;

namespace DriveMark.App;

/// <summary>
/// 应用入口。没有 App.xaml，直接创建 Application 与主窗口。
/// 支持 <c>--screenshot</c> 离屏截图模式，用于自动化视觉检查与界面联动验证，例如：
/// <code>
/// DriveMark.App.exe --screenshot out.png --x volume --y iops --xmin 10 --xmax 50 --xstep 5
/// </code>
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest-presets")
            return SelfTestPresets();

        if (args.Length > 0 && args[0] == "--screenshot")
            return Screenshot(args);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow();
        app.Run(window);
        return 0;
    }

    /// <summary>离屏回归自检：验证可编辑下拉框仍能正常选择预设。</summary>
    private static int SelfTestPresets()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var window = new MainWindow
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = -30000,
            };

            window.Show();
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var report = window.RunSimplePresetRegression();
            Console.WriteLine(report);

            window.Close();
            app.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Screenshot(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            var path = options.GetValueOrDefault("screenshot") ?? "fio-gui-screenshot.png";
            var width = ParseInt(options.GetValueOrDefault("width"), 0);
            var height = ParseInt(options.GetValueOrDefault("height"), 0);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            // 离屏模式没有 Application.Run 的消息循环，这里手动安装 Dispatcher 同步上下文，
            // 这样界面里的 await 会回到 UI 线程（否则跨线程更新控件会抛异常）。
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            // 放到屏幕外显示：既能完成完整布局与样式应用，又不打扰用户。
            var window = new MainWindow
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = -30000,
            };

            // 只有显式给了尺寸才覆盖窗口大小；否则使用启动时的自适应尺寸（用于验证真实启动效果）。
            if (width > 0 && height > 0)
            {
                window.MaxWidth = double.PositiveInfinity;
                window.MaxHeight = double.PositiveInfinity;
                window.Width = width;
                window.Height = height;
            }

            if (options.GetValueOrDefault("dataset") is { } dataset)
                window.SelectDataset(dataset);

            // 简单模式的自定义数值（仅用于截图展示，如 --ui-duration 1800）
            window.ApplySimpleSettingsForAutomation(
                options.GetValueOrDefault("ui-block"),
                options.GetValueOrDefault("ui-size"),
                options.GetValueOrDefault("ui-duration"),
                options.GetValueOrDefault("ui-sampling"),
                options.GetValueOrDefault("ui-iodepth"),
                options.GetValueOrDefault("ui-jobs"));

            if (options.GetValueOrDefault("preset") is { } preset)
                window.ApplyPresetByName(preset);

            window.ApplyViewSettings(
                ParseX(options.GetValueOrDefault("x")),
                ParseY(options.GetValueOrDefault("y")),
                ParseDouble(options.GetValueOrDefault("xmin")),
                ParseDouble(options.GetValueOrDefault("xmax")),
                ParseDouble(options.GetValueOrDefault("xstep")),
                ParseDouble(options.GetValueOrDefault("ymin")),
                ParseDouble(options.GetValueOrDefault("ymax")),
                ParseDouble(options.GetValueOrDefault("ystep")));

            if (options.GetValueOrDefault("style") is { } styleText)
            {
                window.ApplyChartStyleForAutomation(
                    styleText.Equals("scatter", StringComparison.OrdinalIgnoreCase)
                        ? ChartRenderStyle.Scatter
                        : ChartRenderStyle.Line);
            }

            if (options.ContainsKey("expand"))
                window.ExpandAdvancedForAutomation();

            window.Show();
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            // 界面语言（--lang en / --lang zh），用于英文截图和自动化验证。
            if (options.GetValueOrDefault("lang") is { } langText)
            {
                window.SetUiLanguageForAutomation(langText);
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            var workArea = SystemParameters.WorkArea;
            Console.WriteLine(
                $"window={window.ActualWidth:F0}x{window.ActualHeight:F0}  " +
                $"workArea={workArea.Width:F0}x{workArea.Height:F0}  " +
                $"dpi={VisualTreeHelper.GetDpi(window).PixelsPerDip:F2}");

            // 非运行类截图选项：目标盘 / 测试项目 / 写入开关 / 结果保存开关。
            if (options.TryGetValue("target", out var targetText))
            {
                var root = targetText.TrimEnd('\\', '/');
                if (root.Length == 2 && root[1] == ':')
                    root += "\\";
                window.SelectTargetForAutomation(root);
            }

            if (options.TryGetValue("profile", out var profileText))
                window.SelectSimpleProfileForAutomation(profileText);

            // 直接指定读写模式（RAW 写入测试自动化用，如 --rw randwrite）。
            if (options.TryGetValue("rw", out var rwText) && !string.IsNullOrWhiteSpace(rwText))
                window.SetRwForAutomation(rwText);

            if (options.ContainsKey("allow-writes") || options.ContainsKey("allow-system-write"))
            {
                window.SetWriteOptionsForAutomation(
                    options.ContainsKey("allow-writes"),
                    options.ContainsKey("allow-system-write"));
            }

            if (options.ContainsKey("no-save-results"))
                window.SetSaveResultsForAutomation(false);

            // 自检：打开结果目录（验证管理员模式下不再报 0xc0000142）。
            if (options.ContainsKey("open-results"))
                Console.WriteLine("openResults=" + window.OpenResultsDirectoryForAutomation());

            // --run：在离屏窗口里真实跑一次测试（只读），用于验证"界面 → 执行器 → 图表"整条链路。
            if (options.ContainsKey("run"))
            {
                var runSize = options.GetValueOrDefault("size");
                var runRuntime = options.GetValueOrDefault("runtime");
                var runRamp = options.GetValueOrDefault("ramp");
                if (runSize is not null || runRuntime is not null || runRamp is not null)
                    window.ConfigureRunForAutomation(runSize, runRuntime, runRamp);

                // 故障模拟：测试中途杀掉 fio，验证"硬盘断开/进程异常"时界面能报错并恢复。
                if (options.TryGetValue("kill-fio-after", out var killText) &&
                    int.TryParse(killText, out var killMilliseconds))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(killMilliseconds);
                        foreach (var process in System.Diagnostics.Process.GetProcessesByName("fio"))
                        {
                            try
                            {
                                process.Kill(entireProcessTree: true);
                            }
                            catch
                            {
                                // 忽略。
                            }
                        }
                    });
                }

                PumpUntilCompleted(window.RunTestForAutomationAsync());

                Console.WriteLine(window.GetLiveDiagnosticsForAutomation());

                // 测试完成后可切换绘图方式/视口再截图（如 --plot-mode after --viewport 30）。
                if (string.Equals(options.GetValueOrDefault("plot-mode"), "after",
                        StringComparison.OrdinalIgnoreCase))
                {
                    window.SetPlotModeForAutomation(true);
                }
            }

            // --raw-run：对原始设备（RAW/整盘）做一次测试并截图；--raw-writes 才允许真实写盘。
            if (options.TryGetValue("raw-run", out var rawDevicePath) &&
                !string.IsNullOrWhiteSpace(rawDevicePath) && rawDevicePath != "1")
            {
                PumpUntilCompleted(window.RunRawTestForAutomationAsync(
                    rawDevicePath,
                    options.ContainsKey("raw-writes")));
            }

            if (string.Equals(options.GetValueOrDefault("plot-mode"), "after",
                    StringComparison.OrdinalIgnoreCase))
            {
                window.SetPlotModeForAutomation(true);
            }

            if (options.TryGetValue("viewport", out var viewportText) &&
                double.TryParse(viewportText, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var viewportSeconds))
            {
                window.SetViewportForAutomation(viewportSeconds);
            }

            if (options.TryGetValue("viewport-offset", out var offsetText) &&
                double.TryParse(offsetText, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var viewportOffset))
            {
                window.SetViewportOffsetForAutomation(viewportOffset);
            }

            if (options.ContainsKey("expand"))
                window.ExpandAdvancedForAutomation();
            window.UpdateLayout();
            if (options.GetValueOrDefault("lang") is { } langBeforeShot)
                window.SetUiLanguageForAutomation(langBeforeShot);
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)content.ActualWidth),
                Math.Max(1, (int)content.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
                encoder.Save(stream);

            window.Close();
            app.Shutdown();

            Console.WriteLine($"OK {Path.GetFullPath(path)} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>在当前 UI 线程上把异步任务跑完（DispatcherFrame 保持消息泵运转，避免死锁）。</summary>
    private static void PumpUntilCompleted(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 从 0 开始：args[0] 本身就是 --screenshot（其值即输出路径）。
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                result[key] = args[++i];
            else
                result[key] = "1";
        }
        return result;
    }

    private static XAxisDimension? ParseX(string? value) => value?.ToLowerInvariant() switch
    {
        "time" or "seconds" => XAxisDimension.ElapsedSeconds,
        "volume" or "bytes" or "mib" => XAxisDimension.TransferredMebibytes,
        _ => null,
    };

    private static YAxisMetric? ParseY(string? value) => value?.ToLowerInvariant() switch
    {
        "iops" => YAxisMetric.Iops,
        "mibps" or "throughput" or "bw" => YAxisMetric.ThroughputMebibytesPerSecond,
        "lat" or "latency" or "延迟" => YAxisMetric.Latency,
        _ => null,
    };

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
