using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseBar.Utilities;

/// <summary>
/// Exe/dll থেকে app এর icon, একবার পড়ে cache করা। HICON থেকে ছবি কপি করে সাথে সাথে
/// DestroyIcon, তাই কোনো GDI handle ধরে রাখা হয় না। UI thread থেকে ডাকতে হবে।
/// </summary>
public static class AppIconCache
{
    /// <summary>Row এ ২৪ DIP; ২০০% DPI পর্যন্ত ঝাপসা না হওয়ার জন্য ৪৮px।</summary>
    private const int IconSize = 48;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><paramref name="index"/> ঋণাত্মক হলে resource ID। না পেলে null।</summary>
    public static ImageSource? Get(string? path, int index)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string key = $"{path},{index}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var image = Extract(path, index);
        Cache[key] = image;
        return image;
    }

    private static ImageSource? Extract(string path, int index)
    {
        IntPtr icon = IntPtr.Zero;
        try
        {
            uint size = IconSize | (16u << 16);
            if (SHDefExtractIconW(path, index, 0, out icon, IntPtr.Zero, size) != 0 || icon == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // HICON এর সাথে কোনো যোগ না রেখে নিজস্ব pixel এর কপি
            var copy = new WriteableBitmap(source);
            copy.Freeze();
            return copy;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icon != IntPtr.Zero)
            {
                DestroyIcon(icon);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIconW(string iconFile, int index, uint flags, out IntPtr largeIcon, IntPtr smallIcon, uint iconSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
