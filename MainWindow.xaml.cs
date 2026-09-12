using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LIME_YOUR_PC;

public partial class MainWindow : Window
{
    private readonly PowerPlanEngine _engine = new();
    private readonly SystemTweaksEngine _tweaks = new();
    private HardwareInfo? _hardware;
    private SystemTweakSnapshot? _tweakSnapshot;
    private bool _running;
    private bool _tweakRunning;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }


    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (!IsInitialized || MaximizeButton is null || WindowFrame is null)
            return;

        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("正在检测硬件", "正在检测", Brushes.Gold);
            AppendLog("正在通过 Windows 原生 API 读取硬件信息…");
            _hardware = await Task.Run(_engine.DetectHardware);
            RenderHardware(_hardware);
            SetStatus("准备就绪", "准备就绪", FindBrush("SuccessBrush"));
            AppendLog($"硬件检测完成：{_hardware.CpuName}");
            AppendLog("程序只会修改 AC 白名单；DC/电池参数不会写入。");
        }
        catch (Exception ex)
        {
            SetStatus("硬件检测失败", "错误", FindBrush("ErrorBrush"));
            AppendLog("[ERROR] " + ex);
        }

        await RefreshSystemTweaksAsync();
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        if (_hardware is null)
        {
            MessageBox.Show("硬件检测尚未完成。", "LIME_YOUR_PC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            "将应用游戏性能电源优化。\n\n" +
            "• 仅修改 AC（接通电源）参数\n" +
            "• DC / 电池参数保持不变\n" +
            "• 不删除原有电源计划\n" +
            "• 已存在 LIME_YOUR_PC 时将直接复用\n\n" +
            "继续吗？",
            "确认应用",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _running = true;
        ApplyButton.IsEnabled = false;
        ApplyButton.Content = "正在应用优化…";
        MainProgress.Value = 0;
        LogText.Text = string.Empty;
        SummaryText.Text = "正在应用…";
        SetStatus("正在应用优化", "正在运行", FindBrush("WarningBrush"));

        var progress = new Progress<EngineProgress>(p =>
        {
            MainProgress.Value = p.Percent;
            AppendLog(p.Message);
        });

        try
        {
            OptimizationResult optimization = await Task.Run(() => _engine.Apply(_hardware, progress));

            MainProgress.Value = 100;
            SummaryText.Text = $"成功 {optimization.SuccessCount} · 跳过 {optimization.SkippedCount} · 验证异常 {optimization.MismatchCount}";

            if (optimization.MismatchCount == 0)
            {
                SetStatus("优化完成", "已完成", FindBrush("SuccessBrush"));
                AppendLog($"[DONE] 当前计划：{optimization.PlanGuid}");
            }
            else
            {
                SetStatus("完成，但存在验证异常", "请检查", FindBrush("WarningBrush"));
                AppendLog("[WARNING] 有参数未达到期望值，请查看日志。");
            }
        }
        catch (Exception ex)
        {
            SetStatus("执行失败", "错误", FindBrush("ErrorBrush"));
            SummaryText.Text = "执行失败";
            AppendLog("[ERROR] " + ex.Message);
            MessageBox.Show(ex.Message, "LIME_YOUR_PC", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            ApplyButton.IsEnabled = true;
            ApplyButton.Content = "▶  重新应用 LIME 优化";
        }
    }

    private void RenderHardware(HardwareInfo hw)
    {
        CpuNameText.Text = hw.CpuName;
        DeviceTypeText.Text = hw.IsLaptop ? "笔记本" : "台式机";
        CoreText.Text = $"{hw.PhysicalCores} / {hw.LogicalProcessors}";
        CpuPlatformText.Text = hw.IsAmd ? "AMD" : hw.IsIntel ? "Intel" : "Unknown";

        CoreTopologyText.Text = hw.IsIntelHybrid
            ? $"P-Core {hw.PerformanceCores}C/{hw.PerformanceLogicalProcessors}T · E-Core {hw.EfficiencyCores}C/{hw.EfficiencyLogicalProcessors}T"
            : "同构处理器";

        int schedule = PowerPlanEngine.GetSchedulingValue(hw);
        ScheduleText.Text = schedule switch
        {
            0 => "全部处理器 (0)",
            2 => "优先高性能处理器 (2)",
            _ => "高性能处理器 (1)"
        };
    }

    private async Task RefreshSystemTweaksAsync()
    {
        try
        {
            _tweakSnapshot = await Task.Run(_tweaks.GetSnapshot);
            RenderSystemTweaks(_tweakSnapshot);
        }
        catch (Exception ex)
        {
            AppendLog("[TWEAK] 状态检测失败：" + ex.Message);
            RenderSystemTweaks(new SystemTweakSnapshot(null, null, null, null, null));
        }
    }

    private void RenderSystemTweaks(SystemTweakSnapshot snapshot)
    {
        RenderTweakRow(MouseTweakStatusText, MouseTweakButton, snapshot.EnhancePointerPrecisionEnabled,
            enabledText: "开启 · 建议游戏用户关闭",
            disabledText: "关闭 ✓",
            enableButtonText: "开启",
            disableButtonText: "关闭");

        RenderTweakRow(NotificationsTweakStatusText, NotificationsTweakButton, snapshot.ToastNotificationsEnabled,
            enabledText: "开启",
            disabledText: "关闭 ✓",
            enableButtonText: "开启",
            disableButtonText: "关闭");

        RenderTweakRow(UpdateTweakStatusText, UpdateTweakButton, snapshot.WindowsAutomaticUpdatesEnabled,
            enabledText: "自动更新允许",
            disabledText: "自动更新已关闭",
            enableButtonText: "恢复",
            disableButtonText: "关闭");

        if (snapshot.DefenderRealtimeProtectionEnabled is null)
        {
            DefenderTweakStatusText.Text = "不可用 / 无法读取";
            DefenderTweakButton.Content = "不可用";
            DefenderTweakButton.IsEnabled = false;
        }
        else if (snapshot.DefenderRealtimeProtectionEnabled == true && snapshot.DefenderTamperProtectionEnabled == true)
        {
            DefenderTweakStatusText.Text = "开启 · 篡改防护开启";
            DefenderTweakButton.Content = "受保护";
            DefenderTweakButton.IsEnabled = false;
        }
        else
        {
            DefenderTweakStatusText.Text = snapshot.DefenderRealtimeProtectionEnabled == true
                ? "开启"
                : snapshot.DefenderTamperProtectionEnabled == true ? "关闭 · 篡改防护开启" : "关闭";
            DefenderTweakButton.Content = snapshot.DefenderRealtimeProtectionEnabled == true ? "关闭" : "开启";
            DefenderTweakButton.IsEnabled = !_tweakRunning;
        }
    }

    private void RenderTweakRow(
        TextBlock statusText,
        Button button,
        bool? state,
        string enabledText,
        string disabledText,
        string enableButtonText,
        string disableButtonText)
    {
        if (state is null)
        {
            statusText.Text = "不可用 / 无法读取";
            button.Content = "不可用";
            button.IsEnabled = false;
            return;
        }

        statusText.Text = state.Value ? enabledText : disabledText;
        button.Content = state.Value ? disableButtonText : enableButtonText;
        button.IsEnabled = !_tweakRunning;
    }

    private async void MouseTweakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tweakSnapshot?.EnhancePointerPrecisionEnabled is not bool current) return;
        await RunTweakAsync(
            () => _tweaks.SetEnhancePointerPrecision(!current),
            "鼠标加速");
    }

    private async void NotificationsTweakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tweakSnapshot?.ToastNotificationsEnabled is not bool current) return;
        await RunTweakAsync(
            () => _tweaks.SetToastNotifications(!current),
            "Windows 通知");
    }

    private async void UpdateTweakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tweakSnapshot?.WindowsAutomaticUpdatesEnabled is not bool current) return;

        if (current)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "关闭 Windows 自动更新会减少系统自动获取安全修复与功能更新。\n\n" +
                "LIME 只写入 Windows Update 的 NoAutoUpdate 策略，不会禁用更新服务；你可以随时在这里恢复。\n\n" +
                "继续关闭自动更新吗？",
                "确认关闭自动更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;
        }

        await RunTweakAsync(
            () => _tweaks.SetWindowsAutomaticUpdates(!current),
            "Windows 自动更新");
    }

    private async void DefenderTweakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tweakSnapshot?.DefenderRealtimeProtectionEnabled is not bool current) return;

        if (current)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "关闭 Microsoft Defender 实时保护会明显降低恶意软件防护能力。\n\n" +
                "如果系统开启了篡改防护，LIME 不会尝试绕过它。\n\n" +
                "仍然继续吗？",
                "确认关闭 Defender 实时保护",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;
        }

        await RunTweakAsync(
            () => _tweaks.SetDefenderRealtimeProtection(!current),
            "Defender 实时保护");
    }

    private async Task RunTweakAsync(Func<TweakActionResult> action, string name)
    {
        if (_tweakRunning) return;

        _tweakRunning = true;
        if (_tweakSnapshot is not null)
            RenderSystemTweaks(_tweakSnapshot);

        try
        {
            AppendLog($"[TWEAK] 正在修改：{name}…");
            TweakActionResult result = await Task.Run(action);
            AppendLog(result.Success ? $"[TWEAK-PASS] {result.Message}" : $"[TWEAK-WARN] {result.Message}");

            if (!result.Success)
            {
                MessageBox.Show(result.Message, "LIME_YOUR_PC", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[TWEAK-ERROR] {name}：{ex.Message}");
            MessageBox.Show(ex.Message, "LIME_YOUR_PC", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _tweakRunning = false;
            await RefreshSystemTweaksAsync();
        }
    }

    private void AppendLog(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(text));
            return;
        }

        string prefix = DateTime.Now.ToString("HH:mm:ss");
        LogText.Text += $"[{prefix}] {text}{Environment.NewLine}";
        LogScroller.ScrollToEnd();
    }

    private void SetStatus(string title, string badge, Brush badgeBrush)
    {
        StatusTitleText.Text = title;
        StatusBadgeText.Text = badge;
        StatusBadgeText.Foreground = badgeBrush;
    }

    private Brush FindBrush(string key)
        => (Brush)FindResource(key);

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(PowerPlanEngine.LogFile))
                File.WriteAllText(PowerPlanEngine.LogFile, "LIME_YOUR_PC log\r\n");

            Process.Start(new ProcessStartInfo
            {
                FileName = PowerPlanEngine.LogFile,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法打开日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
