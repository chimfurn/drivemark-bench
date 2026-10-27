using System.IO;
using DriveMark.Core.Charts;

namespace DriveMark.Core.Execution;

/// <summary>测试目标磁盘的信息。</summary>
public sealed record TestTargetInfo(
    string RootPath,
    string DriveLetter,
    string VolumeLabel,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    DriveType DriveType = DriveType.Fixed,
    bool IsSystemDrive = false)
{
    public string Describe() =>
        $"{DriveLetter} 盘「{VolumeLabel}」({FileSystem})，可用 {Units.Bytes(FreeBytes)} / 总计 {Units.Bytes(TotalBytes)}";

    /// <summary>界面下拉框里显示的一行文字。</summary>
    public string Display =>
        $"{DriveLetter}: 「{VolumeLabel}」 {Units.Bytes(FreeBytes)} 可用" +
        (IsSystemDrive ? "（系统盘）" : DriveType == DriveType.Removable ? "（可移动）" : string.Empty);

    /// <summary>可移动磁盘标记。</summary>
    public bool IsRemovable => DriveType == DriveType.Removable;
}

/// <summary>
/// 测试目标盘的安全闸门。
///
/// 软件面向普通用户，**允许自由选择本机任意已就绪的本地磁盘 / 可移动磁盘**做测试；
/// 同时保留两条安全底线：
/// 1. 写测试必须显式允许（界面勾选 / 命令行 --writes）；
/// 2. **系统盘禁止写入测试**（只读测试可以），避免误伤操作系统。
/// 测试数据只写入 <c>&lt;目标盘&gt;\DriveMark-test\</c>，结束后由程序删除。
/// </summary>
public static class TestTargetGuard
{
    /// <summary>系统盘根路径（通常是 C:\）。</summary>
    public static string SystemDriveRoot { get; } =
        (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\")
        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>测试数据目录名（位于目标盘根目录下）。</summary>
    public const string DataDirectoryName = TestDataJanitor.DataDirectoryName;

    /// <summary>把任意路径归一化为盘符根（如 "X:\foo\bar" → "X:\"）。</summary>
    public static string NormalizeDriveRoot(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("测试目标不能为空", nameof(targetPath));

        var expanded = Environment.ExpandEnvironmentVariables(targetPath.Trim());
        var full = Path.GetFullPath(expanded);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException($"无法解析盘符：{targetPath}");
        return root;
    }

    /// <summary>
    /// 枚举可作为测试目标的磁盘：已就绪的本地固定盘与可移动磁盘。
    /// 排序：非系统盘优先，其次可用空间从大到小。
    /// </summary>
    public static IReadOnlyList<TestTargetInfo> EnumerateCandidates()
    {
        var result = new List<TestTargetInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;

                var root = drive.RootDirectory.FullName;
                var info = new TestTargetInfo(
                    RootPath: root,
                    DriveLetter: root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).TrimEnd(':'),
                    VolumeLabel: drive.VolumeLabel ?? string.Empty,
                    FileSystem: drive.DriveFormat ?? string.Empty,
                    TotalBytes: drive.TotalSize,
                    FreeBytes: drive.AvailableFreeSpace,
                    DriveType: drive.DriveType,
                    IsSystemDrive: string.Equals(root, SystemDriveRoot, StringComparison.OrdinalIgnoreCase));
                result.Add(info);
            }
            catch
            {
                // 个别驱动器读不到信息时跳过。
            }
        }

        return result
            .OrderBy(info => info.IsSystemDrive ? 1 : 0)
            .ThenByDescending(info => info.FreeBytes)
            .ThenBy(info => info.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>默认测试目标：优先非系统盘中可用空间最大的；若只有系统盘则选系统盘（只读测试）。</summary>
    public static TestTargetInfo? GetDefaultCandidate() => EnumerateCandidates().FirstOrDefault();

    /// <summary>按盘符查找候选磁盘（找不到返回 null）。</summary>
    public static TestTargetInfo? FindCandidate(string targetPath)
    {
        var root = NormalizeDriveRoot(targetPath);
        return EnumerateCandidates()
            .FirstOrDefault(info => string.Equals(info.RootPath, root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 校验目标盘是否可用于测试，并返回磁盘信息。
    /// </summary>
    /// <param name="targetPath">目标盘符或任意其上的路径。</param>
    /// <param name="allowedRoots">可选白名单；为 null 表示允许任意已枚举到的候选磁盘。</param>
    public static TestTargetInfo Inspect(string targetPath, IReadOnlyList<string>? allowedRoots = null)
    {
        var root = NormalizeDriveRoot(targetPath);
        var candidate = FindCandidate(root)
            ?? throw new InvalidOperationException(
                $"目标盘不可用于测试：{root}。仅支持已就绪的本地磁盘或可移动磁盘（不含光驱/网络盘）。");

        if (allowedRoots is not null &&
            !allowedRoots.Any(allowed => string.Equals(NormalizeDriveRoot(allowed), root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"测试目标 {root} 不在允许列表（{string.Join("、", allowedRoots)}）中。");
        }

        return candidate;
    }

    /// <summary>
    /// 写测试前的权限检查（仅在本次运行包含写操作时调用）。
    /// </summary>
    public static void EnsureWritePermission(TestTargetInfo target, bool allowWrites, bool allowSystemDriveWrite)
    {
        if (!allowWrites)
            throw new InvalidOperationException("这次测试包含写操作。请先明确允许写入测试（界面勾选「允许写入测试」/ 命令行 --writes）。");

        if (target.IsSystemDrive && !allowSystemDriveWrite)
            throw new InvalidOperationException(
                $"系统盘（{target.DriveLetter}:）禁止写入测试，以免影响操作系统。请改选其它磁盘，或改用只读测试。");
    }

    /// <summary>检查可用空间是否够本次测试（含安全余量）。</summary>
    public static void EnsureCapacity(
        TestTargetInfo target,
        long requiredBytes,
        double marginFactor = 1.25,
        long absoluteMarginBytes = 128L * 1024 * 1024)
    {
        var required = (long)(requiredBytes * marginFactor) + absoluteMarginBytes;
        if (target.FreeBytes < required)
        {
            throw new InvalidOperationException(
                $"可用空间不足：本次测试需要约 {Units.Bytes(required)}（含安全余量），" +
                $"{target.DriveLetter} 盘仅剩 {Units.Bytes(target.FreeBytes)}。");
        }
    }
}
