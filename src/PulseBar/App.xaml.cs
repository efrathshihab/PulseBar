using System.Windows;
using H.NotifyIcon;

namespace PulseBar;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // কোনো window খোলা হবে না; শুধু tray icon চালু হবে।
        // MainWindow দিন ২ এ popup হিসেবে ব্যবহার হবে।
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
