namespace PulseBar.Models;

/// <summary>
/// The system state at one moment. A value that can't be read stays null ("N/A" in the UI).
/// </summary>
public sealed record SystemSnapshot(
    double? CpuPercent,
    ulong? RamUsedBytes,
    ulong? RamTotalBytes,
    double? RamPercent,
    IReadOnlyList<DriveUsage> Drives,
    ulong? DownloadBytesPerSec,
    ulong? UploadBytesPerSec,
    double? CpuTempCelsius,
    TemperatureStatus CpuTempStatus,
    double? GpuPercent,
    string? GpuName,
    ulong? VramUsedBytes,
    ulong? VramTotalBytes,
    double? GpuTempCelsius,
    DateTime Timestamp);

/// <summary>Usage of one fixed drive. Name is e.g. "C:\".</summary>
public sealed record DriveUsage(
    string Name,
    string Label,
    ulong UsedBytes,
    ulong TotalBytes,
    double Percent);

/// <summary>Why the CPU temperature was or wasn't available.</summary>
public enum TemperatureStatus
{
    Starting,
    Ok,
    NotAdmin,
    NoDriver,
    NoSensor,
}
