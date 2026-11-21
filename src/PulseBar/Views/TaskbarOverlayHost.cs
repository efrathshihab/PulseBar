using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.Utilities;
using PulseBar.ViewModels;

namespace PulseBar.Views;

/// <summary>
/// Places TaskbarOverlay as a child window of the primary taskbar (Shell_TrayWnd).
/// Being a child, it doesn't go behind when the taskbar is clicked, hides with the taskbar in fullscreen,
/// and never shows in Alt+Tab or on the taskbar.
/// </summary>
/// <remarks>
/// The position is checked on every snapshot (1 second), because nobody tells a child window when
/// the taskbar grew, an app button was added or a new tray icon widened the notification area.
/// Each tick the taskbar buttons are measured with UI Automation on a background thread
/// (<see cref="TaskbarButtonProbe"/>), and <see cref="TaskbarSpace"/> decides whether Balanced, Compact
/// or nothing fits next to them, so the overlay never covers a button. SetWindowPos isn't called unless
/// size/position/layout changed. When Explorer restarts the old child is destroyed with it;
/// it is recreated on "TaskbarCreated" (or the next tick).
/// </remarks>
public sealed class TaskbarOverlayHost : IDisposable
{
    // Without TrayNotifyWnd, this much space from the taskbar's right edge is assumed for the notification area
    private const double FallbackTrayWidthDip = 200;

    // If creating the child window fails, pause for a while instead of retrying every second
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly AppState _state;
    private readonly Dispatcher _dispatcher;
    private readonly TaskbarOverlayViewModel _viewModel = new();
    private readonly HwndSource _messageWindow;
    private readonly uint _taskbarCreatedMessage;
    private readonly TaskbarButtonProbe _probe = new();

    private HwndSource? _source;
    private IntPtr _taskbar;
    private OverlayFit? _lastFit;
    private bool _isLightTaskbar;
    private OverlayLayout _layout = OverlayLayout.Balanced;
    private OverlayLayout _shownLayout = OverlayLayout.Balanced;
    private OverlayPosition _position = OverlayPosition.Right;
    private OverlaySpaceStatus _status;
    private bool _enabled;
    private bool _disposed;
    private DateTime _retryAfter = DateTime.MinValue;
    private int _tickQueued;

    // Latest UI Automation result for _taskbar; UI thread only
    private MeasureState _measureState = MeasureState.Pending;
    private TaskbarButtons? _buttons;
    private int _measuring;

    /// <summary>Left-click on the overlay, from the UI thread.</summary>
    public event EventHandler? Clicked;

    /// <summary><see cref="Status"/> changed. UI thread.</summary>
    public event EventHandler? StatusChanged;

    public TaskbarOverlayHost(AppState state, Dispatcher dispatcher)
    {
        _state = state;
        _dispatcher = dispatcher;
        _isLightTaskbar = TaskbarInterop.IsTaskbarLight();

        // Broadcast messages (TaskbarCreated, theme/display changes) only reach top-level windows,
        // so an invisible top-level window just for listening
        _messageWindow = new HwndSource(new HwndSourceParameters("PulseBarTaskbarWatcher")
        {
            WindowStyle = TaskbarInterop.WS_POPUP,
            ExtendedWindowStyle = TaskbarInterop.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        });
        _messageWindow.AddHook(MessageWindowHook);

        _taskbarCreatedMessage = TaskbarInterop.RegisterMessage("TaskbarCreated");
        if (_taskbarCreatedMessage != 0)
        {
            TaskbarInterop.AllowMessage(_messageWindow.Handle, _taskbarCreatedMessage);
        }

        TaskbarInterop.AllowMessage(_messageWindow.Handle, TaskbarInterop.WM_SETTINGCHANGE);
        TaskbarInterop.AllowMessage(_messageWindow.Handle, TaskbarInterop.WM_DISPLAYCHANGE);

        _state.SnapshotUpdated += OnSnapshotUpdated;
    }

    /// <summary>"Show on taskbar". Change only from the UI thread.</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value || _disposed)
            {
                return;
            }

            _enabled = value;
            if (value)
            {
                _retryAfter = DateTime.MinValue;
                Refresh();
            }
            else
            {
                Detach();
                SetStatus(OverlaySpaceStatus.None);
            }
        }
    }

    /// <summary>
    /// The user's choice: Compact (CPU + RAM) or Balanced (2x2). When Balanced doesn't fit, Compact is
    /// shown without changing this, and Balanced comes back once there is room. UI thread.
    /// </summary>
    public OverlayLayout Layout
    {
        get => _layout;
        set
        {
            if (_layout == value)
            {
                return;
            }

            _layout = value;
            PlaceAgain();
        }
    }

    /// <summary>Which side of the taskbar buttons the overlay goes on. UI thread.</summary>
    public OverlayPosition Position
    {
        get => _position;
        set
        {
            if (_position == value)
            {
                return;
            }

            _position = value;
            PlaceAgain();
        }
    }

    /// <summary>Whether the overlay had to shrink or hide for lack of space. UI thread.</summary>
    public OverlaySpaceStatus Status => _status;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enabled = false;
        _state.SnapshotUpdated -= OnSnapshotUpdated;
        Detach();
        _messageWindow.RemoveHook(MessageWindowHook);
        _messageWindow.Dispose();
    }

    private void OnSnapshotUpdated(object? sender, SystemSnapshot snapshot)
    {
        if (!_enabled)
        {
            return;
        }

        // Ticks don't pile up while the UI thread is busy; the next one reads the latest snapshot
        if (Interlocked.Exchange(ref _tickQueued, 1) == 1)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _tickQueued, 0);
            Refresh();
        });
    }

    /// <summary>
    /// Attaches to the taskbar if needed, updates the position, sets the latest values and starts the
    /// next button measurement.
    /// </summary>
    private void Refresh()
    {
        if (!_enabled || _disposed)
        {
            return;
        }

        EnsureAttached();
        UpdatePlacement();

        if (_source is not null && _state.Snapshot is { } latest)
        {
            _viewModel.Update(latest);
        }

        StartMeasure();
    }

    /// <summary>A setting changed: place again right away with the latest measurement.</summary>
    private void PlaceAgain()
    {
        _lastFit = null;
        if (_enabled && !_disposed)
        {
            UpdatePlacement();
        }
    }

    /// <summary>
    /// Measures the taskbar buttons on a thread-pool thread, one measurement at a time, so a hung Explorer
    /// can't pile up calls. The result is applied on the UI thread and only moves the overlay if it changed.
    /// </summary>
    private void StartMeasure()
    {
        var taskbar = _taskbar;
        if (_source is null || taskbar == IntPtr.Zero || Interlocked.Exchange(ref _measuring, 1) == 1)
        {
            return;
        }

        Task.Run(() =>
        {
            TaskbarButtons? buttons = null;
            string? error;
            try
            {
                buttons = _probe.Measure(taskbar);
                error = _probe.LastError;
            }
            catch (Exception ex)
            {
                // Measure doesn't throw, but nothing on a background thread may take the app down
                error = ex.Message;
            }

            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _measuring, 0);
                ApplyMeasurement(taskbar, buttons, error);
            });
        });
    }

    private void ApplyMeasurement(IntPtr taskbar, TaskbarButtons? buttons, string? error)
    {
        if (_disposed || !_enabled || taskbar != _taskbar)
        {
            return;
        }

        var state = buttons is null ? MeasureState.Failed : MeasureState.Measured;
        if (state == MeasureState.Failed && _measureState != MeasureState.Failed)
        {
            Log.Error($"Taskbar overlay: couldn't measure the taskbar buttons with UI Automation ({error}); placing it next to the tray");
        }
        else if (state == MeasureState.Measured && _measureState == MeasureState.Failed)
        {
            Log.Info("Taskbar overlay: taskbar buttons can be measured again");
        }

        if (state == _measureState && buttons == _buttons)
        {
            return; // Nothing moved
        }

        _measureState = state;
        _buttons = buttons;
        UpdatePlacement();
    }

    private void SetStatus(OverlaySpaceStatus status)
    {
        if (status == _status)
        {
            return;
        }

        _status = status;
        string? message = status switch
        {
            OverlaySpaceStatus.Hidden => "Taskbar overlay: hidden, not enough space on the taskbar",
            OverlaySpaceStatus.CompactFallback => "Taskbar overlay: showing compact layout, not enough space",
            _ when _enabled => "Taskbar overlay: enough space again",
            _ => null,
        };
        if (message is not null)
        {
            Log.Info(message);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureAttached()
    {
        if (_source is { IsDisposed: false } source
            && TaskbarInterop.IsAlive(source.Handle)
            && TaskbarInterop.IsAlive(_taskbar)
            && TaskbarInterop.IsChildOf(source.Handle, _taskbar))
        {
            return;
        }

        Detach();

        if (DateTime.UtcNow < _retryAfter)
        {
            return;
        }

        var taskbar = TaskbarInterop.FindPrimaryTaskbar();
        if (taskbar == IntPtr.Zero)
        {
            // Explorer hasn't started yet; try again on TaskbarCreated or the next tick
            return;
        }

        try
        {
            var parameters = new HwndSourceParameters("PulseBarTaskbarOverlay")
            {
                ParentWindow = taskbar,
                // Without WS_VISIBLE; shown only once the position is right
                WindowStyle = TaskbarInterop.WS_CHILD | TaskbarInterop.WS_CLIPSIBLINGS,
                ExtendedWindowStyle = TaskbarInterop.WS_EX_TOOLWINDOW | TaskbarInterop.WS_EX_NOACTIVATE,
                Width = 1,
                Height = 1,
                UsesPerPixelTransparency = true,
            };

            var newSource = new HwndSource(parameters);
            newSource.AddHook(OverlayHook);

            var view = new TaskbarOverlay { DataContext = _viewModel };
            view.ApplyTheme(_isLightTaskbar);
            view.ApplyLayout(_layout);
            view.MouseLeftButtonUp += OnOverlayMouseUp;
            newSource.RootVisual = view;

            _source = newSource;
            _taskbar = taskbar;
            _shownLayout = _layout;
            _lastFit = null;

            // A new taskbar (first start or Explorer restarted): stays hidden until its buttons are measured
            _measureState = MeasureState.Pending;
            _buttons = null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ExternalException)
        {
            Detach();
            _retryAfter = DateTime.UtcNow + RetryDelay;
        }
    }

    private void Detach()
    {
        var source = _source;
        _source = null;
        _taskbar = IntPtr.Zero;
        _lastFit = null;

        if (source is null)
        {
            return;
        }

        try
        {
            if (source.RootVisual is TaskbarOverlay view)
            {
                view.MouseLeftButtonUp -= OnOverlayMouseUp;
            }

            if (!source.IsDisposed)
            {
                source.RemoveHook(OverlayHook);
                source.RootVisual = null;
                source.Dispose();
            }
        }
        catch
        {
            // If the window already died with Explorer, there's nothing left to Dispose
        }
    }

    private void UpdatePlacement()
    {
        if (_source is not { IsDisposed: false } source)
        {
            return;
        }

        var hwnd = source.Handle;
        var fit = ComputeFit();
        SetStatus(fit.Status);
        if (fit == _lastFit)
        {
            if (fit.Visible)
            {
                TaskbarInterop.EnsureOnTop(hwnd);
            }

            return;
        }

        _lastFit = fit;
        if (!fit.Visible)
        {
            TaskbarInterop.Hide(hwnd);
            return;
        }

        if (fit.Layout != _shownLayout && source.RootVisual is TaskbarOverlay view)
        {
            view.ApplyLayout(fit.Layout);
            _shownLayout = fit.Layout;
        }

        TaskbarInterop.PlaceChild(hwnd, _taskbar, fit.X, fit.Y, fit.Width, fit.Height);
    }

    private OverlayFit ComputeFit()
    {
        if (!TaskbarInterop.TryGetTaskbarLayout(_taskbar, out var bar, out bool isBottom, out bool isAutoHide)
            || !isBottom
            || isAutoHide)
        {
            // For now only a bottom, always-visible taskbar
            return OverlayFit.Hidden(OverlaySpaceStatus.None);
        }

        if (_measureState == MeasureState.Pending)
        {
            // Not drawn anywhere until we know where the buttons are
            return OverlayFit.Hidden(OverlaySpaceStatus.None);
        }

        double scale = TaskbarInterop.GetScale(_taskbar);
        int trayLeft = TaskbarInterop.TryGetTrayNotifyRect(_taskbar, out var notify)
            ? notify.Left
            : bar.Right - TaskbarSpace.ToPixels(FallbackTrayWidthDip, scale);

        // When UI Automation failed, Buttons is null and TaskbarSpace places it next to the tray like before
        var geometry = new TaskbarGeometry(bar.Left, bar.Top, bar.Right, bar.Bottom, trayLeft,
            _measureState == MeasureState.Measured ? _buttons : null, scale);
        return TaskbarSpace.Fit(geometry, _position, _layout);
    }

    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Clicked?.Invoke(this, EventArgs.Empty);
    }

    private IntPtr OverlayHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case TaskbarInterop.WM_MOUSEACTIVATE:
                // A click doesn't steal focus from the taskbar or the popup
                handled = true;
                return TaskbarInterop.MA_NOACTIVATE;

            case TaskbarInterop.WM_DPICHANGED_AFTERPARENT:
                ScheduleRefresh(reattach: false);
                break;
        }

        return IntPtr.Zero;
    }

    private IntPtr MessageWindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            // Explorer restarted
            ScheduleRefresh(reattach: true);
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case TaskbarInterop.WM_SETTINGCHANGE:
                if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                {
                    UpdateTheme();
                }

                // This message also arrives when the taskbar position/auto-hide/alignment changes
                ScheduleRefresh(reattach: false);
                break;

            case TaskbarInterop.WM_DISPLAYCHANGE:
                ScheduleRefresh(reattach: false);
                break;
        }

        return IntPtr.Zero;
    }

    private void ScheduleRefresh(bool reattach)
    {
        // Measured after Explorer finishes its own layout
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (reattach)
            {
                Detach();
                _probe.Reset();
                _retryAfter = DateTime.MinValue;
            }

            _lastFit = null;
            Refresh();
        });
    }

    /// <summary>
    /// The text color always follows the Windows taskbar color (SystemUsesLightTheme), not PulseBar's
    /// Theme setting; otherwise dark text on a dark taskbar would be unreadable.
    /// </summary>
    private void UpdateTheme()
    {
        bool isLight = TaskbarInterop.IsTaskbarLight();
        if (isLight == _isLightTaskbar)
        {
            return;
        }

        _isLightTaskbar = isLight;
        if (_source?.RootVisual is TaskbarOverlay view)
        {
            view.ApplyTheme(isLight);
        }
    }

    private enum MeasureState
    {
        /// <summary>No result yet for this taskbar.</summary>
        Pending,

        Measured,

        /// <summary>UI Automation didn't work; placed next to the tray like before.</summary>
        Failed,
    }
}
