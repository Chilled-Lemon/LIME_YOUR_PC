using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace LIME_YOUR_PC;

public sealed record SystemTweakSnapshot(
    bool? EnhancePointerPrecisionEnabled,
    bool? ToastNotificationsEnabled,
    bool? WindowsAutomaticUpdatesEnabled,
    bool? DefenderRealtimeProtectionEnabled,
    bool? DefenderTamperProtectionEnabled,
    bool? MemoryIntegrityConfiguredEnabled,
    bool? MemoryIntegrityRunning,
    bool? VirtualizationBasedSecurityConfiguredEnabled,
    bool? VirtualizationBasedSecurityRunning,
    bool? MemoryIntegrityLocked,
    bool? VirtualizationBasedSecurityLocked);

public sealed record TweakActionResult(
    bool Success,
    bool? ActualState,
    string Message,
    bool RestartRequired = false);

/// <summary>
/// Small, explicit Windows tweaks that are intentionally kept separate from the
/// power-plan engine. Every tweak supports status readback and post-write verification.
/// Security virtualization changes are configuration changes and require a reboot before
/// their runtime state can be verified.
/// </summary>
public sealed class SystemTweaksEngine
{
    private const string PushNotificationsKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string WindowsUpdateAuKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    private const string DeviceGuardPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";
    private const string DeviceGuardRuntimeKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    private const string HvciRuntimeKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    private const uint SPI_GETMOUSE = 0x0003;
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPIF_UPDATEINIFILE = 0x0001;
    private const uint SPIF_SENDCHANGE = 0x0002;

    public SystemTweakSnapshot GetSnapshot()
    {
        bool? mouse = GetEnhancePointerPrecisionState();
        bool? notifications = GetToastNotificationsState();
        bool? update = GetWindowsAutomaticUpdatesState();
        (bool? realtime, bool? tamper) = GetDefenderState();
        DeviceGuardState deviceGuard = GetDeviceGuardState();

        return new SystemTweakSnapshot(
            mouse,
            notifications,
            update,
            realtime,
            tamper,
            deviceGuard.MemoryIntegrityConfiguredEnabled,
            deviceGuard.MemoryIntegrityRunning,
            deviceGuard.VirtualizationBasedSecurityConfiguredEnabled,
            deviceGuard.VirtualizationBasedSecurityRunning,
            deviceGuard.MemoryIntegrityLocked,
            deviceGuard.VirtualizationBasedSecurityLocked);
    }

    public bool? GetEnhancePointerPrecisionState()
    {
        try
        {
            int[] values = new int[3];
            if (!NativeMethods.SystemParametersInfo(SPI_GETMOUSE, 0, values, 0))
            {
                Log("SPI_GETMOUSE failed: " + Marshal.GetLastWin32Error());
                return null;
            }

            // SPI_GETMOUSE returns threshold1, threshold2 and acceleration level.
            // An acceleration level of 0 means Windows pointer acceleration is disabled.
            return values[2] != 0;
        }
        catch (Exception ex)
        {
            Log("Mouse acceleration read failed: " + ex.Message);
            return null;
        }
    }

    public TweakActionResult SetEnhancePointerPrecision(bool enabled)
    {
        try
        {
            int[] values = new int[3];
            if (!NativeMethods.SystemParametersInfo(SPI_GETMOUSE, 0, values, 0))
                return Fail("无法读取当前鼠标加速设置。");

            // Keep the current thresholds when enabling if they are meaningful. When the
            // current state is fully disabled (0/0/0), use the long-standing Windows
            // defaults 6/10 with acceleration level 1 rather than changing pointer speed.
            int[] desired = enabled
                ? new[] { values[0] > 0 ? values[0] : 6, values[1] > 0 ? values[1] : 10, values[2] > 0 ? values[2] : 1 }
                : new[] { 0, 0, 0 };

            bool ok = NativeMethods.SystemParametersInfo(
                SPI_SETMOUSE,
                0,
                desired,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

            if (!ok)
                return Fail("写入鼠标加速设置失败。");

            bool? actual = GetEnhancePointerPrecisionState();
            bool verified = actual == enabled;
            string message = verified
                ? enabled ? "已开启提高指针精确度 / Windows 鼠标加速。" : "已关闭提高指针精确度 / Windows 鼠标加速。"
                : "鼠标设置已写入，但重新读取后的状态与预期不一致。";

            Log($"Mouse acceleration set={enabled} actual={actual}");
            return new TweakActionResult(verified, actual, message);
        }
        catch (Exception ex)
        {
            Log("Mouse acceleration write failed: " + ex);
            return Fail("修改鼠标加速失败：" + ex.Message);
        }
    }

    public bool? GetToastNotificationsState()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PushNotificationsKey, writable: false);
            object? value = key?.GetValue("ToastEnabled");

            // Missing value follows the normal Windows default: notifications are enabled.
            if (value is null)
                return true;

            return Convert.ToInt32(value) != 0;
        }
        catch (Exception ex)
        {
            Log("Toast notification read failed: " + ex.Message);
            return null;
        }
    }

    public TweakActionResult SetToastNotifications(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(PushNotificationsKey, writable: true)
                ?? throw new InvalidOperationException("无法打开通知设置注册表项。");

            key.SetValue("ToastEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);

            bool? actual = GetToastNotificationsState();
            bool verified = actual == enabled;
            string message = verified
                ? enabled ? "已开启 Windows Toast 通知。" : "已关闭 Windows Toast 通知。"
                : "通知设置已写入，但重新读取后的状态与预期不一致。";

            Log($"Toast notifications set={enabled} actual={actual}");
            return new TweakActionResult(verified, actual, message);
        }
        catch (Exception ex)
        {
            Log("Toast notification write failed: " + ex);
            return Fail("修改 Windows 通知失败：" + ex.Message);
        }
    }

    public bool? GetWindowsAutomaticUpdatesState()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(WindowsUpdateAuKey, writable: false);
            object? value = key?.GetValue("NoAutoUpdate");

            // No policy, or policy value 0, means automatic updates are allowed.
            return value is null || Convert.ToInt32(value) == 0;
        }
        catch (Exception ex)
        {
            Log("Windows Update policy read failed: " + ex.Message);
            return null;
        }
    }

    public TweakActionResult SetWindowsAutomaticUpdates(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(WindowsUpdateAuKey, writable: true)
                ?? throw new InvalidOperationException("无法打开 Windows Update 策略注册表项。");

            // Microsoft documents NoAutoUpdate: 0 = enabled, 1 = disabled.
            key.SetValue("NoAutoUpdate", enabled ? 0 : 1, RegistryValueKind.DWord);

            bool? actual = GetWindowsAutomaticUpdatesState();
            bool verified = actual == enabled;
            string message = verified
                ? enabled
                    ? "已允许 Windows 自动更新。"
                    : "已通过 Windows Update 策略关闭自动更新；仍可手动恢复。"
                : "Windows Update 策略已写入，但重新读取后的状态与预期不一致。";

            Log($"Windows automatic updates set={enabled} actual={actual}");
            return new TweakActionResult(verified, actual, message);
        }
        catch (Exception ex)
        {
            Log("Windows Update policy write failed: " + ex);
            return Fail("修改 Windows 自动更新策略失败：" + ex.Message);
        }
    }

    public (bool? RealtimeProtection, bool? TamperProtection) GetDefenderState()
    {
        const string command = "$s=Get-MpComputerStatus -ErrorAction Stop; Write-Output (($s.RealTimeProtectionEnabled.ToString())+'|'+($s.IsTamperProtected.ToString()))";
        ProcessResult result = RunPowerShell(command, 15000);

        if (result.ExitCode != 0)
        {
            Log("Defender status unavailable: " + result.Output);
            return (null, null);
        }

        string line = result.Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.Contains('|')) ?? string.Empty;

        string[] parts = line.Split('|');
        if (parts.Length != 2 || !bool.TryParse(parts[0], out bool realtime) || !bool.TryParse(parts[1], out bool tamper))
        {
            Log("Defender status parse failed: " + result.Output);
            return (null, null);
        }

        return (realtime, tamper);
    }

    public TweakActionResult SetDefenderRealtimeProtection(bool enabled)
    {
        try
        {
            (bool? current, bool? tamper) = GetDefenderState();

            if (!enabled && tamper == true)
            {
                return new TweakActionResult(
                    false,
                    current,
                    "Microsoft Defender 篡改防护已开启。LIME 不会尝试绕过它；如确有需要，请先在 Windows 安全中心手动关闭篡改防护。 ");
            }

            string disableValue = enabled ? "$false" : "$true";
            ProcessResult result = RunPowerShell(
                $"Set-MpPreference -DisableRealtimeMonitoring {disableValue} -ErrorAction Stop",
                15000);

            if (result.ExitCode != 0)
                return new TweakActionResult(false, current, "Defender 设置失败：" + Compact(result.Output));

            Thread.Sleep(500);
            (bool? actual, bool? actualTamper) = GetDefenderState();
            bool verified = actual == enabled;

            string message = verified
                ? enabled
                    ? "已开启 Microsoft Defender 实时保护。"
                    : "已关闭 Microsoft Defender 实时保护。建议仅在明确需要时暂时关闭。"
                : actualTamper == true
                    ? "设置未生效：Defender 篡改防护可能阻止了修改。"
                    : "Defender 命令已执行，但重新读取后的状态与预期不一致。";

            Log($"Defender realtime set={enabled} actual={actual} tamper={actualTamper}");
            return new TweakActionResult(verified, actual, message);
        }
        catch (Exception ex)
        {
            Log("Defender realtime write failed: " + ex);
            return Fail("修改 Defender 实时保护失败：" + ex.Message);
        }
    }

    public DeviceGuardState GetDeviceGuardState()
    {
        try
        {
            int? hvciPolicy = ReadDword(DeviceGuardPolicyKey, "HypervisorEnforcedCodeIntegrity");
            int? hvciRuntimeConfig = ReadDword(HvciRuntimeKey, "Enabled");
            bool? hvciConfigured = hvciPolicy.HasValue
                ? hvciPolicy.Value != 0
                : hvciRuntimeConfig.HasValue ? hvciRuntimeConfig.Value != 0 : null;

            int? vbsPolicy = ReadDword(DeviceGuardPolicyKey, "EnableVirtualizationBasedSecurity");
            int? vbsRuntimeConfig = ReadDword(DeviceGuardRuntimeKey, "EnableVirtualizationBasedSecurity");
            bool? vbsConfigured = vbsPolicy.HasValue
                ? vbsPolicy.Value != 0
                : vbsRuntimeConfig.HasValue ? vbsRuntimeConfig.Value != 0 : null;

            bool hvciLocked = hvciPolicy == 1 || ReadDword(HvciRuntimeKey, "Locked") == 1;
            bool vbsLocked = ReadDword(DeviceGuardRuntimeKey, "Locked") == 1;

            (bool? hvciRunning, bool? vbsRunning) = GetDeviceGuardRuntimeState();

            // On systems where Windows enabled these features automatically, a registry
            // value may be absent. Runtime state is a useful fallback for the UI.
            hvciConfigured ??= hvciRunning;
            vbsConfigured ??= vbsRunning;

            return new DeviceGuardState(
                hvciConfigured,
                hvciRunning,
                vbsConfigured,
                vbsRunning,
                hvciLocked,
                vbsLocked);
        }
        catch (Exception ex)
        {
            Log("Device Guard state read failed: " + ex);
            return new DeviceGuardState(null, null, null, null, null, null);
        }
    }

    public TweakActionResult SetMemoryIntegrity(bool enabled)
    {
        try
        {
            DeviceGuardState current = GetDeviceGuardState();
            if (!enabled && current.MemoryIntegrityLocked == true)
            {
                return new TweakActionResult(
                    false,
                    current.MemoryIntegrityConfiguredEnabled,
                    "检测到内存完整性 / HVCI 使用 UEFI 锁定。LIME 不会尝试绕过固件级保护，请通过 Windows 官方管理方式处理。",
                    true);
            }

            using RegistryKey policy = Registry.LocalMachine.CreateSubKey(DeviceGuardPolicyKey, writable: true)
                ?? throw new InvalidOperationException("无法打开 Device Guard 策略注册表项。");
            using RegistryKey hvci = Registry.LocalMachine.CreateSubKey(HvciRuntimeKey, writable: true)
                ?? throw new InvalidOperationException("无法打开 HVCI 注册表项。");

            if (enabled)
            {
                // Policy value 2 = enabled without UEFI lock. Enabling HVCI requires VBS.
                policy.SetValue("HypervisorEnforcedCodeIntegrity", 2, RegistryValueKind.DWord);
                hvci.SetValue("Enabled", 1, RegistryValueKind.DWord);
                hvci.SetValue("Locked", 0, RegistryValueKind.DWord);

                policy.SetValue("EnableVirtualizationBasedSecurity", 1, RegistryValueKind.DWord);
                using RegistryKey vbs = Registry.LocalMachine.CreateSubKey(DeviceGuardRuntimeKey, writable: true)
                    ?? throw new InvalidOperationException("无法打开 VBS 注册表项。");
                vbs.SetValue("EnableVirtualizationBasedSecurity", 1, RegistryValueKind.DWord);
            }
            else
            {
                policy.SetValue("HypervisorEnforcedCodeIntegrity", 0, RegistryValueKind.DWord);
                hvci.SetValue("Enabled", 0, RegistryValueKind.DWord);
            }

            DeviceGuardState actual = GetDeviceGuardState();
            bool verified = actual.MemoryIntegrityConfiguredEnabled == enabled;
            string message = verified
                ? enabled
                    ? "已配置开启内存完整性 (HVCI)，并确保 VBS 配置为开启。需要重新启动 Windows 才能验证实际运行状态。"
                    : "已配置关闭内存完整性 (HVCI)。需要重新启动 Windows 才能完全生效。"
                : "HVCI 配置已写入，但重新读取后的配置状态与预期不一致。";

            Log($"HVCI configured={enabled} actualConfig={actual.MemoryIntegrityConfiguredEnabled} running={actual.MemoryIntegrityRunning}");
            return new TweakActionResult(verified, actual.MemoryIntegrityConfiguredEnabled, message, true);
        }
        catch (Exception ex)
        {
            Log("HVCI write failed: " + ex);
            return new TweakActionResult(false, null, "修改内存完整性 (HVCI) 失败：" + ex.Message, true);
        }
    }

    public TweakActionResult SetVirtualizationBasedSecurity(bool enabled)
    {
        try
        {
            DeviceGuardState current = GetDeviceGuardState();
            if (!enabled && current.VirtualizationBasedSecurityLocked == true)
            {
                return new TweakActionResult(
                    false,
                    current.VirtualizationBasedSecurityConfiguredEnabled,
                    "检测到 VBS 使用 UEFI / 固件级锁定。LIME 不会尝试绕过固件保护。",
                    true);
            }

            if (!enabled && current.MemoryIntegrityLocked == true)
            {
                return new TweakActionResult(
                    false,
                    current.VirtualizationBasedSecurityConfiguredEnabled,
                    "内存完整性 (HVCI) 当前使用 UEFI 锁定。由于 HVCI 依赖 VBS，LIME 不会强行关闭底层 VBS。",
                    true);
            }

            using RegistryKey policy = Registry.LocalMachine.CreateSubKey(DeviceGuardPolicyKey, writable: true)
                ?? throw new InvalidOperationException("无法打开 Device Guard 策略注册表项。");
            using RegistryKey vbs = Registry.LocalMachine.CreateSubKey(DeviceGuardRuntimeKey, writable: true)
                ?? throw new InvalidOperationException("无法打开 VBS 注册表项。");

            policy.SetValue("EnableVirtualizationBasedSecurity", enabled ? 1 : 0, RegistryValueKind.DWord);
            vbs.SetValue("EnableVirtualizationBasedSecurity", enabled ? 1 : 0, RegistryValueKind.DWord);

            if (!enabled)
            {
                // HVCI depends on VBS. Keep the configuration internally consistent, but
                // do not touch Credential Guard or other protected services.
                policy.SetValue("HypervisorEnforcedCodeIntegrity", 0, RegistryValueKind.DWord);
                using RegistryKey hvci = Registry.LocalMachine.CreateSubKey(HvciRuntimeKey, writable: true)
                    ?? throw new InvalidOperationException("无法打开 HVCI 注册表项。");
                hvci.SetValue("Enabled", 0, RegistryValueKind.DWord);
            }

            DeviceGuardState actual = GetDeviceGuardState();
            bool verified = actual.VirtualizationBasedSecurityConfiguredEnabled == enabled;
            string message = verified
                ? enabled
                    ? "已配置开启基于虚拟化的安全性 (VBS)。需要重新启动 Windows 才能验证实际运行状态。"
                    : "已配置关闭 VBS，并同时将依赖 VBS 的 HVCI 配置为关闭。需要重新启动 Windows 才能完全生效；Credential Guard 或受管理策略仍可能使 VBS 保持运行。"
                : "VBS 配置已写入，但重新读取后的配置状态与预期不一致。";

            Log($"VBS configured={enabled} actualConfig={actual.VirtualizationBasedSecurityConfiguredEnabled} running={actual.VirtualizationBasedSecurityRunning}");
            return new TweakActionResult(verified, actual.VirtualizationBasedSecurityConfiguredEnabled, message, true);
        }
        catch (Exception ex)
        {
            Log("VBS write failed: " + ex);
            return new TweakActionResult(false, null, "修改 VBS 失败：" + ex.Message, true);
        }
    }

    private (bool? MemoryIntegrityRunning, bool? VbsRunning) GetDeviceGuardRuntimeState()
    {
        const string command =
            "$d=Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\\Microsoft\\Windows\\DeviceGuard -ErrorAction Stop; " +
            "$v=[int]$d.VirtualizationBasedSecurityStatus; " +
            "$h=(@($d.SecurityServicesRunning) -contains 2); " +
            "Write-Output (([string]$v)+'|'+($h.ToString()))";

        ProcessResult result = RunPowerShell(command, 8000);
        if (result.ExitCode != 0)
        {
            Log("Device Guard runtime status unavailable: " + result.Output);
            return (null, null);
        }

        string line = result.Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.Contains('|')) ?? string.Empty;

        string[] parts = line.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int vbsStatus) || !bool.TryParse(parts[1], out bool hvciRunning))
        {
            Log("Device Guard runtime parse failed: " + result.Output);
            return (null, null);
        }

        return (hvciRunning, vbsStatus == 2);
    }

    private static int? ReadDword(string subKey, string valueName)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
        object? value = key?.GetValue(valueName);
        if (value is null)
            return null;

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static TweakActionResult Fail(string message)
        => new(false, null, message);

    private static ProcessResult RunPowerShell(string command, int timeoutMs)
    {
        try
        {
            using var process = new Process();
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
            process.StartInfo = startInfo;

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ProcessResult(-1, "PowerShell 执行超时。");
            }

            string output = string.IsNullOrWhiteSpace(stderr)
                ? stdout.Trim()
                : (stdout + Environment.NewLine + stderr).Trim();

            return new ProcessResult(process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, ex.Message);
        }
    }

    private static string Compact(string text)
    {
        string compact = RegexReplaceWhitespace(text);
        return compact.Length <= 220 ? compact : compact[..220] + "…";
    }

    private static string RegexReplaceWhitespace(string value)
        => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void Log(string text)
    {
        try
        {
            File.AppendAllText(
                PowerPlanEngine.LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SystemTweaks] {text}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Logging should never abort a tweak.
        }
    }

    public sealed record DeviceGuardState(
        bool? MemoryIntegrityConfiguredEnabled,
        bool? MemoryIntegrityRunning,
        bool? VirtualizationBasedSecurityConfiguredEnabled,
        bool? VirtualizationBasedSecurityRunning,
        bool? MemoryIntegrityLocked,
        bool? VirtualizationBasedSecurityLocked);

    private sealed record ProcessResult(int ExitCode, string Output);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SystemParametersInfo(
            uint uiAction,
            uint uiParam,
            [In, Out] int[] pvParam,
            uint fWinIni);
    }
}
