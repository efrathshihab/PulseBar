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
/// TaskbarOverlay কে primary taskbar (Shell_TrayWnd) এর child window হিসেবে বসায়।
/// Child হওয়ায় taskbar এ click করলেও পেছনে যায় না, fullscreen এ taskbar এর সাথেই লুকায়,
/// আর Alt+Tab/taskbar এ কখনো দেখায় না।
/// </summary>
/// <remarks>
/// প্রতি snapshot (১ সেকেন্ড) এ জায়গা মিলিয়ে দেখা হয়, কারণ child window কে কেউ জানায় না
/// কখন taskbar বড় হলো বা tray তে নতুন icon এসে notification area চওড়া হলো। আকার/অবস্থান না
/// বদলালে SetWindowPos ডাকা হয় না। Explorer restart হলে পুরনো child নিজেই নষ্ট হয়ে যায়;
/// "TaskbarCreated" এ (বা পরের tick এ) নতুন করে বানানো হয়।
/// </remarks>
public sealed class TaskbarOverlayHost : IDisposable
{
    private const double GapDip = 8;

    // TrayNotifyWnd না পেলে taskbar এর ডান দিক থেকে এতটা জায়গা notification area এর জন্য ধরা হয়
    private const double FallbackTrayWidthDip = 200;

    // Child window বানানো ব্যর্থ হলে প্রতি সেকেন্ডে আবার চেষ্টা না করে কিছুক্ষণ থামা
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
    private bool _enabled;
    private bool _disposed;
    private DateTime _retryAfter = DateTime.MinValue;
    private int _tickQueued;

    /// <summary>Overlay এ left-click করলে, UI thread থেকে।</summary>
    public event EventHandler? Clicked;

    public TaskbarOverlayHost(AppState state, Dispatcher dispatcher)
    {
        _state = state;
        _dispatcher = dispatcher;
        _isLightTaskbar = TaskbarInterop.IsTaskbarLight();

        // Broadcast message (TaskbarCreated, theme/display বদল) শুধু top-level window পায়,
        // তাই একটা অদৃশ্য top-level window শুধু শোনার জন্য
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

    /// <summary>"Show on taskbar"। শুধু UI thread থেকে বদলাতে হবে।</summary>
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

        // UI thread ব্যস্ত থাকলে tick জমতে দেওয়া হয় না; পরেরটা সর্বশেষ snapshot ই পড়বে
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

    /// <summary>দরকার হলে taskbar এ জুড়ে দেয়, জায়গা মেলায়, আর সর্বশেষ মান বসায়।</summary>
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
            // Explorer এখনো চালু হয়নি; TaskbarCreated বা পরের tick এ আবার
            return;
        }

        try
        {
            var parameters = new HwndSourceParameters("PulseBarTaskbarOverlay")
            {
                ParentWindow = taskbar,
                // WS_VISIBLE ছাড়া; জায়গা ঠিক হলে তবেই দেখানো হবে
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
            // Explorer এর সাথে window আগেই নষ্ট হয়ে গেলে Dispose এ কিছু করার থাকে না
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
            // আপাতত শুধু নিচের, সবসময় দেখানো taskbar
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
                // Click এ taskbar বা popup এর focus কেড়ে নেবে না
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
            // Explorer নতুন করে চালু হয়েছে
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

                // Taskbar এর অবস্থান/auto-hide বদলালেও এই message আসে
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
        // Explorer নিজের layout শেষ করার পরে মাপা হয়
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
