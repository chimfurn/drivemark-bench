using System.Globalization;

namespace DriveMark.Tests;

/// <summary>极简断言工具（环境无法联网装 xunit/nunit）。</summary>
internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new AssertionException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message}（期望 {expected}，实际 {actual}）");
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
            throw new AssertionException(message);
    }

    public static void Null(object? value, string message)
    {
        if (value is not null)
            throw new AssertionException($"{message}（实际 {value}）");
    }

    public static void Approx(double expected, double actual, double tolerance, string message)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new AssertionException(
                $"{message}（期望 {expected.ToString(CultureInfo.InvariantCulture)} ± {tolerance}，" +
                $"实际 {actual.ToString(CultureInfo.InvariantCulture)}）");
    }

    public static void Contains(string haystack, string needle, string message)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertionException($"{message}（未找到 \"{needle}\"）");
    }

    public static void NotContains(string haystack, string needle, string message)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertionException($"{message}（不应出现 \"{needle}\"）");
    }

    public static TException Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new AssertionException($"{message}（抛出了 {ex.GetType().Name} 而不是 {typeof(TException).Name}）");
        }
        throw new AssertionException($"{message}（没有抛出异常）");
    }
}

internal sealed class AssertionException(string message) : Exception(message);
