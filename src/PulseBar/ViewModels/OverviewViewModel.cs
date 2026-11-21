using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>
/// Builds the Overview gauges and text from AppState's snapshot.
/// Updates nothing while the popup is hidden; sets the latest values as soon as it opens.
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject, IDisposable
{
    private const string NotAvailable = "N/A";

    private static readonly string SystemDriveRoot =
        Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    private readonly AppState _state;
    private readonly Dispatcher _dispatcher;
    private readonly Action _takeScreenshot;
    private readonly Action _restartAsAdmin;

    /// <summary>Official PawnIO project page (by namazso); its download is the signed installer.</summary>
    public const string PawnIoUrl = "https://pawnio.eu/";

    public const string NotAdminToolTip =
        "Windows only lets administrator apps read CPU temperature. " +
        "Turn on 'Launch at login → With admin' in Settings to always see it.";

    public const string NoDriverToolTip =
        "CPU temperature needs the PawnIO driver. Install it, then restart PulseBar.";
    private IReadOnlyList<DriveUsage>? _lastDrives;

    [ObservableProperty]
    public partial double? CpuValue { get; set; }

    [ObservableProperty]
    public partial double? GpuValue { get; set; }

    /// <summary>e.g. "2.1 / 6 GB".</summary>
    [ObservableProperty]
    public partial string GpuSubText { get; set; } = string.Empty;

    /// <summary>GPU name for the GPU gauge tooltip.</summary>
    [ObservableProperty]
    public partial string? GpuName { get; set; }

    [ObservableProperty]
    public partial double? GpuTempValue { get; set; }

    [ObservableProperty]
    public partial double? RamValue { get; set; }

    [ObservableProperty]
    public partial string RamSubText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double? DiskValue { get; set; }

    [ObservableProperty]
    public partial string DiskSubText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double? TempValue { get; set; }

    /// <summary>Why the temperature is N/A, shown on hover. null when it works.</summary>
    [ObservableProperty]
    public partial string? TempToolTip { get; set; }

    /// <summary>"Enable" under CPU Temp: not running as admin.</summary>
    [ObservableProperty]
    public partial bool ShowEnableCpuTemp { get; set; }

    /// <summary>"Install driver" under CPU Temp: admin, but PawnIO is missing.</summary>
    [ObservableProperty]
    public partial bool ShowInstallDriver { get; set; }

    [ObservableProperty]
    public partial string DownloadText { get; set; } = $"↓ {NotAvailable}";

    [ObservableProperty]
    public partial string UploadText { get; set; } = $"↑ {NotAvailable}";

    /// <summary>Fixed drives other than the system drive.</summary>
    public ObservableCollection<DriveItem> OtherDrives { get; } = [];

    /// <param name="takeScreenshot">When Overview's "Screenshot" button is pressed.</param>
    /// <param name="restartAsAdmin">After the user confirms "Enable" under CPU Temp: restart PulseBar as administrator.</param>
    public OverviewViewModel(AppState state, Dispatcher dispatcher, Action takeScreenshot, Action restartAsAdmin)
    {
        _state = state;
        _dispatcher = dispatcher;
        _takeScreenshot = takeScreenshot;
        _restartAsAdmin = restartAsAdmin;
        _state.SnapshotUpdated += OnSnapshotUpdated;
        _state.PopupVisibilityChanged += OnPopupVisibilityChanged;
    }

    [RelayCommand]
    private void TakeScreenshot() => _takeScreenshot();

    /// <summary>Called after the confirm dialog.</summary>
    [RelayCommand]
    private void EnableCpuTemp() => _restartAsAdmin();

    [RelayCommand]
    private static void InstallDriver()
    {
        try
        {
            Process.Start(new ProcessStartInfo(PawnIoUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Open PawnIO page", ex);
        }
    }

    public void Dispose()
    {
        _state.SnapshotUpdated -= OnSnapshotUpdated;
        _state.PopupVisibilityChanged -= OnPopupVisibilityChanged;
    }

    private void OnSnapshotUpdated(object? sender, SystemSnapshot snapshot)
    {
        // No need to even wake the UI thread while the popup is hidden
        if (!_state.IsPopupVisible)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_state.IsPopupVisible)
            {
                Apply(snapshot);
            }
        });
    }

    private void OnPopupVisibilityChanged(object? sender, bool visible)
    {
        // Comes from the UI thread; the latest values are set before the popup shows
        if (visible && _state.Snapshot is { } latest)
        {
            Apply(latest);
        }
    }

    private void Apply(SystemSnapshot s)
    {
        // Kept as whole numbers so the same value doesn't animate again and again
        CpuValue = Round(s.CpuPercent);

        RamValue = Round(s.RamPercent);
        RamSubText = s.RamUsedBytes is { } used && s.RamTotalBytes is { } total
            ? ByteFormatter.FormatPair(used, total)
            : NotAvailable;

        GpuValue = Round(s.GpuPercent);
        GpuName = s.GpuName;
        GpuSubText = s.VramUsedBytes is { } vramUsed && s.VramTotalBytes is { } vramTotal && vramTotal > 0
            ? ByteFormatter.FormatPair(vramUsed, vramTotal)
            : NotAvailable;
        GpuTempValue = Round(s.GpuTempCelsius);

        TempValue = Round(s.CpuTempCelsius);
        TempToolTip = s.CpuTempCelsius is not null ? null : s.CpuTempStatus switch
        {
            TemperatureStatus.NotAdmin => NotAdminToolTip,
            TemperatureStatus.NoDriver => NoDriverToolTip,
            TemperatureStatus.NoSensor => "No CPU temperature sensor found",
            _ => null,
        };

        // The actions disappear as soon as a temperature is read
        ShowEnableCpuTemp = s.CpuTempCelsius is null && s.CpuTempStatus == TemperatureStatus.NotAdmin;
        ShowInstallDriver = s.CpuTempCelsius is null && s.CpuTempStatus == TemperatureStatus.NoDriver;

        DownloadText = "↓ " + (s.DownloadBytesPerSec is { } d ? ByteFormatter.FormatSpeed(d) : NotAvailable);
        UploadText = "↑ " + (s.UploadBytesPerSec is { } u ? ByteFormatter.FormatSpeed(u) : NotAvailable);

        // Drives: the monitor gives a new list only once every 10 seconds
        if (!ReferenceEquals(s.Drives, _lastDrives))
        {
            _lastDrives = s.Drives;
            ApplyDrives(s.Drives);
        }
    }

    private void ApplyDrives(IReadOnlyList<DriveUsage> drives)
    {
        DriveUsage? system = null;
        OtherDrives.Clear();

        foreach (var drive in drives)
        {
            if (system is null && string.Equals(drive.Name, SystemDriveRoot, StringComparison.OrdinalIgnoreCase))
            {
                system = drive;
                continue;
            }

            OtherDrives.Add(new DriveItem(
                DriveName(drive),
                $"{ByteFormatter.FormatPair(drive.UsedBytes, drive.TotalBytes)} ({Math.Round(drive.Percent):0}%)",
                drive.Percent,
                UsageLevels.Evaluate(drive.Percent, UsageLevels.UsageWarning, UsageLevels.UsageCritical)));
        }

        if (system is not null)
        {
            DiskValue = Math.Round(system.Percent);
            DiskSubText = $"{DriveName(system)}  {ByteFormatter.FormatPair(system.UsedBytes, system.TotalBytes)}";
        }
        else
        {
            DiskValue = null;
            DiskSubText = NotAvailable;
        }
    }

    private static string DriveName(DriveUsage drive) => drive.Name.TrimEnd('\\');

    private static double? Round(double? value) => value is { } v ? Math.Round(v) : null;
}

/// <summary>One row of the drive list at the bottom of Overview.</summary>
public sealed record DriveItem(string Name, string UsageText, double Percent, UsageLevel Level);
