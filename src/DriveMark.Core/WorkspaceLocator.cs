namespace DriveMark.Core;

/// <summary>
/// 定位工作区根目录（包含 docs/demo_logs 或 FIO 目录的那一级）。
/// App 与 CLI 都从 bin 目录向上查找，避免把绝对路径写死。
/// </summary>
public static class WorkspaceLocator
{
    /// <summary>从 <paramref name="startDirectory"/> 向上查找工作区根目录。</summary>
    public static string? FindRoot(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs", "demo_logs")))
                return dir.FullName;
            if (Directory.Exists(Path.Combine(dir.FullName, "FIO")))
                return dir.FullName;
        }
        return null;
    }

    /// <summary>内置合成示例日志目录。</summary>
    public static string DemoLogsDirectory(string? root = null) =>
        Path.Combine(root ?? FindRoot() ?? Directory.GetCurrentDirectory(), "docs", "demo_logs");

    /// <summary>实测样本目录（上个会话采集的真实 fio 输出）。</summary>
    public static string BenchSamplesDirectory(string? root = null) =>
        Path.Combine(root ?? FindRoot() ?? Directory.GetCurrentDirectory(), "docs", "bench_samples");
}
