using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PulseBar.Models;
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
/// the taskbar grew or a new tray icon widened the notification area. SetWindowPos isn't called unless
/// size/position changed. When Explorer restarts the old child is destroyed with it;
/// it is recreated on "TaskbarCreated" (or the next tick).
/// </remarks>
public sealed class TaskbarOverlayHost : IDisposable
{
    private const double GapDip = 8;

    // Without TrayNotifyWnd, this much space from the taskbar's right edge is assumed for the notification area
    private const double FallbackTrayWidthDip = 200;

    // If creating the child window fails, pause for a while instead of retrying every second
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly AppState _state;
    private readonly Dispatcher _dispatcher;
    private readonly TaskbarOverlayViewModel _viewModel = new();
    private readonly HwndSource _messageWindow;
    private readonly uint _taskbarCreatedMessage;

    private HwndSource? _source;
    private IntPtr _taskbar;
    private Placement? _lastPlacement;
    private bool _isLightTaskbar;
    private OverlayLayout _layout = OverlayLayout.Balanced;
    private bool _enabled;
    private bool _disposed;
    private DateTime _retryAfter = DateTime.MinValue;
    private int _tickQueued;

    /// <summary>Left-click on the overlay, from the UI thread.</summary>
    public event EventHandler? Clicked;

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
            }
        }
    }

    /// <summary>Compact (CPU + RAM) or Balanced (2x2). UI thread.</summary>
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
            if (_source?.RootVisual is TaskbarOverlay view)
            {
                view.ApplyLayout(value);
            }
        }
    }

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

    /// <summary>Attaches to the taskbar if needed, updates the position and sets the latest values.</summary>
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
            _lastPlacement = null;
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
        _lastPlacement = null;

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
        var placement = ComputePlacement();
        if (placement == _lastPlacement)
        {
            if (placement.Visible)
            {
                TaskbarInterop.EnsureOnTop(hwnd);
            }

            return;
        }

        _lastPlacement = placement;
        if (!placement.Visible)
        {
            TaskbarInterop.Hide(hwnd);
            return;
        }

        TaskbarInterop.PlaceChild(hwnd, _taskbar, placement.X, placement.Y, placement.Width, placement.Height);
    }

    private Placement ComputePlacement()
    {
        if (!TaskbarInterop.TryGetTaskbarLayout(_taskbar, out var bar, out bool isBottom, out bool isAutoHide)
            || !isBottom
            || isAutoHide)
        {
            // For now only a bottom, always-visible taskbar
            return Placement.Hidden;
        }

        double scale = TaskbarInterop.GetScale(_taskbar);
        int barHeight = bar.Bottom - bar.Top;
        int width = (int)Math.Round(TaskbarOverlay.DesignWidth * scale);
        int height = Math.Min(barHeight, (int)Math.Round(TaskbarOverlay.DesignHeight * scale));
        int gap = (int)Math.Round(GapDip * scale);

        int right = TaskbarInterop.TryGetTrayNotifyRect(_taskbar, out var notify)
            ? notify.Left
            : bar.Right - (int)Math.Round(FallbackTrayWidthDip * scale);

        int x = right - gap - width;
        int y = bar.Top + (barHeight - height) / 2;
        if (x < bar.Left || height <= 0)
        {
            return Placement.Hidden;
        }

        return new Placement(true, x, y, width, height);
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

                // This message also arrives when the taskbar position/auto-hide changes
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
                _retryAfter = DateTime.MinValue;
            }

            _lastPlacement = null;
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

    private readonly record struct Placement(bool Visible, int X, int Y, int Width, int Height)
    {
        public static Placement Hidden => default;
    }
}
