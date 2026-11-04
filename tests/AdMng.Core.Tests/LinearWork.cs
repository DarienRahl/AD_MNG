using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AdMng.Core.Tests;

/// <summary>
/// Complexity checks that do not depend on the machine's speed or load: an input <see cref="Factor"/> times larger may
/// cost at most <see cref="Factor"/> × <see cref="Slack"/> times as much as the smaller one. Linear (or n log n) work stays
/// far below that on any machine; quadratic work costs <see cref="Factor"/>² times as much and fails on any machine. The
/// cost is the CPU time of the calling thread – the work done, not the time spent waiting for a core on a busy runner – and
/// the cheapest of a few alternating runs counts.
/// </summary>
internal static partial class LinearWork
{
    /// <summary>How much larger the large input is than the small one.</summary>
    public const int Factor = 8;

    /// <summary>
    /// Headroom over linear growth: garbage collection of a larger heap, cache effects, n log n sorting. Measured in full
    /// runs of the suite (also under CPU load and on one core) the tests here grew 6.5× to 14× for 8× the input, so the
    /// bound of 32× leaves twice that, and quadratic work (64× and more) still fails by twice the bound.
    /// </summary>
    public const double Slack = 4;

    private const int Rounds = 3;

    /// <summary>CLOCK_THREAD_CPUTIME_ID (Linux).</summary>
    private const int ThreadCpuClock = 3;

    /// <summary>
    /// Runs <paramref name="work"/> on <paramref name="small"/> and on <paramref name="large"/> (built by the caller,
    /// <see cref="Factor"/> times larger, outside the measurement) and asserts linear growth. Returns the result of the
    /// large input for the test's other assertions. <paramref name="work"/> must run synchronously on the calling thread.
    /// </summary>
    public static TResult Assert<TInput, TResult>(TInput small, TInput large, Func<TInput, TResult> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        // JIT compilation and first-use initialization stay out of the measurement.
        work(small);
        var smallCost = long.MaxValue;
        var largeCost = long.MaxValue;
        var result = default(TResult)!;
        for (var round = 0; round < Rounds; round++)
        {
            smallCost = Math.Min(smallCost, Measure(() => work(small)));
            largeCost = Math.Min(largeCost, Measure(() => result = work(large)));
        }

        var ratio = largeCost / (double)Math.Max(1, smallCost);
        Xunit.Assert.True(
            ratio <= Factor * Slack,
            string.Create(CultureInfo.InvariantCulture, $"An input {Factor}× larger cost {ratio:F1}× as much CPU time (at most {Factor * Slack:F0}× is linear)."));
        return result;
    }

    private static long Measure(Action action)
    {
        var start = ThreadWork();
        action();
        return ThreadWork() - start;
    }

    /// <summary>Work done by the current thread so far, in a unit only ratios are taken of: CPU nanoseconds (Linux), CPU
    /// cycles (Windows), elsewhere the elapsed time.</summary>
    private static long ThreadWork()
    {
        if (OperatingSystem.IsLinux() && ClockGetTime(ThreadCpuClock, out var time) == 0)
        {
            return (time.Seconds * 1_000_000_000) + time.Nanoseconds;
        }

        if (OperatingSystem.IsWindows() && QueryThreadCycleTime(GetCurrentThread(), out var cycles))
        {
            return (long)cycles;
        }

        return Stopwatch.GetTimestamp();
    }

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static partial int ClockGetTime(int clock, out Timespec time);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }
}
