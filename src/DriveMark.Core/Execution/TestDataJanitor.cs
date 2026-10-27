using System.IO;

namespace DriveMark.Core.Execution;

/// <summary>清理结果。</summary>
public sealed record TestDataCleanupReport(
    int RemovedDirectories,
    int RemovedFiles,
    long RemovedBytes,
    IReadOnlyList<string> Errors)
{
    public bool HadWork => RemovedFiles > 0 || RemovedDirectories > 0;

    public string Describe()
    {
        if (!HadWork && Errors.Count == 0)
            return "没有发现需要清理的残留测试数据。";

        var text = $"清理残留测试数据：{RemovedDirectories} 个目录 / {RemovedFiles} 个文件，共释放 " +
                   $"{Charts.Units.Bytes(RemovedBytes)}";
        return Errors.Count == 0 ? text + "。" : text + $"（{Errors.Count} 项失败）";
    }
}

/// <summary>
/// 测试数据清理器。
///
/// 本工具的测试数据只会放在 <c>&lt;目标盘&gt;\DriveMark-test\</c> 下（专用目录，只由本工具使用）。
/// 程序启动时（以及每次运行前）会清空该目录，用于处理上一次异常退出（崩溃/断电）留下的数据，
/// 保证测试盘不会越用越脏。它绝不会触碰该目录之外的任何文件。
/// </summary>
public static class TestDataJanitor
{
    /// <summary>测试数据目录名（位于目标盘根目录下）。</summary>
    public const string DataDirectoryName = "DriveMark-test";

    /// <summary>目标盘上的测试数据根目录，如 <c>&lt;目标盘&gt;:\DriveMark-test</c>。</summary>
    public static string GetDataRoot(string targetRoot) => Path.Combine(targetRoot, DataDirectoryName);

    /// <summary>清空目标盘上的测试数据目录，返回清理统计。只接受盘符根目录（如 X:\）。</summary>
    public static TestDataCleanupReport CleanStale(string targetRoot) =>
        CleanStaleCore(targetRoot, requireDriveRoot: true);

    /// <summary>仅供测试：允许非盘符根目录，便于在临时目录中验证清理逻辑。</summary>
    internal static TestDataCleanupReport CleanStaleForTests(string targetRoot) =>
        CleanStaleCore(targetRoot, requireDriveRoot: false);

    private static TestDataCleanupReport CleanStaleCore(string targetRoot, bool requireDriveRoot)
    {
        var errors = new List<string>();
        var removedDirectories = 0;
        var removedFiles = 0;
        long removedBytes = 0;

        // 安全校验：只允许"盘符根目录"（如 X:\）。
        // 防止把 <某个业务目录>\DriveMark-test 当成垃圾目录误删。
        if (requireDriveRoot)
        {
            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(targetRoot);
            }
            catch (Exception)
            {
                return new TestDataCleanupReport(0, 0, 0, [$"无效路径：{targetRoot}"]);
            }

            var pathRoot = Path.GetPathRoot(fullRoot);
            var trimmedFull = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var trimmedRoot = pathRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmedRoot is null || !string.Equals(trimmedFull, trimmedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new TestDataCleanupReport(0, 0, 0,
                [
                    $"拒绝清理：{targetRoot} 不是盘符根目录。" +
                    "程序只允许清理 <盘符根>\\DriveMark-test，绝不会清空其它目录或整块硬盘。",
                ]);
            }
        }

        var dataRoot = GetDataRoot(targetRoot);
        if (!Directory.Exists(dataRoot))
            return new TestDataCleanupReport(0, 0, 0, errors);

        // 只处理 DriveMark-test 目录内部的内容。
        try
        {
            foreach (var directory in Directory.GetDirectories(dataRoot))
            {
                try
                {
                    foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        removedFiles++;
                        try
                        {
                            removedBytes += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // 忽略单个文件统计失败。
                        }
                    }

                    Directory.Delete(directory, recursive: true);
                    removedDirectories++;
                }
                catch (Exception ex)
                {
                    errors.Add($"删除目录失败 {directory}：{ex.Message}");
                }
            }

            foreach (var file in Directory.GetFiles(dataRoot))
            {
                try
                {
                    removedFiles++;
                    try
                    {
                        removedBytes += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // 忽略。
                    }

                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    errors.Add($"删除文件失败 {file}：{ex.Message}");
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(dataRoot).Any())
                    Directory.Delete(dataRoot);
            }
            catch (Exception ex)
            {
                errors.Add($"删除测试数据根目录失败 {dataRoot}：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"扫描测试数据目录失败 {dataRoot}：{ex.Message}");
        }

        return new TestDataCleanupReport(removedDirectories, removedFiles, removedBytes, errors);
    }
}
