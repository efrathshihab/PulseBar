using System.Windows;
using H.NotifyIcon;
using PulseBar.Services;
using PulseBar.ViewModels;
using PulseBar.Views;
using Wpf.Ui.Appearance;

namespace PulseBar;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private PopupWindow? _popup;
    private SystemMonitor? _systemMonitor;
    private OverviewViewModel? _overviewViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Windows এর system theme (Light/Dark) অনুসরণ করবে
        ApplicationThemeManager.ApplySystemTheme();

        // SystemMonitor সবসময় চলবে (দিন ৫ এ tray icon এ CPU % এর জন্য দরকার)
        _systemMonitor = new SystemMonitor();
        _systemMonitor.Start();
        _overviewViewModel = new OverviewViewModel(_systemMonitor, Dispatcher);

        // Popup একবারই বানানো হয়; পরে শুধু Hide/Show
        _popup = new PopupWindow(_overviewViewModel);
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
        _overviewViewModel?.Dispose();
        _systemMonitor?.Dispose();
        _popup?.CloseForShutdown();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
