using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PulseBar.Utilities;

/// <summary>
/// Draws tray icons with small text (e.g. "23") on a round background.
/// Each icon's HICON must be released with <see cref="Destroy"/>, otherwise GDI handles pile up.
/// </summary>
internal static class TrayIconRenderer
{
    private const int SM_CXSMICON = 49;

    public static Icon Create(string text, Color background, Color foreground)
    {
        int size = IconSize();

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            using (var fill = new SolidBrush(background))
            {
                g.FillEllipse(fill, 0, 0, size - 1, size - 1);
            }

            // Slightly smaller font for two digits, so it fits inside the circle
            float fontPx = size * (text.Length >= 2 ? 0.74f : 0.86f);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(foreground);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };

            // GDI+ places text slightly low, so it's nudged up
            g.DrawString(text, font, brush, new RectangleF(-size * 0.1f, -size * 0.04f, size * 1.2f, size), format);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>Disposing an Icon.FromHandle icon doesn't release the handle, so DestroyIcon is called directly.</summary>
    public static void Destroy(Icon? icon)
    {
        if (icon is null)
        {
            return;
        }

        IntPtr handle = icon.Handle;
        icon.Dispose();
        DestroyIcon(handle);
    }

    private static int IconSize()
    {
        try
        {
            int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
            return size > 0 ? size : 16;
        }
        catch
        {
            return 16;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
