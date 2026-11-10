using System.Windows;
using H.NotifyIcon;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.ViewModels;
using PulseBar.Views;
using Wpf.Ui.Appearance;

namespace PulseBar;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private TrayIconUpdater? _trayIconUpdater;
    private PopupWindow? _popup;
    private SystemMonitor? _systemMonitor;
    private OverviewViewModel? _overviewViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Windows এর system theme (Light/Dark) অনুসরণ করবে
        ApplicationThemeManager.ApplySystemTheme();

        var state = AppState.Current;
        _overviewViewModel = new OverviewViewModel(state, Dispatcher);

        // Popup একবারই বানানো হয়; পরে শুধু Hide/Show
        _popup = new PopupWindow(_overviewViewModel);
        SystemThemeWatcher.Watch(_popup);

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.TrayLeftMouseUp += (_, _) => _popup.Toggle();
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        _trayIconUpdater = new TrayIconUpdater(state, _trayIcon, Dispatcher);

        // SystemMonitor সবসময় চলবে, কারণ tray icon এ CPU % দেখায়
        _systemMonitor = new SystemMonitor(state);
        _systemMonitor.Start();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _systemMonitor?.Dispose();
        _trayIconUpdater?.Dispose();
        _overviewViewModel?.Dispose();
        _popup?.CloseForShutdown();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
