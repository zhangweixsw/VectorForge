using System.Windows;
using System.Diagnostics;
using VectorForge.Services;

namespace VectorForge;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--worker", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exitCode;
            try
            {
                exitCode = WorkerHost.Run();
            }
            catch (Exception ex)
            {
                WorkerHost.WriteEmergencyFailure(ex);
                exitCode = 2;
            }

            Shutdown(exitCode);
            return;
        }

        var currentProcess = Process.GetCurrentProcess();
        var otherInstances = Process.GetProcessesByName(currentProcess.ProcessName)
            .Where(process => process.Id != currentProcess.Id)
            .ToArray();
        if (otherInstances.Length > 0)
        {
            System.Windows.MessageBox.Show(
                "检测到另一个 VectorForge 正在运行。请先从系统托盘退出旧版本，再启动新版。",
                "VectorForge 已在运行",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(3);
            return;
        }

        _singleInstanceMutex = new Mutex(true, @"Local\VectorForgeIllustratorMonitor", out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("VectorForge 已经在运行。", "VectorForge", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(3);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) window.SaveSettingsForSystemShutdown();
        base.OnSessionEnding(e);
    }
}
