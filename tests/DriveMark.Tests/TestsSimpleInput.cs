using DriveMark.Core.Options;

namespace DriveMark.Tests;

/// <summary>简单模式"预设值或自定义输入"解析的测试。</summary>
internal static class TestsSimpleInput
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        // ---------------- 文件大小
        tests.Add(("输入解析：文件大小（预设/自定义）", () =>
        {
            Assert.True(SimpleInputParser.TryParseSize("512", out var v1, out var d1), "纯数字应按 MB");
            Assert.Equal("512m", v1, "fio size 值");
            Assert.Equal("512 MB", d1, "显示值");

            Assert.True(SimpleInputParser.TryParseSize("4g", out var v2, out var d2), "4g");
            Assert.Equal("4g", v2, "fio size 值");
            Assert.Equal("4 GB", d2, "显示值");

            Assert.True(SimpleInputParser.TryParseSize("512 MB", out var v3, out _), "带空格单位");
            Assert.Equal("512m", v3, "带空格也应解析");

            Assert.True(SimpleInputParser.TryParseSize("1.5g", out var v4, out _), "小数");
            Assert.Equal("1.5g", v4, "小数保留");

            Assert.False(SimpleInputParser.TryParseSize("abc", out _, out _), "非数字应失败");
            Assert.False(SimpleInputParser.TryParseSize("0", out _, out _), "0 应失败");
            Assert.False(SimpleInputParser.TryParseSize("2000t", out _, out _), "超过 1TB 应失败");
        }));

        // ---------------- 测试时长
        tests.Add(("输入解析：测试时长（1800 秒）", () =>
        {
            Assert.True(SimpleInputParser.TryParseDuration("1800", out var v1, out var d1), "纯数字按秒");
            Assert.Equal("1800s", v1, "fio runtime 值");
            Assert.Equal("1800 秒", d1, "显示值");

            Assert.True(SimpleInputParser.TryParseDuration("30s", out var v2, out _), "30s");
            Assert.Equal("30s", v2, "带单位秒");

            Assert.True(SimpleInputParser.TryParseDuration("30m", out var v3, out _), "30m");
            Assert.Equal("1800s", v3, "分钟应换算为秒");

            Assert.True(SimpleInputParser.TryParseDuration("1h", out var v4, out _), "1h");
            Assert.Equal("3600s", v4, "小时应换算为秒");

            Assert.False(SimpleInputParser.TryParseDuration("0", out _, out _), "0 秒应失败");
            Assert.False(SimpleInputParser.TryParseDuration("25h", out _, out _), "超过 24 小时应失败");
            Assert.False(SimpleInputParser.TryParseDuration("abc", out _, out _), "非法文本应失败");
        }));

        // ---------------- 块大小
        tests.Add(("输入解析：块大小（默认 4KB）", () =>
        {
            Assert.True(SimpleInputParser.TryParseBlockSize("4k", out var v1, out var d1), "4k");
            Assert.Equal("4096", v1, "fio bs 值（字节）");
            Assert.Equal("4 KB", d1, "显示值");

            Assert.True(SimpleInputParser.TryParseBlockSize("4096", out var v2, out _), "纯数字按字节");
            Assert.Equal("4096", v2, "字节值");

            Assert.True(SimpleInputParser.TryParseBlockSize("512", out var v3, out var d3), "512 字节");
            Assert.Equal("512", v3, "512 字节");
            Assert.Equal("512 B", d3, "显示为 512 B");

            Assert.False(SimpleInputParser.TryParseBlockSize("4", out _, out _), "4 字节过小应失败");
            Assert.False(SimpleInputParser.TryParseBlockSize("1000", out _, out _), "非 512 对齐应失败");
            Assert.False(SimpleInputParser.TryParseBlockSize("32m", out _, out _), "超过 16MB 应失败");
        }));

        // ---------------- 采样间隔
        tests.Add(("输入解析：采样间隔", () =>
        {
            Assert.True(SimpleInputParser.TryParseSampling("10", out var ms1, out var d1), "纯数字按毫秒");
            Assert.Equal(10, ms1, "毫秒值");
            Assert.Equal("10 毫秒", d1, "显示值");

            Assert.True(SimpleInputParser.TryParseSampling("1s", out var ms2, out var d2), "1 秒");
            Assert.Equal(1000, ms2, "1000 毫秒");
            Assert.Equal("1 秒", d2, "显示为 1 秒");

            Assert.True(SimpleInputParser.TryParseSampling("10ms", out var ms3, out _), "10ms");
            Assert.Equal(10, ms3, "带单位毫秒");

            Assert.False(SimpleInputParser.TryParseSampling("0", out _, out _), "0ms 应失败");

            Assert.False(SimpleInputParser.TryParseSampling("61s", out _, out _), "超过 60 秒应失败");
            Assert.False(SimpleInputParser.TryParseSampling("abc", out _, out _), "非法文本应失败");
        }));

        // ---------------- 预设值
        tests.Add(("简单模式：新增 4GB 文件大小与 4KB 块大小预设", () =>
        {
            Assert.True(SimpleTestProfiles.SizePresets.Any(p => p.Value == "4g"), "文件大小应含 4GB 预设");
            Assert.True(SimpleTestProfiles.BlockSizePresets.Any(p => p.Value == "4k"), "块大小应含 4KB 预设");
            Assert.Equal("4k", SimpleTestProfiles.DefaultBlockSize, "默认块大小 4KB");

            var config = SimpleTestProfiles.CreateDefaultConfig();
            Assert.Equal("4k", config.Get("bs"), "默认配置块大小应为 4k");
            Assert.Equal("10", config.Get("log_avg_msec"), "默认采样 10ms");
        }));

        // ---------------- 显示格式
        tests.Add(("输入解析：显示格式归一化", () =>
        {
            Assert.Equal("4 KB", SimpleInputParser.FormatBlockSize("4096"), "块大小显示");
            Assert.Equal("4 GB", SimpleInputParser.FormatSize("4g"), "文件大小显示");
            Assert.Equal("1800 秒", SimpleInputParser.FormatDuration("1800s"), "时长显示");
            Assert.Equal("1 秒", SimpleInputParser.FormatSampling(1000), "采样显示（整秒）");
            Assert.Equal("10 毫秒", SimpleInputParser.FormatSampling(10), "采样显示（毫秒）");
        }));
    }
}
