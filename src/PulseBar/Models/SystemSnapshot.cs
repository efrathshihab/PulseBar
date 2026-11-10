namespace PulseBar.Models;

/// <summary>
/// এক মুহূর্তের system অবস্থা। কোনো মান পড়া না গেলে সেটা null থাকে (UI তে "N/A")।
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
    DateTime Timestamp);

/// <summary>একটা fixed drive এর ব্যবহার। Name যেমন "C:\"।</summary>
public sealed record DriveUsage(
    string Name,
    string Label,
    ulong UsedBytes,
    ulong TotalBytes,
    double Percent);

/// <summary>CPU temperature কেন পাওয়া গেল বা গেল না।</summary>
public enum TemperatureStatus
{
    Starting,
    Ok,
    NotAdmin,
    NoDriver,
    NoSensor,
}
