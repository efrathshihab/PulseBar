using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.Utilities;
using PulseBar.ViewModels;
using PulseBar.Views;
using Wpf.Ui.Appearance;

namespace PulseBar;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private TrayIconUpdater? _trayIconUpdater;
    private TaskbarOverlayHost? _taskbarOverlay;
    private PopupWindow? _popup;
    private SystemMonitor? _systemMonitor;
    private OverviewViewModel? _overviewViewModel;
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // একসাথে একটাই PulseBar; আগে থেকে চললে তার popup খুলে এটা বন্ধ হবে
        _singleInstance = SingleInstance.TryAcquire();
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        // Windows এর system theme (Light/Dark) অনুসরণ করবে
        ApplicationThemeManager.ApplySystemTheme();

        var state = AppState.Current;
        _overviewViewModel = new OverviewViewModel(state, Dispatcher);

        // Popup একবারই বানানো হয়; পরে শুধু Hide/Show
        _popup = new PopupWindow(_overviewViewModel);
        SystemThemeWatcher.Watch(_popup);
        _singleInstance.ListenForActivation(() => Dispatcher.BeginInvoke(() => _popup.ShowPopup()));

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.TrayLeftMouseUp += (_, _) => _popup.Toggle();
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        _trayIconUpdater = new TrayIconUpdater(state, _trayIcon, Dispatcher);

        // Tray এর ^ arrow এর বাম পাশে taskbar এর উপর live মান; click করলে tray icon এর মতোই popup
        _taskbarOverlay = new TaskbarOverlayHost(state, Dispatcher);
        _taskbarOverlay.Clicked += (_, _) => _popup.Toggle();
        _taskbarOverlay.IsEnabled = true;

        // SystemMonitor সবসময় চলবে, কারণ tray icon এ CPU % দেখায়
        _systemMonitor = new SystemMonitor(state);
        _systemMonitor.Start();
    }

    private void ShowOnTaskbar_Click(object sender, RoutedEventArgs e)
    {
        // আপাতত শুধু memory তে; দিন ১২ এর settings এ save হবে
        if (sender is MenuItem item && _taskbarOverlay is not null)
        {
            _taskbarOverlay.IsEnabled = item.IsChecked;
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _systemMonitor?.Dispose();
        _trayIconUpdater?.Dispose();
        _taskbarOverlay?.Dispose();
        _overviewViewModel?.Dispose();
        _popup?.CloseForShutdown();
        _trayIcon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
