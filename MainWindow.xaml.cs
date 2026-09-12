using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace LIME_YOUR_PC;

public partial class MainWindow : Window
{
    private readonly PowerPlanEngine _engine = new();
    private HardwareInfo? _hardware;
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
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

        int schedule = PowerPlanEngine.GetSchedulingValue(hw);
        ScheduleText.Text = schedule switch
        {
            0 => "全部处理器 (0)",
            2 => "优先高性能处理器 (2)",
            _ => "高性能处理器 (1)"
        };
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
