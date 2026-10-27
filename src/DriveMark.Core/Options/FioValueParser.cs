using System.Globalization;

namespace DriveMark.Core.Options;

/// <summary>fio 尺寸/时间取值的解析工具（与 fio 的 is_seconds / 尺寸后缀语义一致）。</summary>
public static class FioValueParser
{
    /// <summary>解析 fio 的尺寸值（如 4096、4k、1g、1MiB）为字节数；无法解析返回 null。</summary>
    public static long? ParseSizeBytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        var i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
            i++;
        if (i == 0 || !double.TryParse(text[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;

        var suffix = text[i..].ToLowerInvariant();
        double multiplier = suffix switch
        {
            "" or "b" => 1,
            "k" or "kb" or "kib" => 1024d,
            "m" or "mb" or "mib" => 1024d * 1024,
            "g" or "gb" or "gib" => 1024d * 1024 * 1024,
            "t" or "tb" or "tib" => 1024d * 1024 * 1024 * 1024,
            "p" or "pb" or "pib" => 1024d * 1024 * 1024 * 1024 * 1024,
            _ => 0,
        };
        return multiplier <= 0 ? null : (long)(number * multiplier);
    }

    /// <summary>解析 fio 的时间值（10s、500ms、2m、1h）为 TimeSpan；纯数字按秒处理。</summary>
    public static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        var i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
            i++;
        if (i == 0 || !double.TryParse(text[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;

        var unit = text[i..].ToLowerInvariant();
        return unit switch
        {
            "" or "s" or "sec" => TimeSpan.FromSeconds(number),
            "ms" or "msec" => TimeSpan.FromMilliseconds(number),
            "us" or "usec" => TimeSpan.FromMilliseconds(number / 1000d),
            "m" or "min" => TimeSpan.FromMinutes(number),
            "h" or "hour" => TimeSpan.FromHours(number),
            "d" => TimeSpan.FromDays(number),
            _ => null,
        };
    }

    /// <summary>该读写模式是否包含写操作。</summary>
    public static bool IsWriteMode(string? rw)
    {
        if (string.IsNullOrWhiteSpace(rw))
            return false;
        return rw.Contains("write", StringComparison.OrdinalIgnoreCase) ||
               rw is "rw" or "randrw";
    }
}
