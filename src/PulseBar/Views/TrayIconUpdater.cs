using System.Globalization;
using System.Windows.Threading;
using H.NotifyIcon;
using PulseBar.Models;
using PulseBar.Utilities;
using DrawingColor = System.Drawing.Color;
using DrawingIcon = System.Drawing.Icon;

namespace PulseBar.Views;

/// <summary>
/// Tray icon এ CPU % এর সংখ্যা আর level অনুযায়ী রং দেখায়।
/// প্রতি ২ সেকেন্ডে একবার দেখে, আর শুধু সংখ্যা বা রং বদলালে icon নতুন করে বানায়।
/// </summary>
/// <remarks>
/// GeneratedIconSource এর property বদলালে H.NotifyIcon প্রতিবার নতুন icon বানায় কিন্তু পুরনো
/// handle ছাড়ে না (এক মিনিটে ~১৭০০ GDI handle জমেছিল)। তাই icon নিজে আঁকা হয় আর পুরনোটা destroy হয়।
/// </remarks>
public sealed class TrayIconUpdater : IDisposable
{
    // Snapshot আসে প্রতি ১ সেকেন্ডে; ১.৫ সেকেন্ডের নিয়মে কার্যত প্রতি ২টা snapshot এ একবার
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1.5);

    private static readonly DrawingColor Purple = DrawingColor.FromArgb(0x7C, 0x5C, 0xFF);
    private static readonly DrawingColor Amber = ToDrawing(UsageLevels.Amber);
    private static readonly DrawingColor Red = ToDrawing(UsageLevels.Red);

    private readonly AppState _state;
    private readonly TaskbarIcon _trayIcon;
    private readonly Dispatcher _dispatcher;

    private DateTime _lastCheck = DateTime.MinValue;
    private string _lastText = "P";
    private UsageLevel _lastLevel = UsageLevel.Unknown;
    /// <summary>Debug build এ যেমন "PulseBar (dev 21:05)", যাতে বোঝা যায় কোন build চলছে।</summary>
    public static readonly string ToolTipTitle =
        BuildInfo.DevLabel is { } dev ? $"PulseBar ({dev})" : "PulseBar";

    private string _lastToolTip = ToolTipTitle;

    // এখন tray তে যে icon বসানো আছে; পরেরটা বসলে এটা destroy হয়
    private DrawingIcon? _currentIcon;

    public TrayIconUpdater(AppState state, TaskbarIcon trayIcon, Dispatcher dispatcher)
    {
        _state = state;
        _trayIcon = trayIcon;
        _dispatcher = dispatcher;
        _state.SnapshotUpdated += OnSnapshotUpdated;
    }

    public void Dispose()
    {
        _state.SnapshotUpdated -= OnSnapshotUpdated;
        TrayIconRenderer.Destroy(_currentIcon);
        _currentIcon = null;
    }

    private void OnSnapshotUpdated(object? sender, SystemSnapshot s)
    {
        if (s.Timestamp - _lastCheck < MinInterval)
        {
            return;
        }

        _lastCheck = s.Timestamp;

        string text;
        UsageLevel level;
        if (s.CpuPercent is { } cpu)
        {
            int rounded = (int)Math.Min(99, Math.Round(cpu));
            text = rounded.ToString(CultureInfo.InvariantCulture);
            level = UsageLevels.Evaluate(cpu, UsageLevels.UsageWarning, UsageLevels.UsageCritical);
        }
        else
        {
            text = "P";
            level = UsageLevel.Unknown;
        }

        string toolTip = ToolTipTitle + " — CPU " + Percent(s.CpuPercent)
                       + " · GPU " + Percent(s.GpuPercent)
                       + " · RAM " + Percent(s.RamPercent);

        bool iconChanged = text != _lastText || level != _lastLevel;
        bool toolTipChanged = toolTip != _lastToolTip;
        if (!iconChanged && !toolTipChanged)
        {
            return;
        }

        _lastText = text;
        _lastLevel = level;
        _lastToolTip = toolTip;

        _dispatcher.BeginInvoke(() => Apply(iconChanged, text, level, toolTipChanged, toolTip));
    }

    private void Apply(bool iconChanged, string text, UsageLevel level, bool toolTipChanged, string toolTip)
    {
        if (iconChanged)
        {
            var background = level switch
            {
                UsageLevel.Warning => Amber,
                UsageLevel.Critical => Red,
                _ => Purple,
            };

            // Amber এর উপর সাদা লেখা পড়া কঠিন, তাই কালো
            var foreground = level == UsageLevel.Warning ? DrawingColor.Black : DrawingColor.White;

            try
            {
                var icon = TrayIconRenderer.Create(text, background, foreground);
                _trayIcon.UpdateIcon(icon);

                // Shell নিজের কপি রাখে, তাই আগের icon এখন ছেড়ে দেওয়া যায়
                TrayIconRenderer.Destroy(_currentIcon);
                _currentIcon = icon;
            }
            catch
            {
                // Icon আঁকা না গেলে আগেরটাই থাকবে
            }
        }

        if (toolTipChanged)
        {
            _trayIcon.ToolTipText = toolTip;
        }
    }

    private static string Percent(double? value) =>
        value is { } v ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%" : "N/A";

    private static DrawingColor ToDrawing(System.Windows.Media.Color c) => DrawingColor.FromArgb(c.R, c.G, c.B);
}
