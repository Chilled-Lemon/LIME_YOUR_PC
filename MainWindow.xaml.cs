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
    private bool _statusRefreshing;

    private bool IsBusy => _running || _tweakRunning;

    public MainWindow()
    {
        InitializeComponent();
        ApplyButton.IsEnabled = false;
        RefreshOptimizationStatusButton.IsEnabled = false;
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
        await RefreshOptimizationStatusAsync(logDetails: true);
        UpdateInteractionState();
    }

    private async void RefreshOptimizationStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _statusRefreshing || _hardware is null) return;
        await RefreshOptimizationStatusAsync(logDetails: true);
    }

    private async Task RefreshOptimizationStatusAsync(bool logDetails = false)
    {
        if (_hardware is null) return;

        _statusRefreshing = true;
        OptimizationStatusBadgeText.Text = "正在检测";
        OptimizationStatusBadgeText.Foreground = FindBrush("WarningBrush");
        OptimizationConclusionText.Text = "正在读取当前活动电源计划与关键参数…";
        OptimizationDetailsText.Text = string.Empty;
        UpdateInteractionState();

        try
        {
            (PowerPlanContext Context, OptimizationAssessment Assessment) state = await Task.Run(() =>
            {
                PowerPlanContext context = _engine.GetPowerPlanContext(refreshMetadata: true);
                OptimizationAssessment assessment = _engine.AssessCurrentPlan(_hardware);
                return (context, assessment);
            });

            RenderOptimizationStatus(state.Context, state.Assessment);
            UpdateApplyButtonText(state.Context);

            if (logDetails)
            {
                AppendLog($"[STATUS] 当前电源计划：{state.Context.ActivePlanName}；" +
                          $"关键参数 {state.Assessment.MatchingCount}/{state.Assessment.SupportedCount} 已符合；" +
                          $"待调整 {state.Assessment.DifferentCount}；不适用/无法读取 {state.Assessment.UnsupportedCount}。");
            }
        }
        catch (Exception ex)
        {
            OptimizationStatusBadgeText.Text = "状态不可用";
            OptimizationStatusBadgeText.Foreground = FindBrush("WarningBrush");
            OptimizationPlanText.Text = "无法读取";
            OptimizationParamsText.Text = "—";
            OptimizationSchedulingText.Text = "—";
            OptimizationSchedulingText.Foreground = FindBrush("MutedTextBrush");
            OptimizationLimePlanText.Text = "—";
            OptimizationConclusionText.Text = "无法完整读取当前电源配置；其他可用功能仍可继续使用。";
            OptimizationDetailsText.Text = ex.Message;
            AppendLog("[STATUS-WARN] 无法刷新优化状态：" + ex.Message);
        }
        finally
        {
            _statusRefreshing = false;
            UpdateInteractionState();
        }
    }

    private void RenderOptimizationStatus(PowerPlanContext context, OptimizationAssessment assessment)
    {
        OptimizationPlanText.Text = context.LimePlanIsActive
            ? "LIME_YOUR_PC（当前使用）"
            : context.ActivePlanName;

        if (assessment.SupportedCount > 0)
        {
            OptimizationParamsText.Text = $"{assessment.MatchingCount}/{assessment.SupportedCount} 已符合";
            if (assessment.UnsupportedCount > 0)
                OptimizationParamsText.Text += $" · {assessment.UnsupportedCount} 不适用";
        }
        else
        {
            OptimizationParamsText.Text = "无法读取";
        }

        string schedulingName = GetSchedulingPolicyDisplayName(_hardware!);
        const string schedulingSettingName = "异类线程调度策略";
        if (assessment.MatchingSettings.Contains(schedulingSettingName))
        {
            OptimizationSchedulingText.Text = schedulingName + " · 已匹配";
            OptimizationSchedulingText.Foreground = FindBrush("SuccessBrush");
        }
        else if (assessment.DifferentSettings.Contains(schedulingSettingName))
        {
            OptimizationSchedulingText.Text = schedulingName + " · 待调整";
            OptimizationSchedulingText.Foreground = FindBrush("WarningBrush");
        }
        else
        {
            OptimizationSchedulingText.Text = schedulingName + " · 此平台不可读取";
            OptimizationSchedulingText.Foreground = FindBrush("MutedTextBrush");
        }

        OptimizationLimePlanText.Text = context.LimePlanIsActive
            ? $"已启用 · {PowerPlanEngine.Version}"
            : context.LimePlanExists
                ? "已存在 · 当前未启用"
                : "尚未创建";

        string conclusion;
        string badge;
        Brush badgeBrush;

        if (assessment.SupportedCount <= 0)
        {
            badge = "读取不完整";
            badgeBrush = FindBrush("WarningBrush");
            conclusion = "无法读取足够的关键电源参数，暂时不能判断当前配置是否符合 LIME 推荐值。";
        }
        else if (context.LimePlanIsActive && assessment.DifferentCount == 0)
        {
            badge = "已高度优化";
            badgeBrush = FindBrush("SuccessBrush");
            conclusion = "当前 LIME_YOUR_PC 已符合可读取的推荐配置。";
        }
        else if (context.LimePlanIsActive)
        {
            badge = "需要刷新";
            badgeBrush = FindBrush("WarningBrush");
            conclusion = $"当前正在使用 LIME_YOUR_PC，但检测到 {assessment.DifferentCount} 项关键参数需要恢复到当前硬件的推荐值。";
        }
        else if (context.LimePlanExists)
        {
            badge = "LIME 未启用";
            badgeBrush = FindBrush("WarningBrush");
            conclusion = "电脑中已经存在 LIME_YOUR_PC，但当前没有使用它。应用优化时会切换至既存计划，并重新写入和验证白名单参数。";
        }
        else if (assessment.DifferentCount == 0)
        {
            badge = "参数已匹配";
            badgeBrush = FindBrush("SuccessBrush");
            conclusion = "当前电源计划的可读取关键参数已经高度符合 LIME 目标；应用后仍会创建独立 LIME 计划，方便单独管理和恢复。";
        }
        else
        {
            badge = "检测到可优化项";
            badgeBrush = FindBrush("WarningBrush");
            conclusion = $"当前计划还有 {assessment.DifferentCount} 项关键参数与 LIME 推荐值不同。应用优化后会创建或切换至独立 LIME 电源计划。";
        }

        OptimizationStatusBadgeText.Text = badge;
        OptimizationStatusBadgeText.Foreground = badgeBrush;
        OptimizationConclusionText.Text = conclusion;

        var detailParts = new List<string>();
        if (assessment.DifferentSettings.Count > 0)
        {
            string items = string.Join("、", assessment.DifferentSettings.Take(4));
            if (assessment.DifferentSettings.Count > 4) items += "…";
            detailParts.Add("待优化：" + items);
        }
        if (assessment.UnsupportedSettings.Count > 0)
        {
            string items = string.Join("、", assessment.UnsupportedSettings.Take(3));
            if (assessment.UnsupportedSettings.Count > 3) items += "…";
            detailParts.Add("不适用 / 无法读取：" + items);
        }
        if (detailParts.Count == 0)
            detailParts.Add("所有可读取的关键电源参数均已符合目标。 ");

        OptimizationDetailsText.Text = string.Join("  ·  ", detailParts);
    }

    private static string GetSchedulingPolicyDisplayName(HardwareInfo hw)
        => PowerPlanEngine.GetSchedulingValue(hw) switch
        {
            0 => "全部处理器",
            2 => "优先高性能处理器",
            _ => "高性能处理器"
        };

    private void UpdateApplyButtonText(PowerPlanContext context)
    {
        ApplyButton.Content = context.LimePlanIsActive
            ? "▶  重新应用 LIME 优化"
            : context.LimePlanExists
                ? "▶  切换并应用 LIME 优化"
                : "▶  应用 LIME 优化";
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;

        if (_hardware is null)
        {
            MessageBox.Show("硬件检测尚未完成。", "LIME_YOUR_PC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PowerPlanContext planContext = new(null, "当前电源计划", null, false, false, false);
        OptimizationAssessment? assessment = null;

        try
        {
            planContext = await Task.Run(() => _engine.GetPowerPlanContext(refreshMetadata: true));
            assessment = await Task.Run(() => _engine.AssessCurrentPlan(_hardware));
        }
        catch (Exception ex)
        {
            AppendLog("[PRECHECK-WARN] 优化前状态检测不完整：" + ex.Message);
        }

        bool alreadyOptimalLime = planContext.LimePlanIsActive &&
                                  assessment is not null &&
                                  assessment.SupportedCount > 0 &&
                                  assessment.DifferentCount == 0;

        MessageBoxResult confirm = MessageBox.Show(
            BuildApplyConfirmMessage(planContext, assessment, alreadyOptimalLime),
            "确认应用",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _running = true;
        MainProgress.Value = 0;
        LogText.Text = string.Empty;
        UpdateInteractionState();

        try
        {
            if (assessment is not null)
                AppendAssessmentLog(assessment);

            ApplyButton.Content = alreadyOptimalLime ? "正在重新验证 LIME 配置…" : "正在应用 LIME 优化…";
            SummaryText.Text = alreadyOptimalLime
                ? "当前配置已符合目标，正在刷新版本信息并重新验证…"
                : planContext.LimePlanExists && !planContext.LimePlanIsActive
                    ? "正在切换至既存 LIME 电源计划并刷新参数…"
                    : "正在应用电源参数…";
            SetStatus(alreadyOptimalLime ? "正在验证现有配置" : "正在应用优化", alreadyOptimalLime ? "验证中" : "正在运行", FindBrush("WarningBrush"));

            var progress = new Progress<EngineProgress>(p =>
            {
                MainProgress.Value = p.Percent;
                AppendLog(p.Message);
            });

            OptimizationResult optimization = await Task.Run(() => _engine.Apply(_hardware, progress));
            MainProgress.Value = 100;
            SummaryText.Text = BuildOptimizationSummary(optimization, assessment, planContext);

            if (optimization.MismatchCount == 0)
            {
                SetStatus("优化配置已验证", "已完成", FindBrush("SuccessBrush"));
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
            UpdateInteractionState();
            await RefreshOptimizationStatusAsync(logDetails: false);
        }
    }

    private static string BuildOptimizationSummary(
        OptimizationResult optimization,
        OptimizationAssessment? assessment,
        PowerPlanContext planContext)
    {
        if (planContext.LimePlanIsActive &&
            assessment is not null &&
            assessment.SupportedCount > 0 &&
            assessment.DifferentCount == 0)
        {
            return $"当前配置已符合 LIME 推荐值 · 已检查 {assessment.SupportedCount} 项 · 不适用 {assessment.UnsupportedCount} 项 · 验证异常 {optimization.MismatchCount}";
        }

        string prefix = !planContext.LimePlanIsActive
            ? (planContext.LimePlanExists ? "已切换既存 LIME · " : "已创建并切换 LIME · ")
            : string.Empty;

        string main = $"{prefix}写入成功 {optimization.SuccessCount} · 不适用/跳过 {optimization.SkippedCount} · 验证异常 {optimization.MismatchCount}";
        if (assessment is not null && assessment.SupportedCount > 0)
            main += $" · 优化前已符合 {assessment.MatchingCount}/{assessment.SupportedCount}";
        return main;
    }

    private static string BuildApplyConfirmMessage(
        PowerPlanContext context,
        OptimizationAssessment? assessment,
        bool alreadyOptimalLime)
    {
        string assessmentText = assessment is not null && assessment.SupportedCount > 0
            ? $"当前可读取的关键参数中，已有 {assessment.MatchingCount}/{assessment.SupportedCount} 项符合 LIME 推荐值。\n\n"
            : string.Empty;

        if (alreadyOptimalLime)
        {
            return
                $"当前已经使用 LIME_YOUR_PC，且可读取的关键电源参数均已符合推荐值。\n\n" +
                $"本次只会将计划说明刷新至 {PowerPlanEngine.Version}，重新写入白名单并验证结果。\n\n" +
                "• 仅修改 / 验证 AC（接通电源）参数\n" +
                "• DC / 电池参数保持不变\n" +
                "• 不删除原有电源计划\n\n继续吗？";
        }

        string planText;
        if (context.LimePlanExists && !context.LimePlanIsActive)
        {
            planText = $"当前使用“{context.ActivePlanName}”，同时检测到既存 LIME_YOUR_PC 计划。\n" +
                       "LIME 会直接切换至既存计划，再按当前硬件刷新和验证白名单参数。";
        }
        else if (!context.LimePlanExists)
        {
            planText = $"当前使用“{context.ActivePlanName}”。LIME 将创建独立的 LIME_YOUR_PC 电源计划，并只修改明确的 AC 白名单参数。";
        }
        else
        {
            planText = "将刷新当前 LIME_YOUR_PC 电源计划的白名单参数并重新验证。";
        }

        return
            $"{planText}\n\n{assessmentText}" +
            "• 仅修改 AC（接通电源）参数\n" +
            "• DC / 电池参数保持不变\n" +
            "• 不删除 Windows 原有电源计划\n" +
            "• 不进行无法稳定复现的内置“延迟跑分”\n\n" +
            "继续吗？";
    }

    private void AppendAssessmentLog(OptimizationAssessment assessment)
    {
        if (assessment.SupportedCount <= 0)
        {
            AppendLog("[ASSESS] 无法读取优化前电源参数。 ");
            return;
        }

        AppendLog($"[ASSESS] 优化前已有 {assessment.MatchingCount}/{assessment.SupportedCount} 项符合 LIME 推荐值；" +
                  $"待调整 {assessment.DifferentCount}；不适用/无法读取 {assessment.UnsupportedCount}。 ");
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
            RenderSystemTweaks(new SystemTweakSnapshot(
                null, null, null, null, null,
                null, null, null, null, null, null));
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
            DefenderTweakButton.IsEnabled = !IsBusy;
        }

        RenderRestartTweakRow(
            MemoryIntegrityTweakStatusText,
            MemoryIntegrityTweakButton,
            snapshot.MemoryIntegrityConfiguredEnabled,
            snapshot.MemoryIntegrityRunning,
            snapshot.MemoryIntegrityLocked,
            "内存完整性");

        RenderRestartTweakRow(
            VbsTweakStatusText,
            VbsTweakButton,
            snapshot.VirtualizationBasedSecurityConfiguredEnabled,
            snapshot.VirtualizationBasedSecurityRunning,
            snapshot.VirtualizationBasedSecurityLocked,
            "VBS");
    }

    private void RenderRestartTweakRow(
        TextBlock statusText,
        Button button,
        bool? configured,
        bool? running,
        bool? locked,
        string name)
    {
        bool? effective = configured ?? running;

        if (effective is null)
        {
            statusText.Text = "不可用 / 无法读取";
            button.Content = "不可用";
            button.IsEnabled = false;
            return;
        }

        if (locked == true)
        {
            statusText.Text = effective.Value
                ? (running == true ? "开启 · UEFI 锁定" : "配置开启 · UEFI 锁定")
                : "关闭 · UEFI 锁定";
            button.Content = "已锁定";
            button.IsEnabled = false;
            return;
        }

        if (running is null)
        {
            statusText.Text = effective.Value
                ? "配置开启 · 重启后确认"
                : "配置关闭 · 重启后确认";
        }
        else if (configured.HasValue && configured.Value != running.Value)
        {
            statusText.Text = configured.Value
                ? $"配置开启 · 当前{(running.Value ? "开启" : "关闭")} · 等待重启"
                : $"配置关闭 · 当前{(running.Value ? "开启" : "关闭")} · 等待重启";
        }
        else
        {
            statusText.Text = running.Value ? "开启 · 当前已生效" : "关闭 · 当前已生效";
        }

        button.Content = effective.Value ? "关闭 · 重启" : "开启 · 重启";
        button.IsEnabled = !IsBusy;
        button.ToolTip = $"修改 {name} 后需要重新启动 Windows 才能验证实际运行状态。";
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
        button.IsEnabled = !IsBusy;
    }

    private async void MouseTweakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tweakSnapshot?.EnhancePointerPrecisionEnabled is not bool current) return;
        await RunTweakAsync(() => _tweaks.SetEnhancePointerPrecision(!current), "鼠标加速");
    }

    private async void NotificationsTweakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tweakSnapshot?.ToastNotificationsEnabled is not bool current) return;
        await RunTweakAsync(() => _tweaks.SetToastNotifications(!current), "Windows 通知");
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

        await RunTweakAsync(() => _tweaks.SetWindowsAutomaticUpdates(!current), "Windows 自动更新");
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

        await RunTweakAsync(() => _tweaks.SetDefenderRealtimeProtection(!current), "Defender 实时保护");
    }

    private async void MemoryIntegrityTweakButton_Click(object sender, RoutedEventArgs e)
    {
        bool? configured = _tweakSnapshot?.MemoryIntegrityConfiguredEnabled;
        bool? running = _tweakSnapshot?.MemoryIntegrityRunning;
        bool? current = configured ?? running;
        if (current is null) return;

        bool target = !current.Value;
        if (!target)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "关闭内存完整性 (HVCI) 会降低 Windows 对内核模式代码的隔离与完整性保护。\n\n" +
                "在部分电脑上可能减少虚拟化安全带来的额外开销，但性能收益并不保证。\n\n" +
                "此修改需要重新启动 Windows 才能完全生效。\n\n" +
                "继续关闭吗？",
                "确认关闭内存完整性",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;
        }

        await RunTweakAsync(() => _tweaks.SetMemoryIntegrity(target), "内存完整性 (HVCI)");
    }

    private async void VbsTweakButton_Click(object sender, RoutedEventArgs e)
    {
        bool? configured = _tweakSnapshot?.VirtualizationBasedSecurityConfiguredEnabled;
        bool? running = _tweakSnapshot?.VirtualizationBasedSecurityRunning;
        bool? current = configured ?? running;
        if (current is null) return;

        bool target = !current.Value;
        if (!target)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "关闭基于虚拟化的安全性 (VBS) 会降低 Windows 的虚拟化隔离保护。\n\n" +
                "由于 HVCI 依赖 VBS，LIME 会同时将 HVCI 配置为关闭；不会绕过 UEFI 锁，也不会主动修改 Credential Guard 的独立配置。\n\n" +
                "此修改需要重新启动 Windows 才能完全生效。\n\n" +
                "继续关闭吗？",
                "确认关闭 VBS",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;
        }

        await RunTweakAsync(() => _tweaks.SetVirtualizationBasedSecurity(target), "基于虚拟化的安全性 (VBS)");
    }

    private async Task RunTweakAsync(Func<TweakActionResult> action, string name)
    {
        if (IsBusy) return;

        _tweakRunning = true;
        UpdateInteractionState();

        try
        {
            AppendLog($"[TWEAK] 正在修改：{name}…");
            TweakActionResult result = await Task.Run(action);
            string logPrefix = result.Success
                ? result.RestartRequired ? "[TWEAK-PENDING]" : "[TWEAK-PASS]"
                : "[TWEAK-WARN]";
            AppendLog($"{logPrefix} {result.Message}");

            if (!result.Success)
            {
                MessageBox.Show(result.Message, "LIME_YOUR_PC", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (result.RestartRequired)
            {
                AppendLog("[RESTART] 此项配置需要重新启动 Windows 才能完全生效。");
                MessageBox.Show(
                    result.Message +
                    "\n\n当前不会自动重启电脑。请在方便时手动重新启动 Windows。重启后再次打开 LIME，即可确认实际运行状态。",
                    "需要重新启动",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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
            UpdateInteractionState();
        }
    }

    private void UpdateInteractionState()
    {
        bool idle = !IsBusy;
        ApplyButton.IsEnabled = idle && _hardware is not null;
        RefreshOptimizationStatusButton.IsEnabled = idle && _hardware is not null && !_statusRefreshing;

        if (_tweakSnapshot is not null)
            RenderSystemTweaks(_tweakSnapshot);
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
