using System.Windows;
using H.NotifyIcon;
using PulseBar.Views;
using Wpf.Ui.Appearance;

namespace PulseBar;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private PopupWindow? _popup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Windows এর system theme (Light/Dark) অনুসরণ করবে
        ApplicationThemeManager.ApplySystemTheme();

        // Popup একবারই বানানো হয়; পরে শুধু Hide/Show
        _popup = new PopupWindow();
        SystemThemeWatcher.Watch(_popup);

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.TrayLeftMouseUp += (_, _) => _popup.Toggle();
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _popup?.CloseForShutdown();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
