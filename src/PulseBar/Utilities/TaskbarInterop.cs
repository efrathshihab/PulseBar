using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PulseBar.Utilities;

/// <summary>
/// Win32 work for finding the primary taskbar (Shell_TrayWnd), reading its position/DPI/theme, and moving
/// the overlay's child window. All math is in physical pixels.
/// </summary>
internal static class TaskbarInterop
{
    public const int WM_DPICHANGED_AFTERPARENT = 0x02E3;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    public const int WS_CHILD = 0x40000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>The primary monitor's taskbar; IntPtr.Zero if not found.</summary>
    public static IntPtr FindPrimaryTaskbar() => FindWindow("Shell_TrayWnd", null);

    /// <summary>The notification area inside the taskbar (from the ^ arrow to the clock).</summary>
    public static bool TryGetTrayNotifyRect(IntPtr taskbar, out RECT rect)
    {
        rect = default;
        var notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        return notify != IntPtr.Zero
            && IsWindowVisible(notify)
            && GetWindowRect(notify, out rect)
            && rect.Right > rect.Left;
    }

    /// <summary>The first direct child window of <paramref name="parent"/> with this class; IntPtr.Zero if none.</summary>
    public static IntPtr FindChild(IntPtr parent, string className) =>
        parent == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(parent, IntPtr.Zero, className, null);

    /// <summary>Screen rectangles of the visible direct children of <paramref name="parent"/> with this class.</summary>
    public static List<RECT> VisibleChildRects(IntPtr parent, string className)
    {
        var rects = new List<RECT>();
        if (parent == IntPtr.Zero)
        {
            return rects;
        }

        var child = IntPtr.Zero;
        while ((child = FindWindowEx(parent, child, className, null)) != IntPtr.Zero)
        {
            if (IsWindowVisible(child) && GetWindowRect(child, out var rect) && rect.Right > rect.Left)
            {
                rects.Add(rect);
            }
        }

        return rects;
    }

    /// <summary>Whether the taskbar is at the bottom and whether auto-hide is on.</summary>
    public static bool TryGetTaskbarLayout(IntPtr taskbar, out RECT rect, out bool isBottom, out bool isAutoHide)
    {
        isBottom = false;
        isAutoHide = false;
        if (!GetWindowRect(taskbar, out rect))
        {
            return false;
        }

        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = taskbar };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) == IntPtr.Zero)
        {
            return false;
        }

        isBottom = abd.uEdge == ABE_BOTTOM;

        var stateData = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = taskbar };
        isAutoHide = ((long)SHAppBarMessage(ABM_GETSTATE, ref stateData) & ABS_AUTOHIDE) != 0;
        return true;
    }

    /// <summary>The taskbar's DPI scale (1.0 = 100%).</summary>
    public static double GetScale(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    /// <summary>Whether the taskbar uses the Light theme (SystemUsesLightTheme). Dark if unknown.</summary>
    public static bool IsTaskbarLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Places the child window in the parent's client coordinates and brings it above its siblings.</summary>
    public static void PlaceChild(IntPtr hwnd, IntPtr parent, int screenX, int screenY, int width, int height)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        ScreenToClient(parent, ref pt);
        SetWindowPos(hwnd, HWND_TOP, pt.X, pt.Y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>So the taskbar's own XAML layer doesn't cover the overlay.</summary>
    public static void EnsureOnTop(IntPtr hwnd)
    {
        if (GetWindow(hwnd, GW_HWNDPREV) != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public static void Hide(IntPtr hwnd) => ShowWindow(hwnd, SW_HIDE);

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static bool IsChildOf(IntPtr hwnd, IntPtr parent) => GetParent(hwnd) == parent;

    /// <summary>So messages from Explorer aren't blocked even when running as admin.</summary>
    public static void AllowMessage(IntPtr hwnd, uint message) =>
        ChangeWindowMessageFilterEx(hwnd, message, MSGFLT_ALLOW, IntPtr.Zero);

    public static uint RegisterMessage(string name) => RegisterWindowMessage(name);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABE_BOTTOM = 3;
    private const long ABS_AUTOHIDE = 0x01;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0;
    private const uint GW_HWNDPREV = 3;
    private const uint MSGFLT_ALLOW = 1;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
