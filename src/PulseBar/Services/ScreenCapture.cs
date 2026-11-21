using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Taking screenshots: opens Windows' own screen clip ("ms-screenclip:"); the result lands on the clipboard
/// and ClipboardManager's listener picks it up. Without Snipping Tool, as a fallback it captures
/// the whole screen of the monitor the mouse is on.
/// </summary>
public static class ScreenCapture
{
    /// <summary>
    /// Opens Windows' screen clip. False if it doesn't start (Snipping Tool uninstalled); never shows
    /// a dialog.
    /// </summary>
    public static bool TryLaunchScreenClip()
    {
        try
        {
            // ErrorDialog = false: without the protocol, throw instead of showing a "look for an app" dialog
            using var process = Process.Start(new ProcessStartInfo("ms-screenclip:")
            {
                UseShellExecute = true,
                ErrorDialog = false,
            });
            Log.Info("Screenshot: opened ms-screenclip");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Error("Screenshot: ms-screenclip could not be started", ex);
            return false;
        }
    }

    /// <summary>
    /// PNG of the whole screen of the monitor the mouse is on. Can be called from any thread; releases every GDI
    /// object. null on failure.
    /// </summary>
    public static byte[]? CaptureCursorMonitorPng()
    {
        if (!GetCursorPos(out var cursor))
        {
            cursor = default;
        }

        IntPtr monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            Log.Error("Screenshot fallback: monitor not found");
            return null;
        }

        var bounds = info.rcMonitor;
        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return null;
            }

            memoryDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                Log.Error("Screenshot fallback: could not create the bitmap");
                return null;
            }

            previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, bounds.Left, bounds.Top, SrcCopy | CaptureBlt))
            {
                Log.Error($"Screenshot fallback: BitBlt failed (Win32 {Marshal.GetLastPInvokeError()})");
                return null;
            }

            // The bitmap is read after it is selected out of the DC
            SelectObject(memoryDc, previous);
            previous = IntPtr.Zero;

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            Log.Info($"Screenshot fallback: captured {width} × {height}");
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot fallback", ex);
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
}
