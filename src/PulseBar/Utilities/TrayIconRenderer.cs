using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PulseBar.Utilities;

/// <summary>Background of the live CPU tray icon.</summary>
public enum TrayIconStyle
{
    /// <summary>Violet gradient, like the logo.</summary>
    Normal,

    /// <summary>Amber, with dark text.</summary>
    Warning,

    /// <summary>Red.</summary>
    Critical,
}

/// <summary>
/// Draws the tray icons: the live CPU number on a Windows 11 style rounded square, and the app logo.
/// Icons are drawn at the real small-icon size for the current DPI (16 px at 100%, 20 at 125%, 24 at 150%),
/// so they stay sharp. Each icon's HICON must be released with <see cref="Destroy"/>, otherwise GDI
/// handles pile up.
/// </summary>
public static class TrayIconRenderer
{
    private const int SM_CXSMICON = 49;
    private const float CornerRadius = 0.25f;

    // Digit height as a share of the icon: one digit a bit bigger, two digits a bit smaller
    private const float OneDigitHeight = 0.70f;
    private const float TwoDigitHeight = 0.60f;
    private const float MaxTextWidth = 0.86f;

    private static readonly (Color Top, Color Bottom, Color Text) NormalColors =
        (Color.FromArgb(0x4B, 0x2B, 0xF8), Color.FromArgb(0x93, 0x70, 0xFA), Color.White);

    private static readonly (Color Top, Color Bottom, Color Text) WarningColors =
        (Color.FromArgb(0xF8, 0xB8, 0x4E), Color.FromArgb(0xE8, 0x92, 0x0B), Color.FromArgb(0x1F, 0x13, 0x00));

    private static readonly (Color Top, Color Bottom, Color Text) CriticalColors =
        (Color.FromArgb(0xF2, 0x6A, 0x70), Color.FromArgb(0xD8, 0x3A, 0x40), Color.White);

    private static readonly Lazy<FontFamily> DigitFont = new(LoadFont);

    /// <summary>The live CPU icon, at the current small-icon size.</summary>
    public static Icon Create(string text, TrayIconStyle style)
    {
        using var bitmap = Render(text, style, CurrentIconSize());
        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>
    /// The app logo for the tray (at startup, and while CPU % isn't known yet), from the embedded
    /// PulseBar.ico at the current small-icon size. null if it can't be loaded.
    /// </summary>
    public static Icon? CreateLogo()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/PulseBar;component/Assets/PulseBar.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is null)
            {
                return null;
            }

            int size = CurrentIconSize();
            using var icon = new Icon(stream, size, size);
            using var bitmap = icon.ToBitmap();

            // A handle of our own, so Destroy works the same way for every tray icon
            return Icon.FromHandle(bitmap.GetHicon());
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException or InvalidOperationException)
        {
            Log.Error("Tray logo icon", ex);
            return null;
        }
    }

    /// <summary>Draws the CPU icon into a bitmap of exactly <paramref name="size"/> pixels (also used for previews).</summary>
    public static Bitmap Render(string text, TrayIconStyle style, int size)
    {
        var (top, bottom, textColor) = style switch
        {
            TrayIconStyle.Warning => WarningColors,
            TrayIconStyle.Critical => CriticalColors,
            _ => NormalColors,
        };

        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        using (var square = RoundedSquare(size, size * CornerRadius))
        using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), top, bottom))
        {
            g.FillPath(fill, square);
        }

        using var glyphs = new GraphicsPath();
        glyphs.AddString(text, DigitFont.Value, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = glyphs.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return bitmap;
        }

        // Whole-pixel digit height and top edge, so the strokes stay crisp at 16 px
        float height = MathF.Round(size * (text.Length >= 2 ? TwoDigitHeight : OneDigitHeight));
        float scale = height / bounds.Height;
        if (bounds.Width * scale > size * MaxTextWidth)
        {
            scale = size * MaxTextWidth / bounds.Width;
            height = bounds.Height * scale;
        }

        float x = (size - bounds.Width * scale) / 2f;
        float y = MathF.Round((size - height) / 2f);

        using (var transform = new Matrix())
        {
            transform.Translate(x, y);
            transform.Scale(scale, scale);
            transform.Translate(-bounds.X, -bounds.Y);
            glyphs.Transform(transform);
        }

        using var textBrush = new SolidBrush(textColor);
        g.FillPath(textBrush, glyphs);
        return bitmap;
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

    /// <summary>The small (tray) icon size for the system DPI: 16 at 100%, 20 at 125%, 24 at 150%, 32 at 200%.</summary>
    public static int CurrentIconSize()
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

    private static GraphicsPath RoundedSquare(int size, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(size - d, 0, d, d, 270, 90);
        path.AddArc(size - d, size - d, d, d, 0, 90);
        path.AddArc(0, size - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Segoe UI Variable Display (Windows 11), otherwise Segoe UI (Windows 10).</summary>
    private static FontFamily LoadFont()
    {
        foreach (string name in new[] { "Segoe UI Variable Display", "Segoe UI" })
        {
            try
            {
                var family = new FontFamily(name);
                if (family.IsStyleAvailable(FontStyle.Bold))
                {
                    return family;
                }

                family.Dispose();
            }
            catch (ArgumentException)
            {
                // Not installed
            }
        }

        return FontFamily.GenericSansSerif;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
