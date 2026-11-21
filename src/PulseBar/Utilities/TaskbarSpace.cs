using PulseBar.Models;

namespace PulseBar.Utilities;

/// <summary>A horizontal range in physical screen pixels, [Left, Right).</summary>
public readonly record struct PixelSpan(int Left, int Right)
{
    public int Width => Right - Left;

    public PixelSpan Union(PixelSpan other) => new(Math.Min(Left, other.Left), Math.Max(Right, other.Right));
}

/// <summary>
/// What UI Automation found on the taskbar, in physical pixels: the union of all buttons (Start, Search,
/// Task View, app buttons) and, on Windows 11, the Widgets/weather button.
/// </summary>
public readonly record struct TaskbarButtons(PixelSpan Buttons, PixelSpan? Widgets);

/// <summary>Everything the space calculation needs, in physical pixels.</summary>
/// <param name="TrayLeft">Left edge of the notification area (TrayNotifyWnd, the ^ arrow).</param>
/// <param name="Buttons">null when UI Automation couldn't measure the buttons.</param>
/// <param name="Scale">Taskbar DPI scale, 1.0 = 100%.</param>
public readonly record struct TaskbarGeometry(
    int BarLeft,
    int BarTop,
    int BarRight,
    int BarBottom,
    int TrayLeft,
    TaskbarButtons? Buttons,
    double Scale);

/// <summary>Why the overlay isn't shown the way the user chose; shown in Settings.</summary>
public enum OverlaySpaceStatus
{
    /// <summary>Shown as chosen, or hidden for a reason that isn't space (taskbar not at the bottom, off).</summary>
    None,

    /// <summary>Balanced doesn't fit, Compact does.</summary>
    CompactFallback,

    /// <summary>Not even Compact fits.</summary>
    Hidden,
}

/// <summary>Where the overlay goes, in physical screen pixels.</summary>
public readonly record struct OverlayFit(
    bool Visible,
    OverlayLayout Layout,
    int X,
    int Y,
    int Width,
    int Height,
    OverlaySpaceStatus Status)
{
    public static OverlayFit Hidden(OverlaySpaceStatus status) => new(false, OverlayLayout.Balanced, 0, 0, 0, 0, status);
}

/// <summary>
/// Decides where the taskbar overlay fits without covering any taskbar button. Pure math, no Win32,
/// so it can be unit tested with made-up taskbars.
/// </summary>
public static class TaskbarSpace
{
    /// <summary>Overlay sizes (DIP). TaskbarOverlay uses the same values.</summary>
    public const double BalancedWidthDip = 130;
    public const double CompactWidthDip = 124;
    public const double HeightDip = 30;

    /// <summary>Minimum free space on each side of the overlay (DIP; 8 px at 100%).</summary>
    public const double GapDip = 8;

    public static double WidthDip(OverlayLayout layout) =>
        layout == OverlayLayout.Compact ? CompactWidthDip : BalancedWidthDip;

    public static int ToPixels(double dip, double scale) => (int)Math.Round(dip * scale);

    /// <summary>
    /// Balanced if it fits on the chosen side, otherwise Compact (only when the user chose Balanced),
    /// otherwise hidden. The other side is never tried. Without a measurement the overlay goes next to
    /// the tray like before, without checking the buttons.
    /// </summary>
    public static OverlayFit Fit(in TaskbarGeometry g, OverlayPosition position, OverlayLayout preferred)
    {
        int barHeight = g.BarBottom - g.BarTop;
        int height = Math.Min(barHeight, ToPixels(HeightDip, g.Scale));
        int gap = ToPixels(GapDip, g.Scale);
        if (height <= 0 || g.BarRight <= g.BarLeft)
        {
            return OverlayFit.Hidden(OverlaySpaceStatus.None);
        }

        int y = g.BarTop + (barHeight - height) / 2;

        if (g.Buttons is not { } measured)
        {
            int width = ToPixels(WidthDip(preferred), g.Scale);
            int x = g.TrayLeft - gap - width;
            return x < g.BarLeft
                ? OverlayFit.Hidden(OverlaySpaceStatus.Hidden)
                : new OverlayFit(true, preferred, x, y, width, height, OverlaySpaceStatus.None);
        }

        PixelSpan free = FreeSpan(g, measured, position);

        foreach (var layout in Candidates(preferred))
        {
            int width = ToPixels(WidthDip(layout), g.Scale);
            if (free.Width < width + 2 * gap)
            {
                continue;
            }

            // Hug the tray on the right, the widgets (or the taskbar edge) on the left
            int x = position == OverlayPosition.Left ? free.Left + gap : free.Right - gap - width;
            var status = layout == preferred ? OverlaySpaceStatus.None : OverlaySpaceStatus.CompactFallback;
            return new OverlayFit(true, layout, x, y, width, height, status);
        }

        return OverlayFit.Hidden(OverlaySpaceStatus.Hidden);
    }

    /// <summary>The empty part of the taskbar on the chosen side of the buttons.</summary>
    public static PixelSpan FreeSpan(in TaskbarGeometry g, TaskbarButtons measured, OverlayPosition position)
    {
        var buttons = measured.Buttons;
        int leftEdge = g.BarLeft;

        if (measured.Widgets is { } widgets)
        {
            if (widgets.Right <= buttons.Left)
            {
                // Centered taskbar: Widgets sits at the far left, apart from the buttons
                leftEdge = Math.Max(leftEdge, widgets.Right);
            }
            else
            {
                // Left-aligned taskbar (or anything unusual): treat it as one more button
                buttons = buttons.Union(widgets);
            }
        }

        int left;
        int right;
        if (position == OverlayPosition.Left)
        {
            left = leftEdge;
            right = buttons.Left;
        }
        else
        {
            left = buttons.Right;
            right = Math.Min(g.TrayLeft, g.BarRight);
        }

        left = Math.Max(left, g.BarLeft);
        right = Math.Min(right, g.BarRight);
        return right > left ? new PixelSpan(left, right) : new PixelSpan(left, left);
    }

    private static readonly OverlayLayout[] BalancedFirst = [OverlayLayout.Balanced, OverlayLayout.Compact];
    private static readonly OverlayLayout[] CompactOnly = [OverlayLayout.Compact];

    private static OverlayLayout[] Candidates(OverlayLayout preferred) =>
        preferred == OverlayLayout.Balanced ? BalancedFirst : CompactOnly;
}
