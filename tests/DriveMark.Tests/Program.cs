namespace DriveMark.Tests;

/// <summary>测试入口：零外部依赖的断言运行器（离线环境无法安装 xunit）。</summary>
public static class Program
{
    public static int Main()
    {
        var tests = new List<(string Name, Action Body)>();
        TestsAxis.Register(tests);
        TestsParsers.Register(tests);
        TestsSeries.Register(tests);
        TestsConfig.Register(tests);
        TestsExecution.Register(tests);
        TestsSimpleInput.Register(tests);
        TestsLiveAndLatency.Register(tests);
        TestsSafetyAndPoints.Register(tests);
        TestsRawDevices.Register(tests);
        TestsLogTailer.Register(tests);

        Console.WriteLine($"工作区根目录：{TestData.Root}");
        Console.WriteLine($"共 {tests.Count} 个测试");
        Console.WriteLine(new string('-', 72));

        var passed = 0;
        var failures = new List<string>();

        foreach (var (name, body) in tests)
        {
            try
            {
                body();
                passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                var message = ex is AssertionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                failures.Add($"{name} → {message}");
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"      {message}");
            }
        }

        Console.WriteLine(new string('-', 72));
        Console.WriteLine($"结果：{passed}/{tests.Count} 通过，{failures.Count} 失败");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            foreach (var failure in failures)
                Console.WriteLine("  - " + failure);
            return 1;
        }

        return 0;
    }
}
