using PulseBar.Models;
using PulseBar.Utilities;
using Xunit;

namespace PulseBar.Tests;

/// <summary>
/// TaskbarSpace with made-up taskbars; no real taskbar, screen or UI Automation involved.
/// Numbers are physical pixels, modeled on a 1920x1080 screen with a 48 px (100%) Windows 11 taskbar.
/// </summary>
public class TaskbarSpaceTests
{
    // 100%: Balanced 130, Compact 124, height 30, gap 8
    private const int BarTop = 1032;
    private const int BarHeight = 48;
    private const int BarRight = 1920;
    private const int TrayLeft = 1710;
    private static readonly PixelSpan Widgets = new(6, 158);

    private static TaskbarGeometry Bar(PixelSpan? buttons, PixelSpan? widgets = null, double scale = 1.0,
        int barRight = BarRight, int barHeight = BarHeight, int trayLeft = TrayLeft, int barTop = BarTop) =>
        new(0, barTop, barRight, barTop + barHeight, trayLeft,
            buttons is { } b ? new TaskbarButtons(b, widgets) : null, scale);

    [Fact]
    public void FewIcons_Centered_Right_ShowsBalancedNextToTray()
    {
        var fit = TaskbarSpace.Fit(Bar(new PixelSpan(606, 1315), Widgets), OverlayPosition.Right, OverlayLayout.Balanced);

        Assert.True(fit.Visible);
        Assert.Equal(OverlayLayout.Balanced, fit.Layout);
        Assert.Equal(OverlaySpaceStatus.None, fit.Status);
        Assert.Equal(TrayLeft - 8 - 130, fit.X);
        Assert.Equal(130, fit.Width);
        Assert.Equal(30, fit.Height);
        Assert.Equal(BarTop + 9, fit.Y); // centered vertically
    }

    [Fact]
    public void ManyIcons_Centered_Right_Hides()
    {
        // App buttons reach almost to the tray: only 40 px free
        var fit = TaskbarSpace.Fit(Bar(new PixelSpan(250, 1670), Widgets), OverlayPosition.Right, OverlayLayout.Balanced);

        Assert.False(fit.Visible);
        Assert.Equal(OverlaySpaceStatus.Hidden, fit.Status);
    }

    [Fact]
    public void RoomForCompactOnly_FallsBackToCompact()
    {
        // 142 px free: Balanced needs 130 + 2*8 = 146, Compact needs 124 + 16 = 140
        int buttonsRight = TrayLeft - 142;
        var fit = TaskbarSpace.Fit(Bar(new PixelSpan(500, buttonsRight), Widgets), OverlayPosition.Right, OverlayLayout.Balanced);

        Assert.True(fit.Visible);
        Assert.Equal(OverlayLayout.Compact, fit.Layout);
        Assert.Equal(OverlaySpaceStatus.CompactFallback, fit.Status);
        Assert.Equal(124, fit.Width);
        Assert.Equal(TrayLeft - 8 - 124, fit.X);
        Assert.True(fit.X >= buttonsRight + 8);
    }

    [Fact]
    public void ExactlyEnoughForBalanced_StaysBalanced()
    {
        var fit = TaskbarSpace.Fit(Bar(new PixelSpan(500, TrayLeft - 146)), OverlayPosition.Right, OverlayLayout.Balanced);

        Assert.True(fit.Visible);
        Assert.Equal(OverlayLayout.Balanced, fit.Layout);
        Assert.Equal(TrayLeft - 146 + 8, fit.X);
    }

    [Fact]
    public void BalancedComesBack_WhenAnAppCloses()
    {
        var crowded = Bar(new PixelSpan(500, TrayLeft - 142));
        var roomy = Bar(new PixelSpan(500, TrayLeft - 142 - 44)); // one 44 px app button fewer

        Assert.Equal(OverlayLayout.Compact, TaskbarSpace.Fit(crowded, OverlayPosition.Right, OverlayLayout.Balanced).Layout);
        var fit = TaskbarSpace.Fit(roomy, OverlayPosition.Right, OverlayLayout.Balanced);
        Assert.Equal(OverlayLayout.Balanced, fit.Layout);
        Assert.Equal(OverlaySpaceStatus.None, fit.Status);
    }

    [Fact]
    public void UserChoseCompact_NeverShowsBalanced_AndHidesWhenCompactDoesNotFit()
    {
        var roomy = TaskbarSpace.Fit(Bar(new PixelSpan(606, 1315)), OverlayPosition.Right, OverlayLayout.Compact);
        Assert.Equal(OverlayLayout.Compact, roomy.Layout);
        Assert.Equal(OverlaySpaceStatus.None, roomy.Status);

        var tight = TaskbarSpace.Fit(Bar(new PixelSpan(500, TrayLeft - 139)), OverlayPosition.Right, OverlayLayout.Compact);
        Assert.False(tight.Visible);
        Assert.Equal(OverlaySpaceStatus.Hidden, tight.Status);
    }

    [Fact]
    public void Left_WithWidgets_SitsRightOfWidgets()
    {
        var fit = TaskbarSpace.Fit(Bar(new PixelSpan(606, 1315), Widgets), OverlayPosition.Left, OverlayLayout.Balanced);

        Assert.True(fit.Visible);
        Assert.Equal(Widgets.Right + 8, fit.X);
        Assert.Equal(OverlayLayout.Balanced, fit.Layout);
    }

    [Fact]
    public void Left_WithoutWidgets_SitsAtTaskbarEdge()
    {
        var fit = TaskbarSpace.Fit(Bar(new PixelSpan(606, 1315)), OverlayPosition.Left, OverlayLayout.Balanced);

        Assert.True(fit.Visible);
        Assert.Equal(8, fit.X);
    }

    [Fact]
    public void Left_ManyIcons_Hides_EvenWithRoomOnTheRight()
    {
        // 92 px between Widgets and Start; lots of room on the right, which must not be used
        var fit = TaskbarSpace.Fit(Bar(new PixelSpan(250, 1000), Widgets), OverlayPosition.Left, OverlayLayout.Balanced);

        Assert.False(fit.Visible);
        Assert.Equal(OverlaySpaceStatus.Hidden, fit.Status);
    }

    [Fact]
    public void Right_ManyIcons_Hides_EvenWithRoomOnTheLeft()
    {
        var fit = TaskbarSpace.Fit(Bar(new PixelSpan(700, 1690)), OverlayPosition.Right, OverlayLayout.Balanced);

        Assert.False(fit.Visible);
    }

    [Fact]
    public void LeftAlignedTaskbar_LeftSideHasNoRoom()
    {
        // Start at x=12; Widgets comes after the app buttons when the taskbar is left-aligned
        var buttons = new PixelSpan(12, 600);
        var widgets = new PixelSpan(604, 648);

        var left = TaskbarSpace.Fit(Bar(buttons, widgets), OverlayPosition.Left, OverlayLayout.Balanced);
        Assert.False(left.Visible);
        Assert.Equal(OverlaySpaceStatus.Hidden, left.Status);

        // Right side: Widgets counts as one more button, so the free space starts after it
        var free = TaskbarSpace.FreeSpan(Bar(buttons, widgets), new TaskbarButtons(buttons, widgets), OverlayPosition.Right);
        Assert.Equal(new PixelSpan(648, TrayLeft), free);

        var right = TaskbarSpace.Fit(Bar(buttons, widgets), OverlayPosition.Right, OverlayLayout.Balanced);
        Assert.True(right.Visible);
        Assert.Equal(TrayLeft - 8 - 130, right.X);
    }

    [Fact]
    public void Windows10Style_NoWidgets_AppsFromTheLeftEdge()
    {
        var buttons = new PixelSpan(0, 900);

        Assert.False(TaskbarSpace.Fit(Bar(buttons), OverlayPosition.Left, OverlayLayout.Balanced).Visible);
        Assert.True(TaskbarSpace.Fit(Bar(buttons), OverlayPosition.Right, OverlayLayout.Balanced).Visible);
    }

    [Fact]
    public void Scale125_SizesAndGapScale()
    {
        // 2400x1350 at 125%: taskbar 60 px, tray at 2137
        var g = Bar(new PixelSpan(757, 1645), new PixelSpan(8, 198), scale: 1.25,
            barRight: 2400, barHeight: 60, trayLeft: 2137, barTop: 1290);

        var fit = TaskbarSpace.Fit(g, OverlayPosition.Right, OverlayLayout.Balanced);

        int width = TaskbarSpace.ToPixels(130, 1.25);
        int gap = TaskbarSpace.ToPixels(8, 1.25);
        Assert.Equal(10, gap);
        Assert.True(fit.Visible);
        Assert.Equal(width, fit.Width);
        Assert.Equal(TaskbarSpace.ToPixels(30, 1.25), fit.Height);
        Assert.Equal(2137 - gap - width, fit.X);
        Assert.Equal(1290 + (60 - fit.Height) / 2, fit.Y);

        var left = TaskbarSpace.Fit(g, OverlayPosition.Left, OverlayLayout.Balanced);
        Assert.Equal(198 + gap, left.X);
    }

    [Fact]
    public void Scale150_SameFreePixels_NeedsCompact()
    {
        // 214 px free. At 100% Balanced fits easily; at 150% Balanced needs 195 + 2*12 = 219, Compact 186 + 24 = 210
        var at100 = Bar(new PixelSpan(500, TrayLeft - 214));
        var at150 = Bar(new PixelSpan(500, TrayLeft - 214), scale: 1.5, barHeight: 72, barTop: 1008);

        Assert.Equal(OverlayLayout.Balanced, TaskbarSpace.Fit(at100, OverlayPosition.Right, OverlayLayout.Balanced).Layout);

        var fit = TaskbarSpace.Fit(at150, OverlayPosition.Right, OverlayLayout.Balanced);
        Assert.True(fit.Visible);
        Assert.Equal(OverlayLayout.Compact, fit.Layout);
        Assert.Equal(OverlaySpaceStatus.CompactFallback, fit.Status);
        Assert.Equal(186, fit.Width);
        Assert.Equal(45, fit.Height);
        Assert.Equal(TrayLeft - 12 - 186, fit.X);
    }

    [Fact]
    public void Scale150_TooTight_Hides()
    {
        var at150 = Bar(new PixelSpan(500, TrayLeft - 205), scale: 1.5, barHeight: 72, barTop: 1008);

        Assert.False(TaskbarSpace.Fit(at150, OverlayPosition.Right, OverlayLayout.Balanced).Visible);
    }

    [Fact]
    public void NotMeasured_FallsBackToTray_IgnoringPosition()
    {
        var fit = TaskbarSpace.Fit(Bar(null), OverlayPosition.Left, OverlayLayout.Balanced);

        Assert.True(fit.Visible);
        Assert.Equal(OverlayLayout.Balanced, fit.Layout);
        Assert.Equal(TrayLeft - 8 - 130, fit.X);
        Assert.Equal(OverlaySpaceStatus.None, fit.Status);
    }

    [Fact]
    public void ZeroHeightTaskbar_Hides_WithoutSpaceStatus()
    {
        var fit = TaskbarSpace.Fit(Bar(new PixelSpan(606, 1315), barHeight: 0), OverlayPosition.Right, OverlayLayout.Balanced);

        Assert.False(fit.Visible);
        Assert.Equal(OverlaySpaceStatus.None, fit.Status);
    }

    /// <summary>Sweeps button widths, sides, layouts and scales: a visible overlay never touches a button.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void NeverOverlapsButtonsOrWidgets(double scale)
    {
        int gap = TaskbarSpace.ToPixels(TaskbarSpace.GapDip, scale);
        int barRight = (int)(1920 * scale);
        int trayLeft = barRight - (int)(210 * scale);
        int center = barRight / 2;
        var widgets = new PixelSpan((int)(6 * scale), (int)(158 * scale));

        for (int half = 20; half < barRight / 2; half += 7)
        {
            var buttons = new PixelSpan(Math.Max(0, center - half), Math.Min(barRight, center + half));
            foreach (var withWidgets in new[] { false, true })
            {
                PixelSpan? w = withWidgets && widgets.Right <= buttons.Left ? widgets : null;
                var g = Bar(buttons, w, scale, barRight, (int)(48 * scale), trayLeft);

                foreach (var position in new[] { OverlayPosition.Left, OverlayPosition.Right })
                {
                    foreach (var layout in new[] { OverlayLayout.Balanced, OverlayLayout.Compact })
                    {
                        var fit = TaskbarSpace.Fit(g, position, layout);
                        if (!fit.Visible)
                        {
                            Assert.Equal(OverlaySpaceStatus.Hidden, fit.Status);
                            continue;
                        }

                        int left = fit.X - gap;
                        int right = fit.X + fit.Width + gap;
                        Assert.True(right <= buttons.Left || left >= buttons.Right, $"overlaps buttons: {fit} {buttons}");
                        Assert.True(right <= trayLeft, $"overlaps tray: {fit}");
                        Assert.True(left >= 0, $"off the taskbar: {fit}");
                        if (w is { } shownWidgets)
                        {
                            Assert.True(left >= shownWidgets.Right || right <= shownWidgets.Left, $"overlaps widgets: {fit}");
                        }

                        if (layout == OverlayLayout.Compact)
                        {
                            Assert.Equal(OverlayLayout.Compact, fit.Layout);
                        }
                    }
                }
            }
        }
    }
}
