using System.IO;
using DriveMark.Core;

namespace DriveMark.Tests;

/// <summary>定位工作区里的测试数据（不依赖相对路径与当前目录）。</summary>
internal static class TestData
{
    public static string Root { get; } = WorkspaceLocator.FindRoot()
        ?? throw new InvalidOperationException("找不到工作区根目录（缺少 docs/demo_logs 与 FIO 目录）");

    public static string DemoLog(string fileName) =>
        Path.Combine(Root, "docs", "demo_logs", fileName);

    public static string BenchSample(string fileName) =>
        Path.Combine(Root, "docs", "bench_samples", fileName);

    public static bool Has(string path) => File.Exists(path);

    /// <summary>
    /// 在工作区内创建一个临时目录（系统 Temp 可能被沙箱限制，工作区一定可写）。
    /// </summary>
    public static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Root, "runtime", "test-tmp", prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>尽力删除临时目录。</summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结果。
        }
    }
}
