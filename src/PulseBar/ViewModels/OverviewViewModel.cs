using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>
/// AppState এর snapshot থেকে Overview এর gauge আর লেখা বানায়।
/// Popup লুকানো থাকলে কিছুই update করে না; খুললে সাথে সাথে সর্বশেষ মান বসায়।
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject, IDisposable
{
    private const string NotAvailable = "N/A";

    private static readonly string SystemDriveRoot =
        Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    private readonly AppState _state;
    private readonly Dispatcher _dispatcher;
    private IReadOnlyList<DriveUsage>? _lastDrives;

    [ObservableProperty]
    public partial double? CpuValue { get; set; }

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

    /// <summary>Temperature কেন N/A, mouse রাখলে দেখায়। ঠিক থাকলে null।</summary>
    [ObservableProperty]
    public partial string? TempToolTip { get; set; }

    [ObservableProperty]
    public partial string DownloadText { get; set; } = $"↓ {NotAvailable}";

    [ObservableProperty]
    public partial string UploadText { get; set; } = $"↑ {NotAvailable}";

    /// <summary>System drive ছাড়া বাকি fixed drive গুলো।</summary>
    public ObservableCollection<DriveItem> OtherDrives { get; } = [];

    public OverviewViewModel(AppState state, Dispatcher dispatcher)
    {
        _state = state;
        _dispatcher = dispatcher;
        _state.SnapshotUpdated += OnSnapshotUpdated;
        _state.PopupVisibilityChanged += OnPopupVisibilityChanged;
    }

    public void Dispose()
    {
        _state.SnapshotUpdated -= OnSnapshotUpdated;
        _state.PopupVisibilityChanged -= OnPopupVisibilityChanged;
    }

    private void OnSnapshotUpdated(object? sender, SystemSnapshot snapshot)
    {
        // Popup লুকানো থাকলে UI thread কে জাগানোরও দরকার নেই
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
        // UI thread থেকে আসে; popup দেখানোর আগেই সর্বশেষ মান বসে যায়
        if (visible && _state.Snapshot is { } latest)
        {
            Apply(latest);
        }
    }

    private void Apply(SystemSnapshot s)
    {
        // পূর্ণ সংখ্যায় রাখা হয়, যাতে একই মানে বারবার animation না হয়
        CpuValue = Round(s.CpuPercent);

        RamValue = Round(s.RamPercent);
        RamSubText = s.RamUsedBytes is { } used && s.RamTotalBytes is { } total
            ? ByteFormatter.FormatPair(used, total)
            : NotAvailable;

        TempValue = Round(s.CpuTempCelsius);
        TempToolTip = s.CpuTempCelsius is not null ? null : s.CpuTempStatus switch
        {
            TemperatureStatus.NotAdmin => "Administrator হিসেবে চালাতে হবে",
            TemperatureStatus.NoDriver => "PawnIO driver install করা নেই",
            TemperatureStatus.NoSensor => "CPU temperature sensor পাওয়া যায়নি",
            _ => null,
        };

        DownloadText = "↓ " + (s.DownloadBytesPerSec is { } d ? ByteFormatter.FormatSpeed(d) : NotAvailable);
        UploadText = "↑ " + (s.UploadBytesPerSec is { } u ? ByteFormatter.FormatSpeed(u) : NotAvailable);

        // Drive: monitor শুধু ১০ সেকেন্ডে একবার নতুন list দেয়
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

/// <summary>Overview এর নিচের drive list এর একটা সারি।</summary>
public sealed record DriveItem(string Name, string UsageText, double Percent, UsageLevel Level);
