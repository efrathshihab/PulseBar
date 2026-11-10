using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PulseBar.Utilities;

/// <summary>
/// Tray এর জন্য গোল ব্যাকগ্রাউন্ডে ছোট লেখা (যেমন "23") দিয়ে icon আঁকে।
/// প্রতিটা icon এর HICON নিজে <see cref="Destroy"/> করতে হয়, নাহলে GDI handle জমে যায়।
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

            // দুই অঙ্ক হলে একটু ছোট font, যাতে গোলের ভেতরে আঁটে
            float fontPx = size * (text.Length >= 2 ? 0.74f : 0.86f);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(foreground);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };

            // GDI+ লেখা সামান্য নিচে বসায়, তাই একটু উপরে তোলা
            g.DrawString(text, font, brush, new RectangleF(-size * 0.1f, -size * 0.04f, size * 1.2f, size), format);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>Icon.FromHandle এর icon Dispose করলে handle ছাড়ে না, তাই নিজে DestroyIcon।</summary>
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
