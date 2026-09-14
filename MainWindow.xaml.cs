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
    private readonly LatencyBenchmarkEngine _benchmark = new();

    private HardwareInfo? _hardware;
    private SystemTweakSnapshot? _tweakSnapshot;
    private LatencyBenchmarkResult? _lastBenchmark;
    private LatencyBenchmarkResult? _savedBaseline;
    private string? _lastManualBenchmarkPlanGuid;
    private bool _lastBenchmarkWasManual;

    private bool _running;
    private bool _tweakRunning;
    private bool _benchmarkRunning;

    private bool IsBusy => _running || _tweakRunning || _benchmarkRunning;

    public MainWindow()
    {
        InitializeComponent();
        ApplyButton.IsEnabled = false;
        RunBenchmarkButton.IsEnabled = false;
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
        LoadBenchmarkBaseline();

        try
        {
            SetStatus("正在检测硬件", "正在检测", Brushes.Gold);
            AppendLog("正在通过 Windows 原生 API 读取硬件信息…");
            _hardware = await Task.Run(_engine.DetectHardware);
            RenderHardware(_hardware);
            SetStatus("准备就绪", "准备就绪", FindBrush("SuccessBrush"));
            AppendLog($"硬件检测完成：{_hardware.CpuName}");
            AppendLog("程序只会修改 AC 白名单；DC/电池参数不会写入。");

            try
            {
                PowerPlanContext powerContext = await Task.Run(() => _engine.GetPowerPlanContext(refreshMetadata: true));
                if (powerContext.LimePlanExists)
                {
                    if (powerContext.LimePlanIsActive)
                    {
                        ApplyButton.Content = "▶  重新应用 LIME 优化";
                        AppendLog($"[POWER] 当前正在使用 LIME_YOUR_PC，计划说明已同步至 {PowerPlanEngine.Version}。");
                    }
                    else
                    {
                        ApplyButton.Content = "▶  切换并应用 LIME 优化";
                        AppendLog($"[POWER] 检测到既存 LIME_YOUR_PC 计划；当前使用“{powerContext.ActivePlanName}”。");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("[POWER-WARN] 无法刷新电源计划说明：" + ex.Message);
            }
        }
        catch (Exception ex)
        {
            SetStatus("硬件检测失败", "错误", FindBrush("ErrorBrush"));
            AppendLog("[ERROR] " + ex);
        }

        await RefreshSystemTweaksAsync();
        UpdateInteractionState();
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
        bool reuseManualBenchmark = !alreadyOptimalLime && CanReuseLastManualBenchmark(planContext);

        MessageBoxResult confirm = MessageBox.Show(
            BuildApplyConfirmMessage(planContext, alreadyOptimalLime, reuseManualBenchmark),
            "确认应用",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _running = true;
        MainProgress.Value = 0;
        LogText.Text = string.Empty;
        UpdateInteractionState();

        LatencyBenchmarkResult? before = null;
        LatencyBenchmarkResult? after = null;
        OptimizationResult? optimization = null;

        try
        {
            if (assessment is not null)
                AppendAssessmentLog(assessment);

            if (alreadyOptimalLime)
            {
                ApplyButton.Content = "正在验证 LIME 配置…";
                SummaryText.Text = "当前 LIME 配置已符合目标，正在刷新版本信息并验证…";
                SetStatus("正在验证现有配置", "验证中", FindBrush("WarningBrush"));
                AppendLog("[BENCH] 当前已使用 LIME_YOUR_PC，且支持的关键参数均符合目标；跳过自动 A/B，避免重复测试制造无意义波动。");
            }
            else if (reuseManualBenchmark && _lastBenchmark is not null)
            {
                before = _lastBenchmark;
                _lastBenchmarkWasManual = false;
                ApplyButton.Content = "正在应用 LIME 优化…";
                SummaryText.Text = "已复用刚才的测试作为优化前基准…";
                SetStatus("已复用当前测试", "准备优化", FindBrush("WarningBrush"));
                RenderBenchmarkResult(before, "已复用刚才的“当前状态”测试作为优化前基准，不再重复测试。 ");
                AppendLog("[BENCH] 已复用刚才的基础测试作为优化前基准，不重复执行优化前测试。 ");
            }
            else
            {
                ApplyButton.Content = "正在进行优化前测试…";
                SummaryText.Text = planContext.LimePlanExists && !planContext.LimePlanIsActive
                    ? $"正在测量“{planContext.ActivePlanName}”作为切换前基准…"
                    : "正在建立优化前基准…";
                SetStatus("正在建立优化前基准", "测试中", FindBrush("WarningBrush"));

                before = await RunBenchmarkCoreAsync("优化前基准", renderAsCurrent: true);
                _lastBenchmarkWasManual = false;
                if (before is null)
                    AppendLog("[BENCH-WARN] 优化前测试失败，将继续执行电源优化。 ");
            }

            ApplyButton.Content = "正在应用 LIME 优化…";
            SummaryText.Text = planContext.LimePlanExists && !planContext.LimePlanIsActive
                ? "正在切换至既存 LIME 电源计划并刷新参数…"
                : "正在应用电源参数…";
            SetStatus("正在应用优化", "正在运行", FindBrush("WarningBrush"));

            var progress = new Progress<EngineProgress>(p =>
            {
                MainProgress.Value = p.Percent;
                AppendLog(p.Message);
            });

            optimization = await Task.Run(() => _engine.Apply(_hardware, progress));
            MainProgress.Value = 100;

            string scenarioNote = BuildPowerPlanScenarioNote(planContext);

            if (!alreadyOptimalLime)
            {
                // Give power-policy notifications a moment to settle before the second run.
                await Task.Delay(900);

                ApplyButton.Content = "正在进行优化后测试…";
                SummaryText.Text = "优化完成，正在测量优化后响应…";
                SetStatus("正在进行优化后测试", "测试中", FindBrush("WarningBrush"));

                after = await RunBenchmarkCoreAsync("优化后测试", renderAsCurrent: true);
                _lastBenchmarkWasManual = false;

                if (before is not null && after is not null)
                {
                    LatencyComparison comparison = LatencyBenchmarkEngine.Compare(before, after);
                    RenderBenchmarkComparison(before, after, comparison, assessment, scenarioNote);
                    AppendComparisonLog(before, after, comparison, assessment, scenarioNote);
                }
                else if (after is not null)
                {
                    RenderBenchmarkResult(after, $"{scenarioNote}。优化后测试完成；由于切换前基准不可用，无法计算 A/B 变化。 ");
                }
            }
            else
            {
                BenchmarkBadgeText.Text = "无需重复测试";
                BenchmarkBadgeText.Foreground = FindBrush("SuccessBrush");
                BenchmarkConclusionText.Text =
                    "当前已经使用 LIME_YOUR_PC，且支持的关键电源参数均已符合推荐值。本次仅刷新版本说明并重新验证，因此自动 A/B 已跳过；你仍可随时手动测试当前状态。";
                BenchmarkEnvironmentText.Text = "避免把正常测试波动误判为“优化提升”";
            }

            SummaryText.Text = BuildOptimizationSummary(optimization, assessment, planContext);

            if (optimization.MismatchCount == 0)
            {
                SetStatus(alreadyOptimalLime ? "当前配置已符合目标" : "优化与测试完成", "已完成", FindBrush("SuccessBrush"));
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
            ApplyButton.Content = "▶  重新应用 LIME 优化";
            UpdateInteractionState();
        }
    }

    private async void RunBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;

        LatencyBenchmarkResult? result = await RunBenchmarkCoreAsync("基础测试", renderAsCurrent: true);
        if (result is null) return;

        _lastBenchmark = result;
        _lastBenchmarkWasManual = true;
        try
        {
            _lastManualBenchmarkPlanGuid = _engine.GetPowerPlanContext().ActivePlanGuid;
        }
        catch
        {
            _lastManualBenchmarkPlanGuid = null;
        }

        if (_savedBaseline is not null && !ReferenceEquals(_savedBaseline, result))
        {
            LatencyComparison comparison = LatencyBenchmarkEngine.Compare(_savedBaseline, result);
            RenderBenchmarkComparison(_savedBaseline, result, comparison, assessment: null);
            AppendComparisonLog(_savedBaseline, result, comparison, assessment: null);
        }
        else
        {
            RenderBenchmarkResult(
                result,
                "当前状态测试完成。若现在执行一键优化，LIME 会直接复用本次结果作为优化前基准，不重复测试；只有跨重启或手动调整时才需要保存对比基准。 ");
        }

        UpdateInteractionState();
    }

    private async Task<LatencyBenchmarkResult?> RunBenchmarkCoreAsync(
        string phase,
        bool renderAsCurrent)
    {
        if (_benchmarkRunning) return null;

        _benchmarkRunning = true;
        BenchmarkProgressBar.Value = 0;
        BenchmarkBadgeText.Text = "测试中";
        BenchmarkBadgeText.Foreground = FindBrush("WarningBrush");
        BenchmarkConclusionText.Text = $"{phase}：正在采样 Windows 定时唤醒与线程调度响应…";
        UpdateInteractionState();

        var progress = new Progress<BenchmarkProgress>(p =>
        {
            BenchmarkProgressBar.Value = p.Percent;
            BenchmarkEnvironmentText.Text = p.Message;
        });

        try
        {
            AppendLog($"[BENCH] 开始 {phase}（{LatencyBenchmarkEngine.DefaultDurationSeconds} 秒）…");
            LatencyBenchmarkResult result = await Task.Run(() =>
                _benchmark.Run(LatencyBenchmarkEngine.DefaultDurationSeconds, progress));

            _lastBenchmark = result;
            BenchmarkProgressBar.Value = 100;
            BenchmarkBadgeText.Text = "测试完成";
            BenchmarkBadgeText.Foreground = FindBrush("SuccessBrush");

            if (renderAsCurrent)
                RenderBenchmarkResult(result, $"{phase}完成。 ");

            AppendBenchmarkLog(phase, result);
            return result;
        }
        catch (Exception ex)
        {
            BenchmarkBadgeText.Text = "测试失败";
            BenchmarkBadgeText.Foreground = FindBrush("ErrorBrush");
            BenchmarkConclusionText.Text = "系统响应测试失败；这不会阻止其他 LIME 功能使用。";
            BenchmarkEnvironmentText.Text = ex.Message;
            AppendLog($"[BENCH-ERROR] {phase}：{ex.Message}");
            return null;
        }
        finally
        {
            _benchmarkRunning = false;
            UpdateInteractionState();
        }
    }

    private void SaveBenchmarkBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _lastBenchmark is null) return;

        try
        {
            LatencyBenchmarkStore.SaveBaseline(_lastBenchmark);
            _savedBaseline = _lastBenchmark;
            RenderBaselineStatus();
            BenchmarkConclusionText.Text = "已保存当前测试作为跨重启 / 手动调整的对比基准。修改 VBS、HVCI 等设置并重启后，再运行一次测试即可比较。";
            AppendLog("[BENCH] 已保存跨重启 / 手动调整对比基准。 ");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存测试基准失败：" + ex.Message, "LIME_YOUR_PC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadBenchmarkBaseline()
    {
        _savedBaseline = LatencyBenchmarkStore.LoadBaseline();
        RenderBaselineStatus();
    }

    private void RenderBaselineStatus()
    {
        if (BenchmarkBaselineText is null) return;

        if (_savedBaseline is null)
        {
            BenchmarkBaselineText.Text = "跨重启对比基准：未保存（仅在 VBS / HVCI 等场景需要）";
            return;
        }

        BenchmarkBaselineText.Text =
            $"跨重启对比基准：{_savedBaseline.Timestamp:MM-dd HH:mm} · 平均 {FormatMs(_savedBaseline.AverageResponseMs)}";
    }

    private void RenderBenchmarkResult(LatencyBenchmarkResult result, string conclusion)
    {
        BenchmarkAverageText.Text = FormatMs(result.AverageResponseMs);
        BenchmarkOccasionalText.Text = FormatMs(result.OccasionalResponseMs);
        BenchmarkJitterText.Text = FormatMs(result.ResponseJitterMs);
        BenchmarkWorstText.Text = FormatMs(result.WorstSpikeMs);
        BenchmarkConclusionText.Text = conclusion.Trim();
        BenchmarkEnvironmentText.Text =
            $"CPU {result.SystemCpuUsagePercent:0.#}% · {result.ProcessCount} 个进程 · " +
            (result.HighResolutionTimerUsed ? "高精度计时" : "兼容计时");
        BenchmarkBadgeText.Text = "测试完成";
        BenchmarkBadgeText.Foreground = FindBrush("SuccessBrush");
    }

    private void RenderBenchmarkComparison(
        LatencyBenchmarkResult before,
        LatencyBenchmarkResult after,
        LatencyComparison comparison,
        OptimizationAssessment? assessment,
        string? scenarioNote = null)
    {
        BenchmarkAverageText.Text = $"{FormatMs(after.AverageResponseMs)}  {FormatChange(comparison.ResponseImprovementPercent, comparison.ResponseChangeMeaningful)}";
        BenchmarkOccasionalText.Text = FormatMs(after.OccasionalResponseMs);
        BenchmarkJitterText.Text = $"{FormatMs(after.ResponseJitterMs)}  {FormatChange(comparison.JitterImprovementPercent, comparison.JitterChangeMeaningful)}";
        BenchmarkWorstText.Text = FormatMs(after.WorstSpikeMs);
        BenchmarkConclusionText.Text = BuildBenchmarkConclusion(before, after, comparison, assessment, scenarioNote);
        BenchmarkEnvironmentText.Text = comparison.EnvironmentComparable
            ? $"{comparison.EnvironmentNote} · 优化后 CPU {after.SystemCpuUsagePercent:0.#}%"
            : "⚠ " + comparison.EnvironmentNote + "，建议再测一次";
        BenchmarkBadgeText.Text = comparison.EnvironmentComparable ? "A/B 完成" : "结果需复测";
        BenchmarkBadgeText.Foreground = comparison.EnvironmentComparable
            ? FindBrush("SuccessBrush")
            : FindBrush("WarningBrush");
    }

    private static string BuildBenchmarkConclusion(
        LatencyBenchmarkResult before,
        LatencyBenchmarkResult after,
        LatencyComparison comparison,
        OptimizationAssessment? assessment,
        string? scenarioNote = null)
    {
        string responseText;
        if (!comparison.ResponseChangeMeaningful)
        {
            responseText = "未检测到明显的平均响应变化";
        }
        else if (comparison.ResponseImprovementPercent > 0)
        {
            responseText = $"系统响应场景下，平均调度响应延迟降低约 {comparison.ResponseImprovementPercent:0.#}%";
        }
        else
        {
            responseText = $"本次平均调度响应延迟上升约 {Math.Abs(comparison.ResponseImprovementPercent):0.#}%";
        }

        string jitterText;
        if (!comparison.JitterChangeMeaningful)
        {
            jitterText = "响应抖动无明显变化";
        }
        else if (comparison.JitterImprovementPercent > 0)
        {
            jitterText = $"响应抖动降低约 {comparison.JitterImprovementPercent:0.#}%";
        }
        else
        {
            jitterText = $"响应抖动上升约 {Math.Abs(comparison.JitterImprovementPercent):0.#}%";
        }

        string configText = string.Empty;
        if (assessment is not null && assessment.SupportedCount > 0)
        {
            if (assessment.MatchRatio >= 0.999 && !comparison.ResponseChangeMeaningful && !comparison.JitterChangeMeaningful)
            {
                configText = $" 优化前 {assessment.MatchingCount}/{assessment.SupportedCount} 项已符合 LIME 推荐值，你的电源配置本来就已经符合优化目标，因此没有明显变化属于预期结果。";
            }
            else if (assessment.MatchRatio >= 0.80 && !comparison.ResponseChangeMeaningful && !comparison.JitterChangeMeaningful)
            {
                configText = $" 优化前已有 {assessment.MatchingCount}/{assessment.SupportedCount} 项符合 LIME 推荐值，当前电源配置已经高度优化，因此变化较小属于正常现象。";
            }
        }

        string environmentText = comparison.EnvironmentComparable
            ? string.Empty
            : " 两次测试环境差异较大，本次百分比仅供参考。";

        string scenarioText = string.IsNullOrWhiteSpace(scenarioNote) ? string.Empty : scenarioNote.TrimEnd('。') + "。 ";
        return $"{scenarioText}{responseText}（{FormatMs(before.AverageResponseMs)} → {FormatMs(after.AverageResponseMs)}）；{jitterText}。{configText}{environmentText}".Trim();
    }

    private static string BuildOptimizationSummary(
        OptimizationResult optimization,
        OptimizationAssessment? assessment,
        PowerPlanContext planContext)
    {
        string prefix = !planContext.LimePlanIsActive
            ? (planContext.LimePlanExists ? "已切换既存 LIME · " : "已创建并切换 LIME · ")
            : string.Empty;

        string main = $"{prefix}成功 {optimization.SuccessCount} · 跳过 {optimization.SkippedCount} · 验证异常 {optimization.MismatchCount}";
        if (assessment is not null && assessment.SupportedCount > 0)
            main += $" · 优化前已符合 {assessment.MatchingCount}/{assessment.SupportedCount}";
        return main;
    }

    private bool CanReuseLastManualBenchmark(PowerPlanContext context)
    {
        if (!_lastBenchmarkWasManual || _lastBenchmark is null) return false;
        if (DateTime.Now - _lastBenchmark.Timestamp > TimeSpan.FromMinutes(10)) return false;
        if (string.IsNullOrWhiteSpace(context.ActivePlanGuid) || string.IsNullOrWhiteSpace(_lastManualBenchmarkPlanGuid)) return false;

        return context.ActivePlanGuid.Equals(_lastManualBenchmarkPlanGuid, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPowerPlanScenarioNote(PowerPlanContext context)
    {
        if (context.LimePlanIsActive)
            return "同一 LIME_YOUR_PC 电源计划的参数刷新前后对比";

        return context.LimePlanExists
            ? $"已从“{context.ActivePlanName}”切换至既存的 LIME_YOUR_PC 电源计划"
            : $"已从“{context.ActivePlanName}”切换至新创建的 LIME_YOUR_PC 电源计划";
    }

    private static string BuildApplyConfirmMessage(
        PowerPlanContext context,
        bool alreadyOptimalLime,
        bool reuseManualBenchmark)
    {
        if (alreadyOptimalLime)
        {
            return
                $"当前已经使用 LIME_YOUR_PC，且可读取的关键电源参数已符合推荐值。\n\n" +
                $"本次将刷新电源计划说明至 {PowerPlanEngine.Version} 并重新验证参数，不再自动重复 A/B 测试。\n\n" +
                "• 仅修改 / 验证 AC（接通电源）参数\n" +
                "• DC / 电池参数保持不变\n" +
                "• 不删除原有电源计划\n\n继续吗？";
        }

        string testText = reuseManualBenchmark
            ? $"检测到你刚完成的“当前状态”测试，将直接复用为优化前基准；应用后只再测试约 {LatencyBenchmarkEngine.DefaultDurationSeconds} 秒。"
            : $"将自动进行一次优化前测试和一次优化后测试，每次约 {LatencyBenchmarkEngine.DefaultDurationSeconds} 秒。";

        string planText;
        if (context.LimePlanExists && !context.LimePlanIsActive)
        {
            planText = $"当前使用“{context.ActivePlanName}”，同时检测到既存 LIME_YOUR_PC 计划。\n" +
                       "LIME 会先记录当前计划的响应，再切换至既存 LIME 计划并进行优化后对比。";
        }
        else if (!context.LimePlanExists)
        {
            planText = $"当前使用“{context.ActivePlanName}”。LIME 将创建专用电源计划并与当前计划进行前后对比。";
        }
        else
        {
            planText = "将刷新当前 LIME_YOUR_PC 电源计划的白名单参数，并进行前后对比。";
        }

        return
            $"{planText}\n\n{testText}\n\n" +
            "• 仅修改 AC（接通电源）参数\n" +
            "• DC / 电池参数保持不变\n" +
            "• 不删除原有电源计划\n\n" +
            "测试衡量 Windows 软件侧调度响应，不代表鼠标到显示器的端到端输入延迟。\n\n" +
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

    private void AppendBenchmarkLog(string phase, LatencyBenchmarkResult result)
    {
        AppendLog($"[BENCH] {phase}：平均 {FormatMs(result.AverageResponseMs)} · " +
                  $"偶发 {FormatMs(result.OccasionalResponseMs)} · 抖动 {FormatMs(result.ResponseJitterMs)} · " +
                  $"最大尖峰 {FormatMs(result.WorstSpikeMs)} · CPU {result.SystemCpuUsagePercent:0.#}% · 样本 {result.SampleCount}");
    }

    private void AppendComparisonLog(
        LatencyBenchmarkResult before,
        LatencyBenchmarkResult after,
        LatencyComparison comparison,
        OptimizationAssessment? assessment,
        string? scenarioNote = null)
    {
        if (!string.IsNullOrWhiteSpace(scenarioNote))
            AppendLog("[POWER-COMPARE] " + scenarioNote.TrimEnd('。') + "。 ");
        AppendLog($"[BENCH-COMPARE] 平均响应 {FormatMs(before.AverageResponseMs)} -> {FormatMs(after.AverageResponseMs)} " +
                  $"({SignedPercent(comparison.ResponseImprovementPercent)} 改善方向)；" +
                  $"抖动 {FormatMs(before.ResponseJitterMs)} -> {FormatMs(after.ResponseJitterMs)} " +
                  $"({SignedPercent(comparison.JitterImprovementPercent)} 改善方向)。 ");

        if (!comparison.EnvironmentComparable)
            AppendLog("[BENCH-WARN] " + comparison.EnvironmentNote);

        if (assessment is not null && assessment.MatchRatio >= 0.80)
            AppendLog($"[ASSESS] 优化前配置匹配率 {assessment.MatchRatio:P0}，属于已高度优化的电源配置。 ");
    }

    private static string FormatMs(double value)
        => value < 0.01 ? $"{value:0.0000} ms" : $"{value:0.000} ms";

    private static string FormatChange(double improvementPercent, bool meaningful)
    {
        if (!meaningful) return "≈";
        return improvementPercent > 0
            ? $"↓ {improvementPercent:0.#}%"
            : $"↑ {Math.Abs(improvementPercent):0.#}%";
    }

    private static string SignedPercent(double value)
        => value >= 0 ? $"+{value:0.#}%" : $"{value:0.#}%";

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
                    "\n\n当前不会自动重启电脑。请在方便时手动重新启动 Windows。" +
                    "\n\n如果想比较此调整前后的软件侧响应，可在重启前点击“测试当前状态”并“保存对比基准”，重启后再测试。",
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
        RunBenchmarkButton.IsEnabled = idle && _hardware is not null;
        SaveBenchmarkBaselineButton.IsEnabled = idle && _lastBenchmark is not null;

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
