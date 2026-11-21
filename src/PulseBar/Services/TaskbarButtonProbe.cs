using System.Runtime.InteropServices;
using System.Windows.Automation;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Measures where the primary taskbar's buttons are, with UI Automation, so the overlay never covers them.
/// Windows 11: the children of the XAML "TaskbarFrame" (Widgets, Start, Search, Task View, app buttons).
/// Windows 10: the children of MSTaskListWClass, plus the Start/Search/Task View windows.
/// </summary>
/// <remarks>
/// The container element is found once and cached; each <see cref="Measure"/> is then a single
/// cross-process call that returns only the direct children's bounding rectangles (no tree walk).
/// Call from one background thread at a time: UI Automation must not run on the UI thread, because the
/// overlay is itself a child window of the taskbar and a query could end up calling back into it.
/// Never throws.
/// </remarks>
public sealed class TaskbarButtonProbe
{
    private const string WidgetsAutomationId = "WidgetsButton";

    // When the container can't be found, don't search again every second
    private static readonly TimeSpan FindRetryDelay = TimeSpan.FromSeconds(10);

    private static readonly string[] Windows10LeftButtonClasses = ["Start", "TrayButton", "TrayDummySearchControl"];

    private readonly CacheRequest _childCache;
    private AutomationElement? _container;
    private IntPtr _containerTaskbar;
    private bool _isWindows10;
    private DateTime _findAfter = DateTime.MinValue;
    private volatile bool _resetRequested;

    public TaskbarButtonProbe()
    {
        _childCache = new CacheRequest
        {
            // Only the cached properties come back; no live references are kept for the children
            AutomationElementMode = AutomationElementMode.None,
            TreeScope = TreeScope.Element,
        };
        _childCache.Add(AutomationElement.BoundingRectangleProperty);
        _childCache.Add(AutomationElement.AutomationIdProperty);
        _childCache.Add(AutomationElement.IsOffscreenProperty);
    }

    /// <summary>Why the last <see cref="Measure"/> failed; null after a success.</summary>
    public string? LastError { get; private set; }

    /// <summary>Drops the cached container (Explorer restarted). Safe from any thread.</summary>
    public void Reset() => _resetRequested = true;

    /// <summary>The buttons of <paramref name="taskbar"/>, or null when they couldn't be measured.</summary>
    public TaskbarButtons? Measure(IntPtr taskbar)
    {
        if (_resetRequested)
        {
            _resetRequested = false;
            _container = null;
            _findAfter = DateTime.MinValue;
        }

        // A cached element can go stale (Explorer restarted, taskbar rebuilt); then find it once more right away
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (_container is null || _containerTaskbar != taskbar)
                {
                    _container = null;
                    if (DateTime.UtcNow < _findAfter)
                    {
                        return null; // LastError stays from the failed search
                    }

                    if (!TryFindContainer(taskbar))
                    {
                        _findAfter = DateTime.UtcNow + FindRetryDelay;
                        LastError = "taskbar button container not found";
                        return null;
                    }
                }

                var result = ReadButtons(taskbar, _container!);
                if (result is null)
                {
                    LastError = "no taskbar buttons found";
                    return null;
                }

                LastError = null;
                return result;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException
                or InvalidOperationException or TimeoutException or ArgumentException)
            {
                _container = null;
                LastError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        _findAfter = DateTime.UtcNow + FindRetryDelay;
        return null;
    }

    private bool TryFindContainer(IntPtr taskbar)
    {
        // Windows 11: Shell_TrayWnd > DesktopWindowContentBridge > InputSite > TaskbarFrame.
        // Found through the window handles, so the search never enters PulseBar's own overlay window.
        var bridge = TaskbarInterop.FindChild(taskbar, "Windows.UI.Composition.DesktopWindowContentBridge");
        var site = TaskbarInterop.FindChild(bridge, "Windows.UI.Input.InputSite.WindowClass");
        if (site != IntPtr.Zero)
        {
            var root = AutomationElement.FromHandle(site);
            var isFrame = new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskbarFrame");
            var frame = root.FindFirst(TreeScope.Children, isFrame) ?? root.FindFirst(TreeScope.Descendants, isFrame);
            if (frame is not null)
            {
                SetContainer(frame, taskbar, isWindows10: false);
                return true;
            }
        }

        // Windows 10: Shell_TrayWnd > ReBarWindow32 > MSTaskSwWClass > MSTaskListWClass
        var rebar = TaskbarInterop.FindChild(taskbar, "ReBarWindow32");
        var taskSwitcher = TaskbarInterop.FindChild(rebar, "MSTaskSwWClass");
        var taskList = TaskbarInterop.FindChild(taskSwitcher, "MSTaskListWClass");
        if (taskList != IntPtr.Zero)
        {
            SetContainer(AutomationElement.FromHandle(taskList), taskbar, isWindows10: true);
            return true;
        }

        return false;
    }

    private void SetContainer(AutomationElement container, IntPtr taskbar, bool isWindows10)
    {
        _container = container;
        _containerTaskbar = taskbar;
        _isWindows10 = isWindows10;
    }

    private TaskbarButtons? ReadButtons(IntPtr taskbar, AutomationElement container)
    {
        AutomationElementCollection children;
        using (_childCache.Activate())
        {
            children = container.FindAll(TreeScope.Children, Condition.TrueCondition);
        }

        PixelSpan? buttons = null;
        PixelSpan? widgets = null;
        foreach (AutomationElement child in children)
        {
            if (child.Cached.IsOffscreen || ToSpan(child.Cached.BoundingRectangle) is not { } span)
            {
                continue;
            }

            if (!_isWindows10 && child.Cached.AutomationId == WidgetsAutomationId)
            {
                widgets = widgets?.Union(span) ?? span;
            }
            else
            {
                buttons = buttons?.Union(span) ?? span;
            }
        }

        if (_isWindows10)
        {
            // Start, Search and Task View are separate windows left of the task list on Windows 10
            foreach (var className in Windows10LeftButtonClasses)
            {
                foreach (var rect in TaskbarInterop.VisibleChildRects(taskbar, className))
                {
                    var span = new PixelSpan(rect.Left, rect.Right);
                    buttons = buttons?.Union(span) ?? span;
                }
            }
        }

        return buttons is { } all ? new TaskbarButtons(all, widgets) : null;
    }

    private static PixelSpan? ToSpan(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0
            || double.IsInfinity(rect.Left) || double.IsInfinity(rect.Right))
        {
            return null;
        }

        return new PixelSpan((int)Math.Floor(rect.Left), (int)Math.Ceiling(rect.Right));
    }
}
