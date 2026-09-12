using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace LIME_YOUR_PC;

public partial class App : Application
{
    private static readonly string CrashLog = Path.Combine(AppContext.BaseDirectory, "LIME_YOUR_PC_crash.log");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            "程序遇到了未处理异常，详细信息已写入：\n" + CrashLog + "\n\n" + e.Exception.Message,
            "LIME_YOUR_PC 错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            WriteCrash("AppDomain.UnhandledException", ex);
        else
            WriteCrashText("AppDomain.UnhandledException", e.ExceptionObject?.ToString() ?? "Unknown fatal error");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void WriteCrash(string source, Exception ex)
        => WriteCrashText(source, ex.ToString());

    private static void WriteCrashText(string source, string text)
    {
        try
        {
            File.AppendAllText(
                CrashLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Never throw from a crash handler.
        }
    }
}
