using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LIME_YOUR_PC;

public sealed record BenchmarkProgress(int Percent, string Message);

public sealed record LatencyBenchmarkResult(
    DateTime Timestamp,
    int ProtocolVersion,
    int DurationSeconds,
    int SampleCount,
    double AverageResponseMs,
    double TypicalResponseMs,
    double OccasionalResponseMs,
    double ExtremeResponseMs,
    double ResponseJitterMs,
    double WorstSpikeMs,
    double SystemCpuUsagePercent,
    int ProcessCount,
    bool HighResolutionTimerUsed);

public sealed record LatencyComparison(
    double ResponseImprovementPercent,
    double JitterImprovementPercent,
    double ResponseDeltaMs,
    double JitterDeltaMs,
    bool ResponseChangeMeaningful,
    bool JitterChangeMeaningful,
    bool EnvironmentComparable,
    string EnvironmentNote);

/// <summary>
/// Lightweight software-side response test.
///
/// This deliberately measures Windows timer wake-up / thread scheduling overshoot rather
/// than claiming to measure mouse-to-photon or total gaming input latency. A dedicated
/// waitable timer is armed repeatedly and the delay beyond the requested wake time is
/// sampled. The result is useful for before/after comparisons on the same machine.
/// </summary>
public sealed class LatencyBenchmarkEngine
{
    public const int BenchmarkProtocolVersion = 1;
    public const int DefaultDurationSeconds = 8;
    private const double IntervalMs = 2.0;
    private const int WarmupSamples = 160;

    public LatencyBenchmarkResult Run(
        int durationSeconds = DefaultDurationSeconds,
        IProgress<BenchmarkProgress>? progress = null)
    {
        if (durationSeconds < 3 || durationSeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "测试时长必须在 3～60 秒之间。");

        int processCount = CountProcesses();
        SystemTimeSnapshot cpuStart = ReadSystemTimes();

        IntPtr timer = NativeMethods.CreateWaitableTimerEx(
            IntPtr.Zero,
            null,
            NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            NativeMethods.TIMER_ALL_ACCESS);

        bool highResolution = timer != IntPtr.Zero;
        if (timer == IntPtr.Zero)
        {
            // High-resolution waitable timers are unavailable on older / stripped builds.
            // Fall back to a normal waitable timer instead of failing the whole benchmark.
            timer = NativeMethods.CreateWaitableTimerEx(
                IntPtr.Zero,
                null,
                0,
                NativeMethods.TIMER_ALL_ACCESS);
        }

        if (timer == IntPtr.Zero)
            throw new InvalidOperationException($"无法创建 Windows 等待计时器，错误代码 {Marshal.GetLastWin32Error()}。");

        var samples = new List<double>(Math.Max(1000, durationSeconds * 500));
        ThreadPriority oldPriority = Thread.CurrentThread.Priority;

        try
        {
            // AboveNormal reduces benchmark self-noise without turning the application into
            // a real-time process. Restore the pool thread priority before returning.
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

            progress?.Report(new BenchmarkProgress(0,
                highResolution ? "正在预热高精度计时器…" : "正在预热兼容计时器…"));

            for (int i = 0; i < WarmupSamples; i++)
                MeasureOne(timer);

            long testStart = Stopwatch.GetTimestamp();
            long durationTicks = (long)(durationSeconds * (double)Stopwatch.Frequency);
            int lastPercent = -1;

            while (true)
            {
                double overshoot = MeasureOne(timer);
                samples.Add(overshoot);

                long elapsedTicks = Stopwatch.GetTimestamp() - testStart;
                int percent = Math.Clamp((int)(elapsedTicks * 100L / durationTicks), 0, 100);
                if (percent >= lastPercent + 4)
                {
                    lastPercent = percent;
                    progress?.Report(new BenchmarkProgress(percent, $"正在采样系统响应… {Math.Min(percent, 100)}%"));
                }

                if (elapsedTicks >= durationTicks)
                    break;
            }

            if (samples.Count < 100)
                throw new InvalidOperationException("有效测试样本不足。");

            samples.Sort();
            double average = samples.Average();
            double typical = Percentile(samples, 0.50);
            double occasional = Percentile(samples, 0.95);
            double extreme = Percentile(samples, 0.99);
            double worst = samples[^1];
            double jitter = StandardDeviation(samples, average);

            SystemTimeSnapshot cpuEnd = ReadSystemTimes();
            double cpuUsage = CalculateCpuUsage(cpuStart, cpuEnd);

            progress?.Report(new BenchmarkProgress(100, "系统响应测试完成。"));

            return new LatencyBenchmarkResult(
                DateTime.Now,
                BenchmarkProtocolVersion,
                durationSeconds,
                samples.Count,
                RoundMetric(average),
                RoundMetric(typical),
                RoundMetric(occasional),
                RoundMetric(extreme),
                RoundMetric(jitter),
                RoundMetric(worst),
                Math.Round(cpuUsage, 1),
                processCount,
                highResolution);
        }
        finally
        {
            Thread.CurrentThread.Priority = oldPriority;
            NativeMethods.CloseHandle(timer);
        }
    }

    public static LatencyComparison Compare(LatencyBenchmarkResult before, LatencyBenchmarkResult after)
    {
        double responseDelta = before.AverageResponseMs - after.AverageResponseMs;
        double jitterDelta = before.ResponseJitterMs - after.ResponseJitterMs;

        double responsePct = ImprovementPercent(before.AverageResponseMs, after.AverageResponseMs);
        double jitterPct = ImprovementPercent(before.ResponseJitterMs, after.ResponseJitterMs);

        // Do not turn normal run-to-run noise into a fake optimization claim.
        bool responseMeaningful = Math.Abs(responseDelta) >= 0.003 && Math.Abs(responsePct) >= 8.0;
        bool jitterMeaningful = Math.Abs(jitterDelta) >= 0.003 && Math.Abs(jitterPct) >= 10.0;

        double cpuDiff = Math.Abs(before.SystemCpuUsagePercent - after.SystemCpuUsagePercent);
        int processDiff = Math.Abs(before.ProcessCount - after.ProcessCount);
        bool sameProtocol = before.ProtocolVersion == after.ProtocolVersion;
        bool timerComparable = before.HighResolutionTimerUsed == after.HighResolutionTimerUsed;
        bool backgroundLoadReasonable = before.SystemCpuUsagePercent <= 35.0 && after.SystemCpuUsagePercent <= 35.0;
        bool environmentComparable = cpuDiff <= 15.0 && processDiff <= 30 && sameProtocol && timerComparable && backgroundLoadReasonable;

        string environmentNote;
        if (!sameProtocol)
            environmentNote = "两次测试使用了不同版本的测试算法";
        else if (!timerComparable)
            environmentNote = "两次测试使用了不同的计时模式";
        else if (!backgroundLoadReasonable)
            environmentNote = "至少一次测试的后台 CPU 占用较高";
        else if (environmentComparable)
            environmentNote = "两次测试环境接近";
        else
            environmentNote = $"测试环境差异较大（CPU 占用差 {cpuDiff:0.#} 个百分点，后台进程差 {processDiff}）";

        return new LatencyComparison(
            Math.Round(responsePct, 1),
            Math.Round(jitterPct, 1),
            RoundMetric(responseDelta),
            RoundMetric(jitterDelta),
            responseMeaningful,
            jitterMeaningful,
            environmentComparable,
            environmentNote);
    }

    private static double MeasureOne(IntPtr timer)
    {
        long start = Stopwatch.GetTimestamp();
        long dueTime100Ns = -(long)(IntervalMs * 10_000.0);

        if (!NativeMethods.SetWaitableTimerEx(
                timer,
                ref dueTime100Ns,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                0))
        {
            throw new InvalidOperationException($"设置 Windows 等待计时器失败，错误代码 {Marshal.GetLastWin32Error()}。");
        }

        uint wait = NativeMethods.WaitForSingleObject(timer, NativeMethods.INFINITE);
        if (wait != NativeMethods.WAIT_OBJECT_0)
            throw new InvalidOperationException($"等待计时器返回异常状态 0x{wait:X8}。");

        long end = Stopwatch.GetTimestamp();
        double elapsedMs = (end - start) * 1000.0 / Stopwatch.Frequency;
        return Math.Max(0.0, elapsedMs - IntervalMs);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static double StandardDeviation(IReadOnlyList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sum = 0;
        foreach (double value in values)
        {
            double d = value - mean;
            sum += d * d;
        }
        return Math.Sqrt(sum / values.Count);
    }

    private static double ImprovementPercent(double before, double after)
    {
        if (before <= 0.000001) return 0;
        return (before - after) / before * 100.0;
    }

    private static double RoundMetric(double value)
        => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static int CountProcesses()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    private static SystemTimeSnapshot ReadSystemTimes()
    {
        if (!NativeMethods.GetSystemTimes(out NativeMethods.FILETIME idle, out NativeMethods.FILETIME kernel, out NativeMethods.FILETIME user))
            return default;

        return new SystemTimeSnapshot(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static double CalculateCpuUsage(SystemTimeSnapshot start, SystemTimeSnapshot end)
    {
        ulong idle = end.Idle >= start.Idle ? end.Idle - start.Idle : 0;
        ulong kernel = end.Kernel >= start.Kernel ? end.Kernel - start.Kernel : 0;
        ulong user = end.User >= start.User ? end.User - start.User : 0;
        ulong total = kernel + user;
        if (total == 0) return 0;

        double busy = 1.0 - (double)idle / total;
        return Math.Clamp(busy * 100.0, 0.0, 100.0);
    }

    private static ulong ToUInt64(NativeMethods.FILETIME value)
        => ((ulong)value.dwHighDateTime << 32) | value.dwLowDateTime;

    private readonly record struct SystemTimeSnapshot(ulong Idle, ulong Kernel, ulong User);

    private static class NativeMethods
    {
        internal const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        internal const uint TIMER_ALL_ACCESS = 0x001F0003;
        internal const uint WAIT_OBJECT_0 = 0x00000000;
        internal const uint INFINITE = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWaitableTimerEx(
            IntPtr lpTimerAttributes,
            string? lpTimerName,
            uint dwFlags,
            uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWaitableTimerEx(
            IntPtr hTimer,
            ref long lpDueTime,
            int lPeriod,
            IntPtr pfnCompletionRoutine,
            IntPtr lpArgToCompletionRoutine,
            IntPtr WakeContext,
            uint TolerableDelay);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);
    }
}

public static class LatencyBenchmarkStore
{
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        PowerPlanEngine.AppName);

    public static readonly string BaselineFile = Path.Combine(FolderPath, "latency_baseline.json");

    public static LatencyBenchmarkResult? LoadBaseline()
    {
        try
        {
            if (!File.Exists(BaselineFile))
                return null;

            string json = File.ReadAllText(BaselineFile);
            return JsonSerializer.Deserialize<LatencyBenchmarkResult>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveBaseline(LatencyBenchmarkResult result)
    {
        Directory.CreateDirectory(FolderPath);
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(BaselineFile, json);
    }
}
