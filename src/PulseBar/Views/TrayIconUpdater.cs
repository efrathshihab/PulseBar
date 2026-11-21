using System.Globalization;
using System.Windows.Threading;
using H.NotifyIcon;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.Utilities;
using DrawingColor = System.Drawing.Color;
using DrawingIcon = System.Drawing.Icon;

namespace PulseBar.Views;

/// <summary>
/// Shows the CPU % number on the tray icon, colored by level; the tooltip shows CPU, GPU, RAM and master volume.
/// Checks once every 2 seconds and rebuilds the icon only when the number or color changes.
/// </summary>
/// <remarks>
/// When GeneratedIconSource's properties change, H.NotifyIcon builds a new icon every time but never releases
/// the old handle (~1700 GDI handles piled up in a minute). So the icon is drawn here and the old one destroyed.
/// </remarks>
public sealed class TrayIconUpdater : IDisposable
{
    // Snapshots arrive every second; with a 1.5 second rule this is effectively once every 2 snapshots
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1.5);

    private static readonly DrawingColor Purple = DrawingColor.FromArgb(0x7C, 0x5C, 0xFF);
    private static readonly DrawingColor Amber = ToDrawing(UsageLevels.Amber);
    private static readonly DrawingColor Red = ToDrawing(UsageLevels.Red);

    private readonly AppState _state;
    private readonly TaskbarIcon _trayIcon;
    private readonly AudioController _audio;
    private readonly Dispatcher _dispatcher;

    private DateTime _lastCheck = DateTime.MinValue;
    private string _lastText = "P";
    private UsageLevel _lastLevel = UsageLevel.Unknown;
    /// <summary>In Debug builds e.g. "PulseBar (dev 21:05)", to show which build is running.</summary>
    public static readonly string ToolTipTitle =
        BuildInfo.DevLabel is { } dev ? $"PulseBar ({dev})" : "PulseBar";

    private string _lastToolTip = ToolTipTitle;

    // The icon currently set in the tray; destroyed once the next one is set
    private DrawingIcon? _currentIcon;

    public TrayIconUpdater(AppState state, TaskbarIcon trayIcon, AudioController audio, Dispatcher dispatcher)
    {
        _state = state;
        _trayIcon = trayIcon;
        _audio = audio;
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
                       + " · RAM " + Percent(s.RamPercent)
                       + " · Vol " + VolumeText(_audio.GetMaster());

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

            // White text on amber is hard to read, so black
            var foreground = level == UsageLevel.Warning ? DrawingColor.Black : DrawingColor.White;

            try
            {
                var icon = TrayIconRenderer.Create(text, background, foreground);
                _trayIcon.UpdateIcon(icon);

                // The shell keeps its own copy, so the previous icon can be released now
                TrayIconRenderer.Destroy(_currentIcon);
                _currentIcon = icon;
            }
            catch
            {
                // If the icon can't be drawn, the previous one stays
            }
        }

        if (toolTipChanged)
        {
            _trayIcon.ToolTipText = toolTip;
        }
    }

    private static string Percent(double? value) =>
        value is { } v ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%" : "N/A";

    // The audio thread keeps the volume snapshot; this only reads it, so no COM calls
    private static string VolumeText(MasterVolumeState? master) => master switch
    {
        null => "N/A",
        { IsMuted: true } => "muted",
        _ => Percent(master.Volume * 100.0),
    };

    private static DrawingColor ToDrawing(System.Windows.Media.Color c) => DrawingColor.FromArgb(c.R, c.G, c.B);
}
