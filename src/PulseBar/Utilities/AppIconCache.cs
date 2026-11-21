using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseBar.Utilities;

/// <summary>
/// App icons from exe/dll, read once and cached. The image is copied out of the HICON and
/// DestroyIcon is called right away, so no GDI handle is held. Must be called from the UI thread.
/// </summary>
public static class AppIconCache
{
    /// <summary>24 DIP in a row; 48px so it stays sharp up to 200% DPI.</summary>
    private const int IconSize = 48;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A negative <paramref name="index"/> means a resource ID. null if not found.</summary>
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

            // Our own copy of the pixels, with no link to the HICON
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
