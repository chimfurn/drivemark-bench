using System.IO;

namespace DriveMark.Core.Execution;

/// <summary>定位 fio 可执行文件：优先工作区 FIO\fio.exe，其次 PATH。</summary>
public static class FioExecutableLocator
{
    /// <summary>返回 fio.exe 的完整路径；找不到返回 null。</summary>
    public static string? Locate(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException($"指定的 fio 不存在：{explicitPath}", explicitPath);
            return Path.GetFullPath(explicitPath);
        }

        var root = WorkspaceLocator.FindRoot();
        if (root is not null)
        {
            var candidate = Path.Combine(root, "FIO", "fio.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "fio.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
