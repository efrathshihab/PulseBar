using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.Utilities;
using PulseBar.ViewModels;
using PulseBar.Views;
using System.Windows.Media.Imaging;

namespace PulseBar;

public partial class App : Application
{
    // If one step takes longer than this, it is skipped and the next one runs
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(2);

    // If PulseBar hasn't exited this long after Quit, it is forced to exit
    private static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(5);

    private TaskbarIcon? _trayIcon;
    private TrayIconUpdater? _trayIconUpdater;
    private TaskbarOverlayHost? _taskbarOverlay;
    private PopupWindow? _popup;
    private SystemMonitor? _systemMonitor;
    private OverviewViewModel? _overviewViewModel;
    private AudioController? _audioController;
    private SoundViewModel? _soundViewModel;
    private ClipboardManager? _clipboardManager;
    private ClipboardViewModel? _clipboardViewModel;
    private ClipboardPanel? _clipboardPanel;
    private HotKeyManager? _hotKeys;
    private HotKeyBindings? _hotKeyBindings;
    private SettingsStore? _settingsStore;
    private ThemeService? _theme;
    private StartupManager? _startupManager;
    private SettingsViewModel? _settingsViewModel;
    private UpdateService? _updateService;
    private UpdateViewModel? _updateViewModel;

    // The last tray notification was "update ready", so clicking it restarts to update
    private bool _updateNotificationShown;
    private MenuItem? _showOnTaskbarItem;
    private ScreenshotWatcher? _screenshotWatcher;
    private SingleInstance? _singleInstance;
    private int _quitRequested;
    private bool _capturing;

    // After hiding the popup/panel/menu, wait for Windows' fade animation so it doesn't show up in the image
    private static readonly TimeSpan HideBeforeCaptureDelay = TimeSpan.FromMilliseconds(200);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) => Log.Error("Unhandled UI exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        // "With admin" launch at login: the helper running as admin after UAC only creates/deletes the task and exits
        if (StartupManager.IsHelperInvocation(e.Args))
        {
            Log.Info($"Startup task helper ({e.Args[0]}, PID {Environment.ProcessId})");
            int code = StartupManager.RunHelper(e.Args);
            Log.Info($"Startup task helper finished with {code}");
            Shutdown(code);
            return;
        }

        bool atLogin = e.Args.Contains(StartupManager.StartupArg, StringComparer.OrdinalIgnoreCase);
        Log.Info($"Starting PulseBar (PID {Environment.ProcessId}, {BuildInfo.DevLabel ?? "release"}" +
            $"{(atLogin ? ", at login" : "")}{(StartupManager.IsElevated ? ", admin" : "")})");

        // Only one PulseBar at a time. Release: shows the running one's popup and this one exits.
        // Debug: closes the running one and this one runs, so dotnet run always runs the latest code.
        // Started at login: nothing opens; if one is already running, exit quietly.
        _singleInstance = SingleInstance.TryAcquire(replaceExisting: BuildInfo.IsDebug && !atLogin, activateExisting: !atLogin);
        if (_singleInstance is null)
        {
            Log.Info("Another instance is running; exiting");
            Shutdown();
            return;
        }

        // Settings first, so theme, hotkeys and overlay start with the right values from the beginning
        var settings = AppSettings.Current;
        _settingsStore = new SettingsStore();
        var loaded = _settingsStore.Load(settings);
        // Installed: the stable current\PulseBar.exe of the Velopack install; otherwise this exe
        _startupManager = new StartupManager(AppPaths.ExePath);
        if (!loaded.HasLaunchAtLogin)
        {
            // First run or the file was corrupt: accept whatever the Run key/task has; nothing is deleted
            settings.LaunchAtLogin = _startupManager.Detect();
        }

        if (loaded.WasCorrupt || loaded.InvalidFields.Count > 0 || loaded.FileExisted && !loaded.HasLaunchAtLogin)
        {
            _settingsStore.ScheduleSave(settings);
        }

        _settingsStore.Attach(settings);

        _theme = new ThemeService();
        _theme.Apply(settings.Theme);

        var state = AppState.Current;
        _overviewViewModel = new OverviewViewModel(state, Dispatcher, () => TakeScreenshot("Overview button"));

        // Output device, master and per-app volume: runs on its own audio thread; no UI updates while the popup is hidden
        _audioController = new AudioController();
        _soundViewModel = new SoundViewModel(_audioController, state, Dispatcher);
        _audioController.Start();

        // Clipboard history: listens from the start, history loads in the background
        _clipboardManager = new ClipboardManager(new ClipboardStorage())
        {
            Retention = AppSettings.ToTimeSpan(settings.ClipboardRetention),
            IsCaptureEnabled = settings.ClipboardEnabled,
        };
        _clipboardViewModel = new ClipboardViewModel(_clipboardManager, state, Dispatcher);
        _clipboardManager.Start();

        // New Screenshots folder images: PulseBar's own copy goes to history, and to the clipboard if needed.
        // Files in the user's folder are only read, never changed.
        var clipboardManager = _clipboardManager;
        _screenshotWatcher = new ScreenshotWatcher((data, _) => settings.ClipboardEnabled
            ? clipboardManager.AddScreenshotAsync(data, "Screenshots folder", settings.AutoCopyScreenshots)
            : Task.CompletedTask);
        _screenshotWatcher.IsEnabled = settings.WatchScreenshotsFolder && settings.ClipboardEnabled;

        // Hotkeys are registered after the tray icon (so failures can be reported from the tray); created earlier for the Settings tab
        _hotKeys = new HotKeyManager();
        _hotKeyBindings = new HotKeyBindings(_hotKeys, settings);
        // Auto-update: off when not installed (dotnet run, Debug)
        _updateService = new UpdateService(settings);
        _updateViewModel = new UpdateViewModel(_updateService, Dispatcher, RestartToUpdate);
        _updateService.UpdateReady += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            Notify($"PulseBar {_updateService.ReadyVersion} is ready — click to restart and update",
                H.NotifyIcon.Core.NotificationIcon.Info);
            _updateNotificationShown = true;
        });

        _settingsViewModel = new SettingsViewModel(settings, _hotKeyBindings, _startupManager, _updateViewModel,
            message => Notify(message, H.NotifyIcon.Core.NotificationIcon.Warning));

        // The popup is created once; after that only Hide/Show
        _popup = new PopupWindow(_overviewViewModel, _soundViewModel, _clipboardViewModel, _settingsViewModel);
        _theme.Register(_popup);

        // The hotkey clipboard panel: the same ClipboardViewModel; also created only once
        _clipboardPanel = new ClipboardPanel(_clipboardViewModel, new PasteService(), settings);
        _theme.Register(_clipboardPanel);

        // The popup and the panel are never open at the same time
        state.PopupVisibilityChanged += (_, visible) =>
        {
            if (visible)
            {
                _clipboardPanel.HidePanel(restoreFocus: false);
            }
        };
        state.ClipboardPanelVisibilityChanged += (_, visible) =>
        {
            if (visible)
            {
                _popup.HidePopup();
            }
        };

        _singleInstance.ListenForActivation(() => Dispatcher.BeginInvoke(() => _popup.ShowPopup()));
        _singleInstance.ListenForQuit(() => Dispatcher.BeginInvoke(() => RequestQuit("newer dev build started")));

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.ToolTipText = TrayIconUpdater.ToolTipTitle;
        _trayIcon.TrayLeftMouseUp += (_, _) =>
        {
            Log.Info("Tray: left click");
            _popup.Toggle();
        };
        // Opening the menu while the popup is open changes the foreground for the menu, so the popup is deactivated and hides,
        // and hiding the active window drops the WPF ContextMenu's mouse capture, closing the menu right away
        // (in ~20 ms); a click on Quit then never arrives. So the popup is hidden before the menu opens.
        _trayIcon.TrayRightMouseDown += (_, _) => _popup.HidePopup();
        _trayIcon.PreviewTrayContextMenuOpen += (_, _) =>
        {
            Log.Info("Tray: opening menu");
            _popup.HidePopup();
        };
        if (_trayIcon.ContextMenu is { } menu)
        {
            // To find out where it got lost when Quit doesn't work
            menu.Opened += (_, _) => Log.Info("Tray menu: opened");
            menu.Closed += (_, _) => Log.Info("Tray menu: closed");
            menu.PreviewMouseDown += (_, args) =>
                Log.Info($"Tray menu: mouse {args.ChangedButton} down on '{MenuItemHeader(args.OriginalSource)}'");

            _showOnTaskbarItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Tag == "ShowOnTaskbar");
            _showOnTaskbarItem?.IsChecked = settings.ShowTaskbarOverlay;
        }

        _trayIcon.TrayBalloonTipClicked += (_, _) =>
        {
            if (_updateNotificationShown)
            {
                Log.Info("Tray: update notification clicked");
                RestartToUpdate();
            }
        };

        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        BindHotKey(HotKeyAction.ClipboardPanel, () => _clipboardPanel?.Toggle());
        BindHotKey(HotKeyAction.Screenshot, () => TakeScreenshot("hotkey"));
        _trayIconUpdater = new TrayIconUpdater(state, _trayIcon, _audioController, Dispatcher);

        // Live values on the taskbar, left of the tray's ^ arrow; clicking opens the popup like the tray icon
        _taskbarOverlay = new TaskbarOverlayHost(state, Dispatcher);
        _taskbarOverlay.Clicked += (_, _) =>
        {
            Log.Info("Taskbar overlay: click");
            _popup.Toggle();
        };
        _taskbarOverlay.Layout = settings.OverlayLayout;
        _taskbarOverlay.IsEnabled = settings.ShowTaskbarOverlay;

        // Changes from the Settings tab or the tray menu take effect right away
        settings.PropertyChanged += OnSettingChanged;

        // SystemMonitor always runs, because the tray icon shows CPU %
        _systemMonitor = new SystemMonitor(state);
        _systemMonitor.Start();

        _updateService.Start();

        Log.Info("Started");

        // After startup finishes, so the panel opens quickly even on the first hotkey
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _clipboardPanel?.Prewarm());

        // Whether the Run key or task runs the current exe (the installer may change the path); in the background
        var startupManager = _startupManager;
        var launchMode = settings.LaunchAtLogin;
        Task.Run(() => startupManager.Verify(launchMode)).ContinueWith(
            task => Dispatcher.BeginInvoke(() => _settingsViewModel?.SetLaunchIssue(task.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>
    /// Global hotkey. If another app already holds it, reports it from the tray instead of crashing;
    /// the tray menu and popup still work, and Settings shows a red message.
    /// </summary>
    private void BindHotKey(HotKeyAction action, Action callback)
    {
        if (_hotKeyBindings is null || _hotKeyBindings.Bind(action, callback))
        {
            return;
        }

        Notify($"{_hotKeyBindings.Get(action).DisplayText} is used by another app", H.NotifyIcon.Core.NotificationIcon.Warning);
    }

    /// <summary>When settings change, applies them to the related services right away. Arrives on the UI thread.</summary>
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        var settings = AppSettings.Current;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme):
                _theme?.Apply(settings.Theme);
                break;

            case nameof(AppSettings.ShowTaskbarOverlay):
                _taskbarOverlay?.IsEnabled = settings.ShowTaskbarOverlay;
                _showOnTaskbarItem?.IsChecked = settings.ShowTaskbarOverlay;
                break;

            case nameof(AppSettings.OverlayLayout):
                _taskbarOverlay?.Layout = settings.OverlayLayout;
                break;

            case nameof(AppSettings.ClipboardRetention):
                _clipboardManager?.Retention = AppSettings.ToTimeSpan(settings.ClipboardRetention);
                break;

            case nameof(AppSettings.ClipboardEnabled):
            case nameof(AppSettings.WatchScreenshotsFolder):
                _clipboardManager?.IsCaptureEnabled = settings.ClipboardEnabled;
                _screenshotWatcher?.IsEnabled = settings.WatchScreenshotsFolder && settings.ClipboardEnabled;
                break;
        }
    }

    /// <summary>Restart (banner, dialog or notification click): the updater applies the update after we exit.</summary>
    private void RestartToUpdate()
    {
        _settingsStore?.Flush();
        if (_updateService?.PrepareRestart() == true)
        {
            RequestQuit("restart to update");
        }
        else
        {
            Notify("Couldn't start the update", H.NotifyIcon.Core.NotificationIcon.Error);
        }
    }

    private void Notify(string message, H.NotifyIcon.Core.NotificationIcon icon)
    {
        _updateNotificationShown = false;
        try
        {
            _trayIcon?.ShowNotification("PulseBar", message, icon);
        }
        catch (Exception ex)
        {
            Log.Error("Tray notification", ex);
        }
    }

    private void TakeScreenshot_Click(object sender, RoutedEventArgs e) => TakeScreenshot("tray menu");

    /// <summary>
    /// Opens Windows' screen clip (the result lands on the clipboard and the listener picks it up). Hides the popup
    /// and panel first so PulseBar isn't in the image. Without Snipping Tool, captures the whole screen of the
    /// mouse's monitor and adds it to history and the clipboard.
    /// </summary>
    private async void TakeScreenshot(string trigger)
    {
        if (_capturing || _clipboardManager is not { } manager)
        {
            return;
        }

        _capturing = true;
        try
        {
            Log.Info($"Screenshot requested ({trigger})");
            _popup?.HidePopup();
            _clipboardPanel?.HidePanel(restoreFocus: true);
            await Task.Delay(HideBeforeCaptureDelay);

            if (ScreenCapture.TryLaunchScreenClip())
            {
                return;
            }

            byte[]? png = await Task.Run(ScreenCapture.CaptureCursorMonitorPng);
            if (png is null)
            {
                Notify("Couldn't take a screenshot", H.NotifyIcon.Core.NotificationIcon.Error);
                return;
            }

            // Our own capture: goes to the clipboard regardless of AutoCopyScreenshots.
            // With history off, only to the clipboard.
            if (AppSettings.Current.ClipboardEnabled)
            {
                await manager.AddScreenshotAsync(png, "PulseBar", copyToClipboard: true);
            }
            else
            {
                CopyPngToClipboard(png);
            }

            Notify("Snipping Tool not found, captured the full screen", H.NotifyIcon.Core.NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    private static void CopyPngToClipboard(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Clipboard.SetImage(frame);
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot copy to clipboard", ex);
        }
    }

    private void ShowOnTaskbar_Click(object sender, RoutedEventArgs e)
    {
        // Changing the setting makes OnSettingChanged toggle the overlay and save
        if (sender is MenuItem item)
        {
            AppSettings.Current.ShowTaskbarOverlay = item.IsChecked;
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => RequestQuit("tray menu");

    private static string MenuItemHeader(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is MenuItem item)
            {
                return item.Header?.ToString() ?? "?";
            }

            if (node is not System.Windows.Media.Visual)
            {
                break;
            }
        }

        return source?.GetType().Name ?? "?";
    }

    /// <summary>
    /// Starts shutting down and starts a watchdog: if the process hasn't ended within <see cref="ExitDeadline"/>,
    /// it logs and calls Environment.Exit(0).
    /// </summary>
    private void RequestQuit(string reason)
    {
        if (Interlocked.Exchange(ref _quitRequested, 1) == 1)
        {
            Log.Info($"Quit requested again ({reason}); already quitting");
            return;
        }

        Log.Info($"Quit requested ({reason})");

        var watchdog = new Thread(() =>
        {
            Thread.Sleep(ExitDeadline);
            Log.Error($"Still running {ExitDeadline.TotalSeconds:0} s after quit; forcing exit");
            Environment.Exit(0);
        })
        {
            IsBackground = true, // won't keep the process alive on a normal exit
            Name = "PulseBar exit watchdog",
        };
        watchdog.Start();

        try
        {
            Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown() threw; forcing exit", ex);
            Environment.Exit(0);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("OnExit: shutting down");
        var total = Stopwatch.StartNew();

        // Runs in the background, so if it hangs it can be skipped after 2 seconds
        // (PDH, LibreHardwareMonitor's Computer.Close, the monitor loop)
        RunStep("UpdateService", () => _updateService?.Dispose());
        RunStep("SystemMonitor", () => _systemMonitor?.Dispose(), offUiThread: true);

        // The rest touch UI-thread windows/HwndSources, so they run on the UI thread; timed and logged
        RunStep("TrayIconUpdater", () => _trayIconUpdater?.Dispose());
        RunStep("TaskbarOverlay", () => _taskbarOverlay?.Dispose());
        RunStep("OverviewViewModel", () => _overviewViewModel?.Dispose());
        RunStep("SoundViewModel", () => _soundViewModel?.Dispose());
        RunStep("AudioController", () => _audioController?.Dispose(), offUiThread: true); // releases COM, thread (2 seconds)
        RunStep("SettingsViewModel", () => _settingsViewModel?.Dispose());
        RunStep("UpdateViewModel", () => _updateViewModel?.Dispose());
        RunStep("HotKeyBindings", () => _hotKeyBindings?.Dispose());
        RunStep("HotKeys", () => _hotKeys?.Dispose());
        RunStep("ClipboardPanel", () => _clipboardPanel?.CloseForShutdown());
        RunStep("ClipboardViewModel", () => _clipboardViewModel?.Dispose());
        RunStep("ScreenshotWatcher", () => _screenshotWatcher?.Dispose());
        RunStep("ClipboardManager", () => _clipboardManager?.Dispose()); // listener, worker (2 seconds), final save
        RunStep("Popup", () => _popup?.CloseForShutdown());
        RunStep("TrayIcon", () => _trayIcon?.Dispose());
        RunStep("SettingsStore", () => _settingsStore?.Dispose()); // writes any pending save
        RunStep("SingleInstance", () => _singleInstance?.Dispose());

        // Last: a ready update (Restart not clicked) is applied by Velopack's updater once we have exited
        RunStep("ApplyUpdateOnExit", () => _updateService?.ApplyOnExit());

        Log.Info($"OnExit: done in {total.ElapsedMilliseconds} ms");
        base.OnExit(e);
    }

    private static void RunStep(string name, Action action, bool offUiThread = false)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (offUiThread)
            {
                var task = Task.Run(action);
                if (!task.Wait(StepTimeout))
                {
                    Log.Error($"Shutdown: {name} did not finish in {StepTimeout.TotalSeconds:0} s; skipped");
                    return;
                }
            }
            else
            {
                action();
            }

            long ms = stopwatch.ElapsedMilliseconds;
            if (ms > StepTimeout.TotalMilliseconds)
            {
                Log.Error($"Shutdown: {name} took {ms} ms");
            }
            else
            {
                Log.Info($"Shutdown: {name} ({ms} ms)");
            }
        }
        catch (Exception ex)
        {
            // Even if one step fails, the rest still run
            Log.Error($"Shutdown: {name} failed", (ex as AggregateException)?.InnerException ?? ex);
        }
    }
}
