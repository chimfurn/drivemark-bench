using System.Globalization;
using System.Text.RegularExpressions;

namespace DriveMark.Core.Options;

/// <summary>
/// 简单模式下拉框的"预设值或自定义输入"解析器。
/// 例如：文件大小可输入 <c>4g</c> / <c>512 MB</c> / <c>512</c>（纯数字默认 MB）；
/// 测试时长可输入 <c>1800</c>（秒）/ <c>30</c> / <c>1h</c>；
/// 采样间隔可输入 <c>10</c> / <c>10ms</c> / <c>0.5s</c>；
/// 块大小可输入 <c>4k</c> / <c>4096</c>（纯数字按字节）。
/// </summary>
public static class SimpleInputParser
{
    // 数字 + 可选单位（单位允许中文）
    private static readonly Regex NumberUnit = new(
        @"^\s*([0-9]+(?:\.[0-9]+)?)\s*([a-zA-Z\u4e00-\u9fff]*)\s*$",
        RegexOptions.Compiled);

    private const long Kb = 1024L;
    private const long Mb = 1024L * 1024L;
    private const long Gb = 1024L * 1024L * 1024L;
    private const long Tb = 1024L * 1024L * 1024L * 1024L;

    /// <summary>解析"文件大小"：返回 fio 的 size 值（如 512m）与界面显示（如 512 MB）。</summary>
    public static bool TryParseSize(string? text, out string fioValue, out string display)
    {
        fioValue = string.Empty;
        display = string.Empty;
        if (!TryParseNumberUnit(text, out var number, out var unit))
            return false;

        var letter = unit.ToLowerInvariant() switch
        {
            "" or "m" or "mb" or "mib" or "兆" => "m",
            "k" or "kb" or "kib" or "千" => "k",
            "g" or "gb" or "gib" => "g",
            "t" or "tb" or "tib" => "t",
            _ => string.Empty,
        };
        if (letter.Length == 0)
            return false;

        var bytes = number * letter switch
        {
            "k" => Kb,
            "m" => Mb,
            "g" => Gb,
            _ => Tb,
        };

        // 合理范围：1 MB ~ 1 TB
        if (bytes < Mb || bytes > Tb)
            return false;

        fioValue = $"{number.ToString("0.###", CultureInfo.InvariantCulture)}{letter}";
        display = $"{number.ToString("0.###", CultureInfo.InvariantCulture)} {letter.ToUpperInvariant()}B";
        return true;
    }

    /// <summary>解析"测试时长"：纯数字按秒；返回 fio 的 runtime 值（如 1800s）与界面显示。</summary>
    public static bool TryParseDuration(string? text, out string fioValue, out string display)
    {
        fioValue = string.Empty;
        display = string.Empty;
        if (!TryParseNumberUnit(text, out var number, out var unit))
            return false;

        var seconds = unit.ToLowerInvariant() switch
        {
            "" or "s" or "sec" or "秒" => number,
            "ms" or "毫秒" => number / 1000d,
            "m" or "min" or "分" or "分钟" => number * 60d,
            "h" or "hour" or "小时" => number * 3600d,
            _ => -1d,
        };

        // 合理范围：1 秒 ~ 24 小时（1800 秒 = 30 分钟，支持）
        if (seconds < 1 || seconds > 86400)
            return false;

        var rounded = Math.Round(seconds, 1);
        fioValue = $"{rounded.ToString("0.###", CultureInfo.InvariantCulture)}s";
        display = $"{rounded.ToString("0.###", CultureInfo.InvariantCulture)} 秒";
        return true;
    }

    /// <summary>解析"块大小"：纯数字按字节；返回 fio 的 bs 值（如 4096）与界面显示（如 4 KB）。</summary>
    public static bool TryParseBlockSize(string? text, out string fioValue, out string display)
    {
        fioValue = string.Empty;
        display = string.Empty;
        if (!TryParseNumberUnit(text, out var number, out var unit))
            return false;

        double bytes = unit.ToLowerInvariant() switch
        {
            "" or "b" or "字节" => number,
            "k" or "kb" or "kib" or "千" => number * Kb,
            "m" or "mb" or "mib" => number * Mb,
            _ => -1d,
        };

        // 合理范围：512 B ~ 16 MB，且按 512 对齐
        if (bytes < 512 || bytes > 16 * Mb)
            return false;
        var rounded = (long)Math.Round(bytes);
        if (rounded % 512 != 0)
            return false;

        fioValue = rounded.ToString(CultureInfo.InvariantCulture);
        display = FormatBlockSizeBytes(rounded);
        return true;
    }

    /// <summary>解析"采样间隔"：纯数字按毫秒；返回毫秒数与界面显示。</summary>
    public static bool TryParseSampling(string? text, out int milliseconds, out string display)
    {
        milliseconds = 0;
        display = string.Empty;
        if (!TryParseNumberUnit(text, out var number, out var unit))
            return false;

        var ms = unit.ToLowerInvariant() switch
        {
            "" or "ms" or "毫秒" => number,
            "s" or "sec" or "秒" => number * 1000d,
            _ => -1d,
        };

        // 合理范围：1ms ~ 60s
        if (ms < 1 || ms > 60000)
            return false;

        milliseconds = (int)Math.Round(ms);
        display = FormatSampling(milliseconds);
        return true;
    }

    /// <summary>解析"队列深度 / 队列数"这类正整数（例如 1~1024）。</summary>
    public static bool TryParseCount(string? text, int minimum, int maximum, out string fioValue, out string display)
    {
        fioValue = string.Empty;
        display = string.Empty;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return false;

        if (value < minimum || value > maximum)
            return false;

        fioValue = value.ToString(CultureInfo.InvariantCulture);
        display = fioValue;
        return true;
    }

    // ---------------------------------------------------------------- 显示格式

    /// <summary>把 fio size 值格式化为界面显示（512m → 512 MB）。</summary>
    public static string FormatSize(string fioValue) =>
        TryParseSize(fioValue, out _, out var display) ? display : fioValue;

    /// <summary>把 fio runtime 值格式化为界面显示（1800s → 1800 秒）。</summary>
    public static string FormatDuration(string fioValue) =>
        TryParseDuration(fioValue, out _, out var display) ? display : fioValue;

    /// <summary>把 fio bs 值格式化为界面显示（4096 → 4 KB；512 → 512 B）。</summary>
    public static string FormatBlockSize(string fioValue)
    {
        if (!TryParseNumberUnit(fioValue, out var number, out var unit))
            return fioValue;

        double bytes = unit.ToLowerInvariant() switch
        {
            "" or "b" or "字节" => number,
            "k" or "kb" or "kib" or "千" => number * Kb,
            "m" or "mb" or "mib" => number * Mb,
            _ => -1d,
        };
        return bytes < 0 ? fioValue : FormatBlockSizeBytes((long)Math.Round(bytes));
    }

    private static string FormatBlockSizeBytes(long bytes) => bytes switch
    {
        < Kb => $"{bytes} B",
        < Mb => $"{(bytes / (double)Kb).ToString("0.###", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / (double)Mb).ToString("0.###", CultureInfo.InvariantCulture)} MB",
    };

    /// <summary>把毫秒数格式化为界面显示（10 → 10 毫秒；1000 → 1 秒）。</summary>
    public static string FormatSampling(int milliseconds) =>
        milliseconds % 1000 == 0
            ? $"{milliseconds / 1000} 秒"
            : $"{milliseconds} 毫秒";

    // ---------------------------------------------------------------- 内部

    private static bool TryParseNumberUnit(string? text, out double number, out string unit)
    {
        number = 0;
        unit = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = NumberUnit.Match(text.Replace(" ", string.Empty).Replace("\u3000", string.Empty));
        if (!match.Success)
            return false;

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return false;

        unit = match.Groups[2].Value;
        return true;
    }
}
