using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LIME_YOUR_PC;

public sealed record HardwareInfo(
    string CpuName,
    string Manufacturer,
    int LogicalProcessors,
    int PhysicalCores,
    bool IsLaptop,
    bool IsIntel,
    bool IsAmd,
    bool IsIntelHybrid,
    bool HasEnabledEfficiencyCores,
    int PerformanceCores,
    int EfficiencyCores,
    int PerformanceLogicalProcessors,
    int EfficiencyLogicalProcessors);

internal sealed record CpuSetEntry(ushort Group, byte LogicalProcessorIndex, byte CoreIndex, byte EfficiencyClass);

internal sealed record CpuTopology(
    int PhysicalCores,
    int LogicalProcessors,
    int PerformanceCores,
    int EfficiencyCores,
    int PerformanceLogicalProcessors,
    int EfficiencyLogicalProcessors,
    IReadOnlyList<byte> EfficiencyClasses);

public sealed record EngineProgress(int Percent, string Message);

public sealed record PowerPlanContext(
    string? ActivePlanGuid,
    string ActivePlanName,
    string? LimePlanGuid,
    bool LimePlanExists,
    bool LimePlanIsActive,
    bool LegacyPlanName);

public sealed record OptimizationResult(
    string PlanGuid,
    int SuccessCount,
    int SkippedCount,
    int MismatchCount,
    bool ReusedExistingPlan);

public sealed record OptimizationAssessment(
    string? PlanGuid,
    int SupportedCount,
    int MatchingCount,
    int DifferentCount,
    int UnsupportedCount,
    IReadOnlyList<string> MatchingSettings,
    IReadOnlyList<string> DifferentSettings,
    IReadOnlyList<string> UnsupportedSettings)
{
    public double MatchRatio => SupportedCount == 0 ? 0 : (double)MatchingCount / SupportedCount;
}

internal sealed record PowerCfgResult(int ExitCode, string Output);

public sealed class PowerPlanEngine
{
    public const string AppName = "LIME_YOUR_PC";
    private const string LegacyAppName = "LEMON_YOUR_PC";
    public const string Version = "v0.6.1";
    public static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, AppName + ".log");

    private const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string Usb3LinkPower = "d4e98f31-5ffe-4ce1-be31-1b38b384c009";
    private const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    private const string PerfIncreaseThreshold = "06cadf0e-64ed-448a-8927-ce7bf90eb35d";
    private const string Class1PerfIncreaseThreshold = "06cadf0e-64ed-448a-8927-ce7bf90eb35e";
    private const string PerfCoreParkingMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string Class1PerfCoreParkingMinCores = "0cc5b647-c1df-4637-891a-dec35c318584";
    private const string AllowThrottleStates = "3b04d4fd-1cc7-4f23-ab1c-d1337819c4bb";
    private const string IdleDemoteThreshold = "4b92d758-5a24-4851-a470-815d78aee119";
    private const string IdlePromoteThreshold = "7b224883-b3cc-4d79-819f-8374152cbe7c";
    private const string PerfTimeCheckInterval = "4d2b0152-7d5c-498b-88e2-34345392a2c5";
    private const string MinProcessorState = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string Class1MinProcessorState = "893dee8e-2bef-41e0-89c6-b55d0929964d";
    private const string Class2MinProcessorState = "893dee8e-2bef-41e0-89c6-b55d0929964e";
    private const string HeterogeneousSchedulingPolicy = "93b8b6dc-0698-4d1c-9ee4-0644e900c85d";
    private const string DisplaySubgroup = "7516b95f-f776-4464-8c53-06167f40cc99";
    private const string DisplayTimeout = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";

    public HardwareInfo DetectHardware()
    {
        // Hardware detection deliberately avoids PowerShell/WMI. On some systems CIM/WMI
        // providers can be very slow or unhealthy, which used to make the GUI appear frozen
        // during startup. Everything below is read through the registry or Win32 APIs.
        string cpuName = "Unknown CPU";
        string manufacturer = "Unknown";

        try
        {
            using RegistryKey? cpuKey = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);

            cpuName = (cpuKey?.GetValue("ProcessorNameString") as string)?.Trim() ?? cpuName;
            manufacturer = (cpuKey?.GetValue("VendorIdentifier") as string)?.Trim() ?? manufacturer;
        }
        catch (Exception ex)
        {
            Log("CPU registry detection failed: " + ex.Message);
        }

        bool isIntel = manufacturer.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase)
                       || manufacturer.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                       || cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        bool isAmd = manufacturer.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase)
                     || manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                     || cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);

        CpuTopology topology = GetCpuTopology();
        int logical = topology.LogicalProcessors > 0 ? topology.LogicalProcessors : GetLogicalProcessorCount();
        int physical = topology.PhysicalCores > 0 ? topology.PhysicalCores : GetPhysicalCoreCount();
        bool isLaptop = HasSystemBattery();

        // Windows CPU Sets expose CoreIndex and EfficiencyClass. CoreIndex lets us group
        // SMT threads into physical cores, while EfficiencyClass lets us distinguish the
        // faster and less-efficient class from lower-performance classes. For Intel we
        // present these as P-cores / E-cores only when more than one class is actually
        // visible to Windows. If E-cores are disabled in BIOS, only one class remains.
        bool hybrid = isIntel && topology.EfficiencyClasses.Count > 1
                      && topology.PerformanceCores > 0
                      && topology.EfficiencyCores > 0;

        int pCores = hybrid ? topology.PerformanceCores : 0;
        int eCores = hybrid ? topology.EfficiencyCores : 0;
        int pThreads = hybrid ? topology.PerformanceLogicalProcessors : 0;
        int eThreads = hybrid ? topology.EfficiencyLogicalProcessors : 0;

        Log($"Hardware | CPU={cpuName} vendor={manufacturer} physical={physical} logical={logical} " +
            $"battery={isLaptop} efficiencyClasses=[{string.Join(",", topology.EfficiencyClasses)}] " +
            $"hybrid={hybrid} pCores={pCores} eCores={eCores} pThreads={pThreads} eThreads={eThreads}");

        return new HardwareInfo(
            cpuName,
            manufacturer,
            logical,
            physical,
            isLaptop,
            isIntel,
            isAmd,
            hybrid,
            eCores > 0,
            pCores,
            eCores,
            pThreads,
            eThreads);
    }

    private static int GetLogicalProcessorCount()
    {
        try
        {
            uint count = NativeMethods.GetActiveProcessorCount(NativeMethods.ALL_PROCESSOR_GROUPS);
            if (count > 0 && count <= int.MaxValue)
                return (int)count;
        }
        catch (Exception ex)
        {
            Log("GetActiveProcessorCount failed: " + ex.Message);
        }

        return Environment.ProcessorCount;
    }

    private static int GetPhysicalCoreCount()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            uint length = 0;
            NativeMethods.GetLogicalProcessorInformationEx(
                NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore,
                IntPtr.Zero,
                ref length);

            if (length == 0)
                return 0;

            buffer = Marshal.AllocHGlobal(checked((int)length));
            if (!NativeMethods.GetLogicalProcessorInformationEx(
                    NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore,
                    buffer,
                    ref length))
            {
                Log("GetLogicalProcessorInformationEx failed: " + Marshal.GetLastWin32Error());
                return 0;
            }

            int coreCount = 0;
            int offset = 0;
            while (offset + 8 <= length)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || offset + size > length)
                    break;

                if (relationship == (int)NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                    coreCount++;

                offset += size;
            }

            return coreCount;
        }
        catch (Exception ex)
        {
            Log("Physical-core detection failed: " + ex.Message);
            return 0;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool HasSystemBattery()
    {
        try
        {
            if (!NativeMethods.GetSystemPowerStatus(out NativeMethods.SYSTEM_POWER_STATUS status))
                return false;

            // 128 = no system battery, 255 = unknown.
            return status.BatteryFlag != 128 && status.BatteryFlag != 255;
        }
        catch (Exception ex)
        {
            Log("Battery detection failed: " + ex.Message);
            return false;
        }
    }

    private static CpuTopology GetCpuTopology()
    {
        var entries = new List<CpuSetEntry>();
        IntPtr buffer = IntPtr.Zero;

        try
        {
            uint requiredLength = 0;
            NativeMethods.GetSystemCpuSetInformation(
                IntPtr.Zero,
                0,
                out requiredLength,
                IntPtr.Zero,
                0);

            if (requiredLength == 0)
                return EmptyTopology();

            buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
            if (!NativeMethods.GetSystemCpuSetInformation(
                    buffer,
                    requiredLength,
                    out uint returnedLength,
                    IntPtr.Zero,
                    0))
            {
                Log("GetSystemCpuSetInformation failed: " + Marshal.GetLastWin32Error());
                return EmptyTopology();
            }

            int offset = 0;
            while (offset + 8 <= returnedLength)
            {
                int size = Marshal.ReadInt32(buffer, offset);
                int type = Marshal.ReadInt32(buffer, offset + 4);

                if (size < 8 || offset + size > returnedLength)
                    break;

                // CPU_SET_INFORMATION_TYPE.CpuSetInformation == 0.
                // Layout after Size/Type: Id (DWORD), Group (WORD), then four BYTE
                // indices followed by EfficiencyClass. The documented structure is
                // variable-sized, so advance by each record's Size field.
                if (type == 0 && size >= 20)
                {
                    ushort group = unchecked((ushort)Marshal.ReadInt16(buffer, offset + 12));
                    byte logicalIndex = Marshal.ReadByte(buffer, offset + 14);
                    byte coreIndex = Marshal.ReadByte(buffer, offset + 15);
                    byte efficiencyClass = Marshal.ReadByte(buffer, offset + 18);
                    entries.Add(new CpuSetEntry(group, logicalIndex, coreIndex, efficiencyClass));
                }

                offset += size;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Very old Windows builds may not expose CPU Set APIs.
            return EmptyTopology();
        }
        catch (Exception ex)
        {
            Log("CPU topology detection failed: " + ex.Message);
            return EmptyTopology();
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }

        if (entries.Count == 0)
            return EmptyTopology();

        byte[] classes = entries
            .Select(x => x.EfficiencyClass)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        byte highestClass = classes[^1];

        // CoreIndex is group-relative, so Group must be part of the physical-core key.
        var physicalCores = entries
            .GroupBy(x => (x.Group, x.CoreIndex))
            .Select(g => new
            {
                EfficiencyClass = g.Max(x => x.EfficiencyClass),
                LogicalCount = g.Count()
            })
            .ToArray();

        int performanceCores = physicalCores.Count(x => x.EfficiencyClass == highestClass);
        int efficiencyCores = physicalCores.Length - performanceCores;
        int performanceLogical = entries.Count(x => x.EfficiencyClass == highestClass);
        int efficiencyLogical = entries.Count - performanceLogical;

        return new CpuTopology(
            physicalCores.Length,
            entries.Count,
            performanceCores,
            efficiencyCores,
            performanceLogical,
            efficiencyLogical,
            classes);
    }

    private static CpuTopology EmptyTopology()
        => new(0, 0, 0, 0, 0, 0, Array.Empty<byte>());

    public PowerPlanContext GetPowerPlanContext(bool refreshMetadata = false)
    {
        string? activeGuid = GetActiveSchemeGuid();
        string? limeGuid = FindExistingLimePlan(out bool legacyPlanName);

        if (limeGuid is not null && refreshMetadata)
        {
            if (UpdateLimePlanMetadata(limeGuid))
                legacyPlanName = false;
        }

        string activeName = GetSchemeName(activeGuid) ?? "当前电源计划";
        bool limeIsActive = activeGuid is not null && limeGuid is not null &&
                            activeGuid.Equals(limeGuid, StringComparison.OrdinalIgnoreCase);

        return new PowerPlanContext(
            activeGuid,
            activeName,
            limeGuid,
            limeGuid is not null,
            limeIsActive,
            legacyPlanName);
    }

    public bool RefreshExistingLimePlanMetadata()
    {
        string? limeGuid = FindExistingLimePlan(out _);
        return limeGuid is null || UpdateLimePlanMetadata(limeGuid);
    }

    public OptimizationAssessment AssessCurrentPlan(HardwareInfo hw)
    {
        string? planGuid = GetActiveSchemeGuid();
        if (planGuid is null)
        {
            return new OptimizationAssessment(
                null, 0, 0, 0, 15,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { "无法读取当前活动电源计划" });
        }

        var targets = new List<(string Name, string Group, string Setting, int Expected)>
        {
            ("USB 选择性暂停", UsbSubgroup, UsbSelectiveSuspend, 0),
            ("USB3 Link Power Management", UsbSubgroup, Usb3LinkPower, 0),
            ("处理器性能提高阈值", ProcessorSubgroup, PerfIncreaseThreshold, 1),
            ("Class 1 处理器性能提高阈值", ProcessorSubgroup, Class1PerfIncreaseThreshold, 1),
            ("处理器性能最小核心数量", ProcessorSubgroup, PerfCoreParkingMinCores, 100),
            ("Class 1 处理器性能最小核心数量", ProcessorSubgroup, Class1PerfCoreParkingMinCores, 100),
            ("允许节流状态", ProcessorSubgroup, AllowThrottleStates, 0),
            ("处理器闲置降级阈值", ProcessorSubgroup, IdleDemoteThreshold, hw.IsLaptop ? 80 : 100),
            ("处理器闲置提升阈值", ProcessorSubgroup, IdlePromoteThreshold, hw.IsLaptop ? 85 : 100),
            ("处理器性能时间间隔", ProcessorSubgroup, PerfTimeCheckInterval, 5000),
            ("最小处理器状态", ProcessorSubgroup, MinProcessorState, 100),
            ("Class 1 最小处理器状态", ProcessorSubgroup, Class1MinProcessorState, 100),
            ("Class 2 最小处理器状态", ProcessorSubgroup, Class2MinProcessorState, 100),
            ("异类线程调度策略", ProcessorSubgroup, HeterogeneousSchedulingPolicy, GetSchedulingValue(hw)),
            ("关闭显示器（AC）", DisplaySubgroup, DisplayTimeout, 0)
        };

        var matching = new List<string>();
        var different = new List<string>();
        var unsupported = new List<string>();

        foreach (var target in targets)
        {
            int? actual = GetAcValue(planGuid, target.Group, target.Setting);
            if (actual is null)
                unsupported.Add(target.Name);
            else if (actual.Value == target.Expected)
                matching.Add(target.Name);
            else
                different.Add(target.Name);
        }

        Log($"Assessment | plan={planGuid} supported={matching.Count + different.Count} matching={matching.Count} different={different.Count} unsupported={unsupported.Count}");
        return new OptimizationAssessment(
            planGuid,
            matching.Count + different.Count,
            matching.Count,
            different.Count,
            unsupported.Count,
            matching,
            different,
            unsupported);
    }

    public OptimizationResult Apply(HardwareInfo hw, IProgress<EngineProgress>? progress = null)
    {
        Report(progress, 3, "检查现有 LIME_YOUR_PC 电源计划…");

        string? previousActiveGuid = GetActiveSchemeGuid();
        string previousActiveName = GetSchemeName(previousActiveGuid) ?? "当前电源计划";

        string? existingPlanGuid = FindExistingLimePlan(out bool legacyPlanName);
        bool reused = existingPlanGuid is not null;
        string planGuid;

        if (existingPlanGuid is not null)
        {
            planGuid = existingPlanGuid;

            if (!UpdateLimePlanMetadata(planGuid))
                Report(progress, 7, "[WARNING] 无法刷新 LIME 电源计划的版本说明，但不会影响参数优化。 ");
            else if (legacyPlanName)
                Report(progress, 8, $"检测到旧版 {LegacyAppName} 计划，已迁移并更新为 {AppName} {Version}。 ");
            else
                Report(progress, 8, $"已将既存 LIME 电源计划信息更新为 {Version}。 ");

            Report(progress, 10, $"找到已有计划，直接复用：{planGuid}");
        }
        else
        {
            string? baseGuid = FindHighPerformancePlan();
            if (baseGuid is null)
            {
                baseGuid = GetActiveSchemeGuid();
                if (baseGuid is null)
                    throw new InvalidOperationException("无法取得当前活动电源计划。\n请确认 powercfg 可正常工作。");

                Report(progress, 8, "未找到 Windows 高性能计划，将以当前活动计划作为基础。 ");
            }
            else
            {
                Report(progress, 8, "找到 Windows 高性能计划，使用它作为基础。 ");
            }

            planGuid = DuplicateScheme(baseGuid)
                       ?? throw new InvalidOperationException("创建 LIME_YOUR_PC 电源计划失败。");

            if (!UpdateLimePlanMetadata(planGuid))
                throw new InvalidOperationException("重命名 LIME_YOUR_PC 电源计划失败。");

            Report(progress, 13, $"已创建新计划：{planGuid}");
        }

        if (!RunPowerCfg($"/setactive {planGuid}"))
            throw new InvalidOperationException("无法激活 LIME_YOUR_PC 电源计划。");

        bool switchedPlan = previousActiveGuid is not null &&
                            !previousActiveGuid.Equals(planGuid, StringComparison.OrdinalIgnoreCase);
        if (switchedPlan)
        {
            Report(progress, 14, reused
                ? $"[INFO] 已从“{previousActiveName}”切换至既存的 LIME_YOUR_PC 电源计划。"
                : $"[INFO] 已从“{previousActiveName}”切换至新创建的 LIME_YOUR_PC 电源计划。");
        }

        int success = 0;
        int skipped = 0;
        int step = 0;

        var settings = new List<(string Name, string Group, string Setting, int Value)>
        {
            ("USB 选择性暂停", UsbSubgroup, UsbSelectiveSuspend, 0),
            ("USB3 Link Power Management", UsbSubgroup, Usb3LinkPower, 0),
            ("处理器性能提高阈值", ProcessorSubgroup, PerfIncreaseThreshold, 1),
            ("Class 1 处理器性能提高阈值", ProcessorSubgroup, Class1PerfIncreaseThreshold, 1),
            ("处理器性能最小核心数量", ProcessorSubgroup, PerfCoreParkingMinCores, 100),
            ("Class 1 处理器性能最小核心数量", ProcessorSubgroup, Class1PerfCoreParkingMinCores, 100),
            ("允许节流状态", ProcessorSubgroup, AllowThrottleStates, 0),
            ("处理器闲置降级阈值", ProcessorSubgroup, IdleDemoteThreshold, hw.IsLaptop ? 80 : 100),
            ("处理器闲置提升阈值", ProcessorSubgroup, IdlePromoteThreshold, hw.IsLaptop ? 85 : 100),
            ("处理器性能时间间隔", ProcessorSubgroup, PerfTimeCheckInterval, 5000),
            ("最小处理器状态", ProcessorSubgroup, MinProcessorState, 100),
            ("Class 1 最小处理器状态", ProcessorSubgroup, Class1MinProcessorState, 100),
            ("Class 2 最小处理器状态", ProcessorSubgroup, Class2MinProcessorState, 100),
            ("异类线程调度策略", ProcessorSubgroup, HeterogeneousSchedulingPolicy, GetSchedulingValue(hw))
        };

        foreach (var s in settings)
        {
            bool ok = SetAcValue(planGuid, s.Group, s.Setting, s.Value);
            if (ok)
            {
                success++;
                Report(progress, 18 + (++step * 3), $"[OK] {s.Name} = {s.Value}");
            }
            else
            {
                skipped++;
                Report(progress, 18 + (++step * 3), $"[SKIP] {s.Name}：当前系统不支持或写入失败");
            }
        }

        if (RunPowerCfg("/change monitor-timeout-ac 0"))
        {
            success++;
            Report(progress, 66, "[OK] 关闭显示器（AC）= 从不");
        }
        else
        {
            skipped++;
            Report(progress, 66, "[SKIP] 关闭显示器（AC）写入失败");
        }

        // Re-activate after all writes. Some systems apply /change against the active scheme.
        RunPowerCfg($"/setactive {planGuid}");

        Report(progress, 72, "开始验证写入结果…");
        int mismatch = Verify(planGuid, hw, progress);

        string? active = GetActiveSchemeGuid();
        if (!string.Equals(active, planGuid, StringComparison.OrdinalIgnoreCase))
        {
            mismatch++;
            Report(progress, 96, $"[WARNING] 活动计划不是 LIME_YOUR_PC：{active ?? "未知"}");
        }
        else
        {
            Report(progress, 96, "[PASS] LIME_YOUR_PC 已处于活动状态");
        }

        Report(progress, 100, switchedPlan
            ? (reused
                ? $"完成：已切换至既存的 LIME_YOUR_PC 计划，并刷新至 {Version} 配置。"
                : $"完成：已创建并切换至 LIME_YOUR_PC {Version} 计划。")
            : (reused
                ? $"完成：已复用并刷新现有 LIME_YOUR_PC 计划至 {Version}。"
                : $"完成：已创建并应用 LIME_YOUR_PC {Version} 计划。"));

        Log($"Completed | plan={planGuid} reused={reused} success={success} skipped={skipped} mismatch={mismatch}");
        return new OptimizationResult(planGuid, success, skipped, mismatch, reused);
    }

    public static int GetSchedulingValue(HardwareInfo hw)
    {
        if (hw.IsAmd) return 0;
        if (hw.IsIntel && hw.IsIntelHybrid && hw.HasEnabledEfficiencyCores) return 2;
        return 1;
    }

    private int Verify(string planGuid, HardwareInfo hw, IProgress<EngineProgress>? progress)
    {
        int mismatch = 0;
        var checks = new List<(string Name, string Group, string Setting, int Expected)>
        {
            ("USB 选择性暂停", UsbSubgroup, UsbSelectiveSuspend, 0),
            ("USB3 Link Power", UsbSubgroup, Usb3LinkPower, 0),
            ("性能提高阈值", ProcessorSubgroup, PerfIncreaseThreshold, 1),
            ("Class 1 性能提高阈值", ProcessorSubgroup, Class1PerfIncreaseThreshold, 1),
            ("性能最小核心数", ProcessorSubgroup, PerfCoreParkingMinCores, 100),
            ("Class 1 性能最小核心数", ProcessorSubgroup, Class1PerfCoreParkingMinCores, 100),
            ("允许节流状态", ProcessorSubgroup, AllowThrottleStates, 0),
            ("闲置降级阈值", ProcessorSubgroup, IdleDemoteThreshold, hw.IsLaptop ? 80 : 100),
            ("闲置提升阈值", ProcessorSubgroup, IdlePromoteThreshold, hw.IsLaptop ? 85 : 100),
            ("性能时间间隔", ProcessorSubgroup, PerfTimeCheckInterval, 5000),
            ("最小处理器状态", ProcessorSubgroup, MinProcessorState, 100),
            ("Class 1 最小处理器状态", ProcessorSubgroup, Class1MinProcessorState, 100),
            ("Class 2 最小处理器状态", ProcessorSubgroup, Class2MinProcessorState, 100),
            ("异类线程调度", ProcessorSubgroup, HeterogeneousSchedulingPolicy, GetSchedulingValue(hw))
        };

        int i = 0;
        foreach (var c in checks)
        {
            int? actual = GetAcValue(planGuid, c.Group, c.Setting);
            int percent = 74 + (++i * 20 / checks.Count);

            if (actual is null)
            {
                Report(progress, percent, $"[VERIFY-SKIP] {c.Name}：无法读取");
            }
            else if (actual.Value == c.Expected)
            {
                Report(progress, percent, $"[PASS] {c.Name} = {actual.Value}");
            }
            else
            {
                mismatch++;
                Report(progress, percent, $"[MISMATCH] {c.Name} = {actual.Value}，期望 {c.Expected}");
            }
        }

        return mismatch;
    }

    private bool UpdateLimePlanMetadata(string planGuid)
    {
        string description = $"{AppName} {Version} · 游戏性能电源计划";
        return RunPowerCfg($"/changename {planGuid} \"{AppName}\" \"{description}\"");
    }

    private string? GetSchemeName(string? schemeGuid)
    {
        if (string.IsNullOrWhiteSpace(schemeGuid) || !Guid.TryParse(schemeGuid, out Guid scheme))
            return null;

        // Do not parse the localized text produced by powercfg here. On Chinese Windows,
        // redirected powercfg output can use the console OEM code page while .NET treats
        // Encoding.Default as UTF-8, which turns Chinese plan names into mojibake.
        // PowerReadFriendlyName returns the Unicode friendly name directly from the power API.
        try
        {
            uint bufferSize = 0;
            uint first = NativeMethods.PowerReadFriendlyName(
                IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref bufferSize);

            // ERROR_MORE_DATA (234) is the normal first-call result; some Windows builds
            // may also return ERROR_SUCCESS while only reporting the required size.
            if ((first != 0 && first != 234) || bufferSize < 2)
            {
                Log($"PowerReadFriendlyName(size) failed | result={first} scheme={schemeGuid}");
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
            try
            {
                uint size = bufferSize;
                uint result = NativeMethods.PowerReadFriendlyName(
                    IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
                if (result != 0)
                {
                    Log($"PowerReadFriendlyName(read) failed | result={result} scheme={schemeGuid}");
                    return null;
                }

                string? name = Marshal.PtrToStringUni(buffer);
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Log("PowerReadFriendlyName exception: " + ex.Message);
            return null;
        }
    }

    private string? FindExistingLimePlan(out bool legacyName)
    {
        legacyName = false;
        var r = RunPowerCfgCapture("/list");
        if (r.ExitCode != 0) return null;

        string? active = GetActiveSchemeGuid();
        var limeMatches = new List<string>();
        var legacyMatches = new List<string>();

        foreach (string rawLine in r.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            Match m = Regex.Match(rawLine, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (!m.Success) continue;

            if (rawLine.Contains("(" + AppName + ")", StringComparison.OrdinalIgnoreCase))
                limeMatches.Add(m.Value);
            else if (rawLine.Contains("(" + LegacyAppName + ")", StringComparison.OrdinalIgnoreCase))
                legacyMatches.Add(m.Value);
        }

        if (active is not null && limeMatches.Any(x => x.Equals(active, StringComparison.OrdinalIgnoreCase)))
            return active;
        if (limeMatches.Count > 0)
            return limeMatches[0];

        string? legacy = null;
        if (active is not null && legacyMatches.Any(x => x.Equals(active, StringComparison.OrdinalIgnoreCase)))
            legacy = active;
        else if (legacyMatches.Count > 0)
            legacy = legacyMatches[0];

        if (legacy is not null) legacyName = true;
        return legacy;
    }

    private string? FindHighPerformancePlan()
    {
        var r = RunPowerCfgCapture("/list");
        if (r.ExitCode != 0) return null;

        foreach (Match m in Regex.Matches(r.Output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"))
        {
            if (m.Value.Equals(HighPerformanceGuid, StringComparison.OrdinalIgnoreCase))
                return m.Value;
        }
        return null;
    }

    private string? GetActiveSchemeGuid()
    {
        var r = RunPowerCfgCapture("/getactivescheme");
        Match m = Regex.Match(r.Output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : null;
    }

    private string? DuplicateScheme(string sourceGuid)
    {
        var r = RunPowerCfgCapture("/duplicatescheme " + sourceGuid);
        if (r.ExitCode != 0) return null;

        Match m = Regex.Match(r.Output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : null;
    }

    private bool SetAcValue(string planGuid, string subgroupGuid, string settingGuid, int value)
        => RunPowerCfgCapture($"/setacvalueindex {planGuid} {subgroupGuid} {settingGuid} {value}").ExitCode == 0;

    private int? GetAcValue(string planGuid, string subgroupGuid, string settingGuid)
    {
        if (!Guid.TryParse(planGuid, out Guid scheme) ||
            !Guid.TryParse(subgroupGuid, out Guid subgroup) ||
            !Guid.TryParse(settingGuid, out Guid setting))
        {
            return null;
        }

        try
        {
            uint result = NativeMethods.PowerReadACValueIndex(
                IntPtr.Zero,
                ref scheme,
                ref subgroup,
                ref setting,
                out uint value);

            if (result == 0)
                return unchecked((int)value);

            Log($"PowerReadACValueIndex failed | result={result} setting={settingGuid}");
            return null;
        }
        catch (Exception ex)
        {
            Log("PowerReadACValueIndex exception: " + ex.Message);
            return null;
        }
    }

    private PowerCfgResult RunPowerCfgCapture(string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default
        };

        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        string output = stdout;
        if (!string.IsNullOrWhiteSpace(stderr)) output += Environment.NewLine + stderr;
        Log($"powercfg {arguments} | exit={process.ExitCode}\n{output}");
        return new PowerCfgResult(process.ExitCode, output);
    }

    private bool RunPowerCfg(string arguments)
        => RunPowerCfgCapture(arguments).ExitCode == 0;


    private static void Report(IProgress<EngineProgress>? progress, int percent, string message)
    {
        Log(message);
        progress?.Report(new EngineProgress(percent, message));
    }

    private static void Log(string text)
    {
        try
        {
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Logging should never abort optimization.
        }
    }

    private static class NativeMethods
    {
        internal const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;

        internal enum LOGICAL_PROCESSOR_RELATIONSHIP
        {
            RelationProcessorCore = 0
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        internal static extern uint GetActiveProcessorCount(ushort GroupNumber);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLogicalProcessorInformationEx(
            LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType,
            IntPtr Buffer,
            ref uint ReturnedLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemCpuSetInformation(
            IntPtr Information,
            uint BufferLength,
            out uint ReturnedLength,
            IntPtr Process,
            uint Flags);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS SystemPowerStatus);

        [DllImport("powrprof.dll", SetLastError = false)]
        internal static extern uint PowerReadACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            out uint AcValueIndex);

        [DllImport("powrprof.dll", SetLastError = false)]
        internal static extern uint PowerReadFriendlyName(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);
    }

}
