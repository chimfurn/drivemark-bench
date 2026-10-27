using System.IO;
using DriveMark.Core.Execution;
using DriveMark.Core.Options;

namespace DriveMark.Tests;

/// <summary>RAW / 整盘只读测试的安全性与路径校验。</summary>
internal static class TestsRawDevices
{
    public static void Register(List<(string Name, Action Body)> tests)
    {
        tests.Add(("RAW：设备路径识别", () =>
        {
            Assert.True(RawDeviceProbe.IsRawDevicePath(@"\\.\PhysicalDrive1"), "物理磁盘路径应识别为 RAW");
            Assert.True(RawDeviceProbe.IsRawDevicePath(@"\\.\Volume{ebe4f75b-e97f-46d7-a714-9fdedd898c52}"),
                "卷 GUID 路径应识别为 RAW");
            Assert.False(RawDeviceProbe.IsRawDevicePath(TestTargetGuard.SystemDriveRoot), "盘符不应识别为 RAW");
            Assert.False(RawDeviceProbe.IsRawDevicePath("test.dat"), "普通文件名不应识别为 RAW");
            Assert.False(RawDeviceProbe.IsRawDevicePath(null), "null 不应识别为 RAW");
            Assert.False(RawDeviceProbe.IsRawDevicePath(""), "空串不应识别为 RAW");
        }));

        tests.Add(("RAW：设备枚举不抛异常且格式正确", () =>
        {
            var devices = RawDeviceProbe.Enumerate();
            Assert.NotNull(devices, "枚举结果不应为 null");
            foreach (var device in devices)
            {
                Assert.True(RawDeviceProbe.IsRawDevicePath(device.DevicePath),
                    $"枚举出的设备路径应合法：{device.DevicePath}");
                Assert.NotNull(device.DisplayName, "应有显示名");
            }

            // 本机至少有一块物理磁盘（含系统盘）。
            Assert.True(devices.Any(d => d.IsPhysicalDisk), "应能枚举到物理磁盘");
            _ = RawDeviceProbe.IsElevated; // 不应抛异常
        }));

        tests.Add(("RAW：写测试默认被拒绝", () =>
        {
            var config = SimpleTestProfiles.CreateDefaultConfig();
            config.TrySet("rw", "randwrite");

            var request = new FioRunRequest
            {
                Config = config,
                RawDevicePath = @"\\.\PhysicalDrive1",
                RunRootDirectory = Path.Combine(TestData.Root, "runtime", "test-tmp", "raw-reject"),
            };

            var result = new FioRunner().RunAsync(request).GetAwaiter().GetResult();
            Assert.False(result.Succeeded, "RAW 写测试默认必须失败");
            Assert.Contains(result.ErrorMessage!, "RAW 写入默认禁止", "错误信息应说明需要显式允许");
            Assert.Equal(string.Empty, result.RunDirectory, "被拒绝时不应创建运行目录");
        }));

        tests.Add(("RAW：显式允许写入后只受设备可读性限制", () =>
        {
            var config = SimpleTestProfiles.CreateDefaultConfig();
            config.TrySet("rw", "randwrite");

            var request = new FioRunRequest
            {
                Config = config,
                RawDevicePath = @"\\.\PhysicalDrive99",
                AllowRawWrites = true,
                RunRootDirectory = Path.Combine(TestData.Root, "runtime", "test-tmp", "raw-write-allowed"),
            };

            var result = new FioRunner().RunAsync(request).GetAwaiter().GetResult();
            Assert.False(result.Succeeded, "不存在的设备仍应失败");
            Assert.False((result.ErrorMessage ?? string.Empty).Contains("默认禁止"),
                "显式允许后不应再因写模式被拒绝");
            Assert.Contains(result.ErrorMessage!, "RAW 测试无法开始", "应卡在设备可读性检查");
        }));

        tests.Add(("RAW：非法设备路径被拒绝", () =>
        {
            var config = SimpleTestProfiles.CreateDefaultConfig();
            var request = new FioRunRequest
            {
                Config = config,
                RawDevicePath = TestTargetGuard.SystemDriveRoot,
                RunRootDirectory = Path.Combine(TestData.Root, "runtime", "test-tmp", "raw-invalid"),
            };

            var result = new FioRunner().RunAsync(request).GetAwaiter().GetResult();
            Assert.False(result.Succeeded, "非法路径必须失败");
            Assert.Contains(result.ErrorMessage!, "不是合法的原始设备路径", "应提示路径格式");
        }));

        tests.Add(("RAW：不存在的整盘给出明确权限提示", () =>
        {
            var config = SimpleTestProfiles.CreateDefaultConfig();
            var request = new FioRunRequest
            {
                Config = config,
                RawDevicePath = @"\\.\PhysicalDrive99",
                RunRootDirectory = Path.Combine(TestData.Root, "runtime", "test-tmp", "raw-missing"),
            };

            var result = new FioRunner().RunAsync(request).GetAwaiter().GetResult();
            Assert.False(result.Succeeded, "不存在的设备必须失败");
            Assert.Contains(result.ErrorMessage!, "RAW 测试无法开始", "应说明无法开始");
        }));
    }
}
