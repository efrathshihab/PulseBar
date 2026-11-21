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
    // একেকটা ধাপ এর বেশি সময় নিলে সেটা বাদ দিয়ে পরেরটায় যাওয়া হয়
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(2);

    // Quit চাপার পর সব মিলিয়ে এর মধ্যে বন্ধ না হলে জোর করে বন্ধ
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
    private MenuItem? _showOnTaskbarItem;
    private ScreenshotWatcher? _screenshotWatcher;
    private SingleInstance? _singleInstance;
    private int _quitRequested;
    private bool _capturing;

    // Popup/panel/menu লুকানোর পরে Windows এর fade animation শেষ হওয়ার অপেক্ষা, যাতে ছবিতে না আসে
    private static readonly TimeSpan HideBeforeCaptureDelay = TimeSpan.FromMilliseconds(200);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) => Log.Error("Unhandled UI exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        // Launch at login এর "With admin": UAC এর পরে admin হিসেবে চলা helper, শুধু task বানিয়ে/মুছে বন্ধ
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

        // একসাথে একটাই PulseBar। Release: আগেরটার popup খুলে এটা বন্ধ হবে।
        // Debug: আগেরটাকে বন্ধ করে এটা চলবে, যাতে dotnet run দিলে সবসময় সর্বশেষ code চলে।
        // Login এ চালু হলে কিছু খোলে না: আগে থেকে চললে চুপচাপ বন্ধ।
        _singleInstance = SingleInstance.TryAcquire(replaceExisting: BuildInfo.IsDebug && !atLogin, activateExisting: !atLogin);
        if (_singleInstance is null)
        {
            Log.Info("Another instance is running; exiting");
            Shutdown();
            return;
        }

        // Settings সবার আগে, যাতে theme, hotkey, overlay প্রথম থেকেই ঠিক মানে চালু হয়
        var settings = AppSettings.Current;
        _settingsStore = new SettingsStore();
        var loaded = _settingsStore.Load(settings);
        _startupManager = new StartupManager(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PulseBar.exe"));
        if (!loaded.HasLaunchAtLogin)
        {
            // প্রথমবার বা file নষ্ট ছিল: Run key/task এ যা আছে সেটাই মেনে নেওয়া, কিছু মোছা হয় না
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

        // Output device, master আর per-app volume: নিজস্ব audio thread এ চলে; popup লুকানো থাকলে UI update হয় না
        _audioController = new AudioController();
        _soundViewModel = new SoundViewModel(_audioController, state, Dispatcher);
        _audioController.Start();

        // Clipboard history: শুরু থেকেই শোনে, history background এ load হয়
        _clipboardManager = new ClipboardManager(new ClipboardStorage())
        {
            Retention = AppSettings.ToTimeSpan(settings.ClipboardRetention),
            IsCaptureEnabled = settings.ClipboardEnabled,
        };
        _clipboardViewModel = new ClipboardViewModel(_clipboardManager, state, Dispatcher);
        _clipboardManager.Start();

        // Screenshots folder এর নতুন ছবি: PulseBar এর নিজের copy history তে, দরকার হলে clipboard এও।
        // User এর folder এর file শুধু পড়া হয়, কখনো বদলানো হয় না।
        var clipboardManager = _clipboardManager;
        _screenshotWatcher = new ScreenshotWatcher((data, _) => settings.ClipboardEnabled
            ? clipboardManager.AddScreenshotAsync(data, "Screenshots folder", settings.AutoCopyScreenshots)
            : Task.CompletedTask);
        _screenshotWatcher.IsEnabled = settings.WatchScreenshotsFolder && settings.ClipboardEnabled;

        // Hotkey register হয় tray icon এর পরে (fail হলে tray থেকে জানানোর জন্য); Settings tab এর জন্য আগেই বানানো
        _hotKeys = new HotKeyManager();
        _hotKeyBindings = new HotKeyBindings(_hotKeys, settings);
        _settingsViewModel = new SettingsViewModel(settings, _hotKeyBindings, _startupManager,
            message => Notify(message, H.NotifyIcon.Core.NotificationIcon.Warning));

        // Popup একবারই বানানো হয়; পরে শুধু Hide/Show
        _popup = new PopupWindow(_overviewViewModel, _soundViewModel, _clipboardViewModel, _settingsViewModel);
        _theme.Register(_popup);

        // Hotkey এর clipboard panel: একই ClipboardViewModel; এটাও একবারই বানানো হয়
        _clipboardPanel = new ClipboardPanel(_clipboardViewModel, new PasteService(), settings);
        _theme.Register(_clipboardPanel);

        // Popup আর panel একসাথে খোলা থাকবে না
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
        // Popup খোলা অবস্থায় menu খুললে, menu এর জন্য foreground বদলানোয় popup Deactivated হয়ে লুকায়,
        // আর active window লুকানোয় WPF ContextMenu এর mouse capture চলে গিয়ে menu সাথে সাথে
        // (~২০ ms এ) বন্ধ হয়ে যায়; তখন Quit এ click আর পৌঁছায় না। তাই menu খোলার আগেই popup লুকানো।
        _trayIcon.TrayRightMouseDown += (_, _) => _popup.HidePopup();
        _trayIcon.PreviewTrayContextMenuOpen += (_, _) =>
        {
            Log.Info("Tray: opening menu");
            _popup.HidePopup();
        };
        if (_trayIcon.ContextMenu is { } menu)
        {
            // Quit না হলে কোথায় হারাল বোঝার জন্য
            menu.Opened += (_, _) => Log.Info("Tray menu: opened");
            menu.Closed += (_, _) => Log.Info("Tray menu: closed");
            menu.PreviewMouseDown += (_, args) =>
                Log.Info($"Tray menu: mouse {args.ChangedButton} down on '{MenuItemHeader(args.OriginalSource)}'");

            _showOnTaskbarItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Tag == "ShowOnTaskbar");
            _showOnTaskbarItem?.IsChecked = settings.ShowTaskbarOverlay;
        }

        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        BindHotKey(HotKeyAction.ClipboardPanel, () => _clipboardPanel?.Toggle());
        BindHotKey(HotKeyAction.Screenshot, () => TakeScreenshot("hotkey"));
        _trayIconUpdater = new TrayIconUpdater(state, _trayIcon, _audioController, Dispatcher);

        // Tray এর ^ arrow এর বাম পাশে taskbar এর উপর live মান; click করলে tray icon এর মতোই popup
        _taskbarOverlay = new TaskbarOverlayHost(state, Dispatcher);
        _taskbarOverlay.Clicked += (_, _) =>
        {
            Log.Info("Taskbar overlay: click");
            _popup.Toggle();
        };
        _taskbarOverlay.Layout = settings.OverlayLayout;
        _taskbarOverlay.IsEnabled = settings.ShowTaskbarOverlay;

        // Settings tab বা tray menu থেকে বদল সাথে সাথে কাজ করবে
        settings.PropertyChanged += OnSettingChanged;

        // SystemMonitor সবসময় চলবে, কারণ tray icon এ CPU % দেখায়
        _systemMonitor = new SystemMonitor(state);
        _systemMonitor.Start();

        Log.Info("Started");

        // Startup শেষ হলে, যাতে প্রথম hotkey তেও panel দ্রুত খোলে
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _clipboardPanel?.Prewarm());

        // Run key বা task এখনকার exe চালায় কিনা (installer এ path বদলাতে পারে); background এ
        var startupManager = _startupManager;
        var launchMode = settings.LaunchAtLogin;
        Task.Run(() => startupManager.Verify(launchMode)).ContinueWith(
            task => Dispatcher.BeginInvoke(() => _settingsViewModel?.SetLaunchIssue(task.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>
    /// Global hotkey। অন্য app আগে থেকে নিয়ে রাখলে crash না করে tray থেকে জানায়;
    /// tray menu বা popup তখনও কাজ করে, আর Settings এ লাল লেখা দেখায়।
    /// </summary>
    private void BindHotKey(HotKeyAction action, Action callback)
    {
        if (_hotKeyBindings is null || _hotKeyBindings.Bind(action, callback))
        {
            return;
        }

        Notify($"{_hotKeyBindings.Get(action).DisplayText} অন্য app ব্যবহার করছে", H.NotifyIcon.Core.NotificationIcon.Warning);
    }

    /// <summary>Settings বদলালে সংশ্লিষ্ট service এ সাথে সাথে বসানো। UI thread এ আসে।</summary>
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

    private void Notify(string message, H.NotifyIcon.Core.NotificationIcon icon)
    {
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
    /// Windows এর screen clip খোলে (ফল clipboard এ আসে, listener নিজেই ধরে)। আগে popup আর panel
    /// লুকায়, যাতে ছবিতে PulseBar না আসে। Snipping Tool না থাকলে mouse এর monitor এর পুরো screen
    /// নিয়ে history আর clipboard এ দেয়।
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
                Notify("Screenshot নেওয়া যায়নি", H.NotifyIcon.Core.NotificationIcon.Error);
                return;
            }

            // নিজে নেওয়া ছবি: AutoCopyScreenshots যাই হোক, clipboard এ দেওয়া হয়।
            // History বন্ধ থাকলে শুধু clipboard এ।
            if (AppSettings.Current.ClipboardEnabled)
            {
                await manager.AddScreenshotAsync(png, "PulseBar", copyToClipboard: true);
            }
            else
            {
                CopyPngToClipboard(png);
            }

            Notify("Snipping Tool পাওয়া যায়নি, পুরো screen নেওয়া হয়েছে", H.NotifyIcon.Core.NotificationIcon.Info);
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
        // Setting বদলালে OnSettingChanged overlay চালু/বন্ধ করে আর save হয়
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
    /// বন্ধ শুরু করে, আর একটা watchdog চালু করে: <see cref="ExitDeadline"/> এর মধ্যে process
    /// শেষ না হলে log এ লিখে Environment.Exit(0)।
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
            IsBackground = true, // স্বাভাবিকভাবে বন্ধ হলে এটা process কে ধরে রাখবে না
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

        // Background এ চলে, তাই আটকে গেলে ২ সেকেন্ড পরে বাদ দেওয়া যায়
        // (PDH, LibreHardwareMonitor এর Computer.Close, monitor এর loop)
        RunStep("SystemMonitor", () => _systemMonitor?.Dispose(), offUiThread: true);

        // বাকিগুলো UI thread এর window/HwndSource ছোঁয়, তাই UI thread এই; সময় মেপে log
        RunStep("TrayIconUpdater", () => _trayIconUpdater?.Dispose());
        RunStep("TaskbarOverlay", () => _taskbarOverlay?.Dispose());
        RunStep("OverviewViewModel", () => _overviewViewModel?.Dispose());
        RunStep("SoundViewModel", () => _soundViewModel?.Dispose());
        RunStep("AudioController", () => _audioController?.Dispose(), offUiThread: true); // COM ছাড়া, thread (২ সেকেন্ড)
        RunStep("SettingsViewModel", () => _settingsViewModel?.Dispose());
        RunStep("HotKeyBindings", () => _hotKeyBindings?.Dispose());
        RunStep("HotKeys", () => _hotKeys?.Dispose());
        RunStep("ClipboardPanel", () => _clipboardPanel?.CloseForShutdown());
        RunStep("ClipboardViewModel", () => _clipboardViewModel?.Dispose());
        RunStep("ScreenshotWatcher", () => _screenshotWatcher?.Dispose());
        RunStep("ClipboardManager", () => _clipboardManager?.Dispose()); // listener, worker (২ সেকেন্ড), শেষ save
        RunStep("Popup", () => _popup?.CloseForShutdown());
        RunStep("TrayIcon", () => _trayIcon?.Dispose());
        RunStep("SettingsStore", () => _settingsStore?.Dispose()); // বাকি থাকা save লেখা
        RunStep("SingleInstance", () => _singleInstance?.Dispose());

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
            // একটা ধাপ fail করলেও বাকিগুলো চলবে
            Log.Error($"Shutdown: {name} failed", (ex as AggregateException)?.InnerException ?? ex);
        }
    }
}
