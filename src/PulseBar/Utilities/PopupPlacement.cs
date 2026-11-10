using System.Runtime.InteropServices;

namespace PulseBar.Utilities;

/// <summary>
/// Taskbar কোন কিনারায় আছে দেখে popup কে tray এর কাছের কোণায় বসায়।
/// সব হিসাব physical pixel এ, monitor এর নিজস্ব DPI দিয়ে, যাতে 125%/150% scaling এও ঠিক থাকে।
/// </summary>
internal static class PopupPlacement
{
    private const double GapDip = 12;

    public static void PlaceNearTray(IntPtr hwnd, double widthDip, double heightDip)
    {
        var edge = TaskbarEdge.Bottom;
        var monitor = IntPtr.Zero;

        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
        {
            edge = (TaskbarEdge)abd.uEdge;
            monitor = MonitorFromRect(ref abd.rc, MONITOR_DEFAULTTOPRIMARY);
        }

        if (monitor == IntPtr.Zero)
        {
            monitor = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        double scale = 1.0;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        int w = (int)Math.Round(widthDip * scale);
        int h = (int)Math.Round(heightDip * scale);
        int gap = (int)Math.Round(GapDip * scale);
        var work = info.rcWork;

        int x = edge == TaskbarEdge.Left ? work.Left + gap : work.Right - w - gap;
        int y = edge == TaskbarEdge.Top ? work.Top + gap : work.Bottom - h - gap;

        // অন্য DPI এর monitor এ সরালে WPF নিজে size বদলাতে পারে, তাই দুবার বসানো হয়।
        const uint flags = SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, flags);
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, flags);
    }

    public static void BringToForeground(IntPtr hwnd)
    {
        SetForegroundWindow(hwnd);
    }

    private enum TaskbarEdge : uint
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3,
    }

    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
