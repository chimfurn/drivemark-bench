using System.Runtime.InteropServices;
using System.Text;
using DriveMark.Core.Charts;

namespace DriveMark.Core.Execution;

/// <summary>一个可直接按"原始设备"访问的测试目标（物理磁盘或 RAW 卷）。</summary>
public sealed record RawDeviceInfo(
    string DevicePath,
    string DisplayName,
    long? SizeBytes,
    bool IsPhysicalDisk,
    bool CanOpenRead,
    bool RequiresElevation)
{
    /// <summary>人类可读描述（目标选择框/命令行用）。</summary>
    public string Describe()
    {
        var kind = IsPhysicalDisk ? "整盘" : "RAW 卷";
        var size = SizeBytes is { } bytes ? "，" + Units.Bytes(bytes) : string.Empty;
        var permission = CanOpenRead ? string.Empty : RequiresElevation ? "，需要管理员权限" : "，当前无法读取";
        return $"{DisplayName}（{kind}{size}{permission}）";
    }
}

/// <summary>
/// 原始设备探测：用于"未格式化（RAW）硬盘"的只读测速。
///
/// 设计原则（与安全策略一致）：
/// - **只读**：程序只会用 fio 以 rw=read 读取设备，绝不写入（RAW 写入会直接覆盖盘上数据）。
/// - 物理磁盘（\\.\PhysicalDriveN）需要管理员权限；卷设备（\\.\Volume{GUID}）能否只读打开取决于系统权限。
/// - 不使用任何格式化/分区/清零 API。
/// </summary>
public static class RawDeviceProbe
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    /// <summary>当前进程是否具备管理员权限。</summary>
    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;

            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
                    return false;

                try
                {
                    var ok = GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(int), out _);
                    return ok && elevated != 0;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>是否是原始设备路径（如 \\.\PhysicalDrive1 / \\.\Volume{...}）。</summary>
    public static bool IsRawDevicePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith(@"\\.\", StringComparison.Ordinal) &&
        path.Length > 4;

    /// <summary>尝试以只读方式打开设备。</summary>
    public static bool CanOpenRead(string devicePath) => TryOpenRead(devicePath, out _);

    /// <summary>读取设备大小（字节）；不支持/无权限时返回 null。</summary>
    public static long? TryGetSize(string devicePath)
    {
        if (!TryOpenRead(devicePath, out var handle))
            return null;

        try
        {
            return DeviceIoControl(handle, IoctlDiskGetLengthInfo,
                IntPtr.Zero, 0, out var length, sizeof(long), out _, IntPtr.Zero)
                ? length
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>枚举可用的原始设备：RAW 卷 + 物理磁盘（物理磁盘通常需要管理员权限才能只读测试）。</summary>
    public static IReadOnlyList<RawDeviceInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var result = new List<RawDeviceInfo>();
        result.AddRange(EnumerateRawVolumes());
        result.AddRange(EnumeratePhysicalDisks());
        return result;
    }

    /// <summary>枚举非文件系统卷（RAW 卷）。</summary>
    public static IReadOnlyList<RawDeviceInfo> EnumerateRawVolumes()
    {
        var result = new List<RawDeviceInfo>();
        if (!OperatingSystem.IsWindows())
            return result;

        var buffer = new StringBuilder(1024);
        var handle = FindFirstVolumeW(buffer, (uint)buffer.Capacity);
        if (handle == new IntPtr(-1))
            return result;

        try
        {
            while (true)
            {
                var volumePath = buffer.ToString(); // \\?\Volume{GUID}\
                var devicePath = ToDevicePath(volumePath);
                if (devicePath is null)
                    break;

                // 能读到文件系统名说明已格式化（不是 RAW），普通文件测试已覆盖，不在这里列出。
                if (!HasRecognizedFileSystem(volumePath))
                {
                    var label = ReadVolumeLabel(volumePath);
                    var size = TryGetSize(devicePath);
                    var canRead = CanOpenRead(devicePath);
                    var name = string.IsNullOrWhiteSpace(label)
                        ? "RAW 卷 " + ShortGuid(volumePath)
                        : $"RAW 卷 {label}";
                    result.Add(new RawDeviceInfo(devicePath, name, size, false, canRead, !canRead));
                }

                if (!FindNextVolumeW(handle, buffer, (uint)buffer.Capacity))
                    break;
            }
        }
        catch
        {
            // 枚举失败时返回已找到的部分。
        }
        finally
        {
            FindVolumeClose(handle);
        }

        return result;
    }

    /// <summary>枚举物理磁盘（0..31）。</summary>
    public static IReadOnlyList<RawDeviceInfo> EnumeratePhysicalDisks()
    {
        var result = new List<RawDeviceInfo>();
        if (!OperatingSystem.IsWindows())
            return result;

        for (var index = 0; index < 32; index++)
        {
            var devicePath = $@"\\.\PhysicalDrive{index}";

            // 0 权限打开只做"存在性探测"（不需要管理员）。
            if (!TryOpen(devicePath, 0, out var probeHandle))
            {
                var error = Marshal.GetLastWin32Error();
                // 2/3 = 设备不存在；其它错误也跳过（后续按需提示）。
                _ = error;
                continue;
            }

            CloseHandle(probeHandle);

            var canRead = CanOpenRead(devicePath);
            var size = canRead ? TryGetSize(devicePath) : null;
            result.Add(new RawDeviceInfo(
                devicePath,
                $"物理磁盘 {index}（整盘）",
                size,
                IsPhysicalDisk: true,
                CanOpenRead: canRead,
                RequiresElevation: !canRead));
        }

        return result;
    }

    /// <summary>按设备路径查找（不要求出现在枚举结果里，例如卷 GUID 路径）。</summary>
    public static RawDeviceInfo? Find(string devicePath)
    {
        if (!IsRawDevicePath(devicePath))
            return null;

        var canRead = CanOpenRead(devicePath);
        var size = canRead ? TryGetSize(devicePath) : null;
        var isPhysical = devicePath.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase);
        return new RawDeviceInfo(
            devicePath,
            isPhysical ? "物理磁盘（指定设备）" : "原始设备（指定卷）",
            size,
            isPhysical,
            canRead,
            RequiresElevation: isPhysical && !canRead);
    }

    // ---------------------------------------------------------------- 内部实现

    private static string? ToDevicePath(string volumePath)
    {
        // \\?\Volume{GUID}\  →  \\.\Volume{GUID}
        var trimmed = volumePath.TrimEnd('\\');
        if (trimmed.Length <= 4 || !trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
            return null;

        return @"\\.\" + trimmed.Substring(4);
    }

    private static string ShortGuid(string volumePath)
    {
        var open = volumePath.IndexOf('{');
        var close = volumePath.IndexOf('}');
        return open >= 0 && close > open + 1
            ? volumePath.Substring(open + 1, Math.Min(8, close - open - 1))
            : "?";
    }

    private static bool HasRecognizedFileSystem(string volumePath)
    {
        var volumeName = new StringBuilder(64);
        var fileSystemName = new StringBuilder(64);
        var ok = GetVolumeInformationW(volumePath, volumeName, (uint)volumeName.Capacity,
            out _, out _, out _, fileSystemName, (uint)fileSystemName.Capacity);
        return ok && fileSystemName.Length > 0;
    }

    private static string? ReadVolumeLabel(string volumePath)
    {
        var volumeName = new StringBuilder(64);
        var fileSystemName = new StringBuilder(64);
        var ok = GetVolumeInformationW(volumePath, volumeName, (uint)volumeName.Capacity,
            out _, out _, out _, fileSystemName, (uint)fileSystemName.Capacity);
        return ok ? volumeName.ToString() : null;
    }

    private static bool TryOpenRead(string path, out IntPtr handle) =>
        TryOpen(path, GenericRead, out handle);

    private static bool TryOpen(string path, uint access, out IntPtr handle)
    {
        handle = CreateFileW(path, access, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return handle != new IntPtr(-1);
    }

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code,
        IntPtr inBuffer, uint inSize, out long outBuffer, uint outSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder volumeName, uint length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool FindNextVolumeW(IntPtr findVolume, StringBuilder volumeName, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindVolumeClose(IntPtr findVolume);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformationW(string rootPathName,
        StringBuilder volumeNameBuffer, uint volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, uint fileSystemNameSize);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass,
        out int tokenInformation, int tokenInformationLength, out int returnLength);
}
