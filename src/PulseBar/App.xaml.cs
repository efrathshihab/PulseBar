using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private ClipboardManager? _clipboardManager;
    private ClipboardViewModel? _clipboardViewModel;
    private SingleInstance? _singleInstance;
    private int _quitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) => Log.Error("Unhandled UI exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        Log.Info($"Starting PulseBar (PID {Environment.ProcessId}, {BuildInfo.DevLabel ?? "release"})");

        // একসাথে একটাই PulseBar। Release: আগেরটার popup খুলে এটা বন্ধ হবে।
        // Debug: আগেরটাকে বন্ধ করে এটা চলবে, যাতে dotnet run দিলে সবসময় সর্বশেষ code চলে।
        _singleInstance = SingleInstance.TryAcquire(replaceExisting: BuildInfo.IsDebug);
        if (_singleInstance is null)
        {
            Log.Info("Another instance is running; exiting");
            Shutdown();
            return;
        }

        // Windows এর system theme (Light/Dark) অনুসরণ করবে
        ApplicationThemeManager.ApplySystemTheme();

        var state = AppState.Current;
        _overviewViewModel = new OverviewViewModel(state, Dispatcher);

        // Clipboard history: শুরু থেকেই শোনে, history background এ load হয়
        _clipboardManager = new ClipboardManager(new ClipboardStorage());
        _clipboardViewModel = new ClipboardViewModel(_clipboardManager, state, Dispatcher);
        _clipboardManager.Start();

        // Popup একবারই বানানো হয়; পরে শুধু Hide/Show
        _popup = new PopupWindow(_overviewViewModel, _clipboardViewModel);
        SystemThemeWatcher.Watch(_popup);
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
        }

        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        _trayIconUpdater = new TrayIconUpdater(state, _trayIcon, Dispatcher);

        // Tray এর ^ arrow এর বাম পাশে taskbar এর উপর live মান; click করলে tray icon এর মতোই popup
        _taskbarOverlay = new TaskbarOverlayHost(state, Dispatcher);
        _taskbarOverlay.Clicked += (_, _) =>
        {
            Log.Info("Taskbar overlay: click");
            _popup.Toggle();
        };
        _taskbarOverlay.IsEnabled = true;

        // SystemMonitor সবসময় চলবে, কারণ tray icon এ CPU % দেখায়
        _systemMonitor = new SystemMonitor(state);
        _systemMonitor.Start();

        Log.Info("Started");
    }

    private void ShowOnTaskbar_Click(object sender, RoutedEventArgs e)
    {
        // আপাতত শুধু memory তে; দিন ১২ এর settings এ save হবে
        if (sender is MenuItem item && _taskbarOverlay is not null)
        {
            _taskbarOverlay.IsEnabled = item.IsChecked;
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
        RunStep("ClipboardViewModel", () => _clipboardViewModel?.Dispose());
        RunStep("ClipboardManager", () => _clipboardManager?.Dispose()); // listener, worker (২ সেকেন্ড), শেষ save
        RunStep("Popup", () => _popup?.CloseForShutdown());
        RunStep("TrayIcon", () => _trayIcon?.Dispose());
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
