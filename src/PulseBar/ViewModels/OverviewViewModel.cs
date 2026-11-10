using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Services;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>
/// SystemMonitor এর snapshot থেকে Overview এর দেখানোর লেখা বানায়।
/// মান না বদলালে নতুন string বানায় না।
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject, IDisposable
{
    private const string NotAvailable = "N/A";

    private readonly SystemMonitor _monitor;
    private readonly Dispatcher _dispatcher;

    private int _lastCpu = int.MinValue;
    private ulong _lastRamUsedTenths = ulong.MaxValue;
    private IReadOnlyList<DriveUsage>? _lastDrives;
    private ulong? _lastDown = ulong.MaxValue;
    private ulong? _lastUp = ulong.MaxValue;
    private int _lastTemp = int.MinValue;
    private TemperatureStatus? _lastTempStatus;

    [ObservableProperty]
    public partial string CpuText { get; set; } = $"CPU  {NotAvailable}";

    [ObservableProperty]
    public partial string RamText { get; set; } = $"RAM  {NotAvailable}";

    [ObservableProperty]
    public partial string NetworkText { get; set; } = $"↓ {NotAvailable}   ↑ {NotAvailable}";

    [ObservableProperty]
    public partial string TempText { get; set; } = $"CPU Temp  {NotAvailable}";

    /// <summary>Temperature কেন N/A, mouse রাখলে দেখায়। ঠিক থাকলে null।</summary>
    [ObservableProperty]
    public partial string? TempToolTip { get; set; }

    public ObservableCollection<string> DriveLines { get; } = [];

    public OverviewViewModel(SystemMonitor monitor, Dispatcher dispatcher)
    {
        _monitor = monitor;
        _dispatcher = dispatcher;
        _monitor.SnapshotUpdated += OnSnapshotUpdated;

        if (_monitor.Latest is { } latest)
        {
            Apply(latest);
        }
    }

    public void Dispose()
    {
        _monitor.SnapshotUpdated -= OnSnapshotUpdated;
    }

    private void OnSnapshotUpdated(object? sender, SystemSnapshot snapshot)
    {
        _dispatcher.BeginInvoke(() => Apply(snapshot));
    }

    private void Apply(SystemSnapshot s)
    {
        // CPU
        int cpu = s.CpuPercent is { } c ? (int)Math.Round(c) : -1;
        if (cpu != _lastCpu)
        {
            _lastCpu = cpu;
            CpuText = cpu < 0 ? $"CPU  {NotAvailable}" : $"CPU  {cpu}%";
        }

        // RAM: ০.১ GB এর মতো ছোট বদল হলেই নতুন লেখা
        if (s.RamUsedBytes is { } used && s.RamTotalBytes is { } total && s.RamPercent is { } pct)
        {
            ulong key = used / (1024UL * 1024 * 100);
            if (key != _lastRamUsedTenths)
            {
                _lastRamUsedTenths = key;
                RamText = $"RAM  {ByteFormatter.FormatPair(used, total)} ({Math.Round(pct):0}%)";
            }
        }
        else if (_lastRamUsedTenths != ulong.MaxValue - 1)
        {
            _lastRamUsedTenths = ulong.MaxValue - 1;
            RamText = $"RAM  {NotAvailable}";
        }

        // Network
        if (s.DownloadBytesPerSec != _lastDown || s.UploadBytesPerSec != _lastUp)
        {
            _lastDown = s.DownloadBytesPerSec;
            _lastUp = s.UploadBytesPerSec;
            string down = s.DownloadBytesPerSec is { } d ? ByteFormatter.FormatSpeed(d) : NotAvailable;
            string up = s.UploadBytesPerSec is { } u ? ByteFormatter.FormatSpeed(u) : NotAvailable;
            NetworkText = $"↓ {down}   ↑ {up}";
        }

        // CPU temperature
        int temp = s.CpuTempCelsius is { } t ? (int)Math.Round(t) : int.MinValue + 1;
        if (temp != _lastTemp || s.CpuTempStatus != _lastTempStatus)
        {
            _lastTemp = temp;
            _lastTempStatus = s.CpuTempStatus;
            TempText = s.CpuTempCelsius is null ? $"CPU Temp  {NotAvailable}" : $"CPU Temp  {temp} °C";
            TempToolTip = s.CpuTempCelsius is not null ? null : s.CpuTempStatus switch
            {
                TemperatureStatus.NotAdmin => "Administrator হিসেবে চালাতে হবে",
                TemperatureStatus.NoDriver => "PawnIO driver install করা নেই",
                TemperatureStatus.NoSensor => "CPU temperature sensor পাওয়া যায়নি",
                _ => null,
            };
        }

        // Drive: monitor শুধু ১০ সেকেন্ডে একবার নতুন list দেয়
        if (!ReferenceEquals(s.Drives, _lastDrives))
        {
            _lastDrives = s.Drives;
            DriveLines.Clear();

            if (s.Drives.Count == 0)
            {
                DriveLines.Add($"Drive  {NotAvailable}");
            }

            foreach (var d in s.Drives)
            {
                string name = d.Name.TrimEnd('\\');
                DriveLines.Add($"{name}  {ByteFormatter.FormatPair(d.UsedBytes, d.TotalBytes)} ({Math.Round(d.Percent):0}%)");
            }
        }
    }
}
