using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>
/// The taskbar overlay's values. ObservableProperty notifies only when a value changes,
/// so the overlay isn't redrawn while the numbers stay the same.
/// </summary>
/// <remarks>
/// Balanced layout: row 1 "CPU 5% 45°C" (or "CPU 5% RAM 85%" without a CPU temperature),
/// row 2 "GPU 1% 54°C" (or just "GPU 1%" without a GPU temperature). Compact: CPU and RAM.
/// </remarks>
public sealed partial class TaskbarOverlayViewModel : ObservableObject
{
    private const string NotAvailable = "N/A";

    [ObservableProperty]
    public partial string CpuText { get; set; } = NotAvailable;

    [ObservableProperty]
    public partial UsageLevel CpuLevel { get; set; }

    [ObservableProperty]
    public partial string GpuText { get; set; } = NotAvailable;

    [ObservableProperty]
    public partial UsageLevel GpuLevel { get; set; }

    [ObservableProperty]
    public partial string RamText { get; set; } = NotAvailable;

    [ObservableProperty]
    public partial UsageLevel RamLevel { get; set; }

    /// <summary>Row 1, after the CPU usage: "" when it shows the CPU temperature, "RAM" otherwise.</summary>
    [ObservableProperty]
    public partial string Row1ExtraLabel { get; set; } = "RAM";

    /// <summary>Row 1, last value: the CPU temperature, or the RAM usage when there is no CPU temperature.</summary>
    [ObservableProperty]
    public partial string Row1ExtraText { get; set; } = NotAvailable;

    [ObservableProperty]
    public partial UsageLevel Row1ExtraLevel { get; set; }

    /// <summary>Row 2, last value: the GPU temperature, or empty when there is none.</summary>
    [ObservableProperty]
    public partial string GpuTempRowText { get; set; } = "";

    [ObservableProperty]
    public partial UsageLevel GpuTempLevel { get; set; }

    /// <summary>Full names on hover, e.g. "CPU 5% · CPU temp 45°C · GPU 1% · GPU temp 54°C · RAM 85%".</summary>
    [ObservableProperty]
    public partial string ToolTipText { get; set; } = "";

    /// <summary>Must be called from the UI thread.</summary>
    public void Update(SystemSnapshot s)
    {
        CpuText = Format(s.CpuPercent, "%");
        CpuLevel = UsageLevels.Evaluate(s.CpuPercent, UsageLevels.UsageWarning, UsageLevels.UsageCritical);

        GpuText = Format(s.GpuPercent, "%");
        GpuLevel = UsageLevels.Evaluate(s.GpuPercent, UsageLevels.UsageWarning, UsageLevels.UsageCritical);

        RamText = Format(s.RamPercent, "%");
        RamLevel = UsageLevels.Evaluate(s.RamPercent, UsageLevels.UsageWarning, UsageLevels.UsageCritical);

        string cpuTemp = Format(s.CpuTempCelsius, "°C");
        if (HasValue(s.CpuTempCelsius))
        {
            Row1ExtraLabel = "";
            Row1ExtraText = cpuTemp;
            Row1ExtraLevel = UsageLevels.Evaluate(s.CpuTempCelsius, UsageLevels.TempWarning, UsageLevels.TempCritical);
        }
        else
        {
            Row1ExtraLabel = "RAM";
            Row1ExtraText = RamText;
            Row1ExtraLevel = RamLevel;
        }

        string gpuTemp = Format(s.GpuTempCelsius, "°C");
        GpuTempRowText = HasValue(s.GpuTempCelsius) ? gpuTemp : "";
        GpuTempLevel = UsageLevels.Evaluate(s.GpuTempCelsius, UsageLevels.GpuTempWarning, UsageLevels.GpuTempCritical);

        ToolTipText = $"CPU {CpuText} · CPU temp {cpuTemp} · GPU {GpuText} · GPU temp {gpuTemp} · RAM {RamText}";
    }

    private static bool HasValue(double? value) => value is { } v && !double.IsNaN(v);

    private static string Format(double? value, string unit)
    {
        if (!HasValue(value))
        {
            return NotAvailable;
        }

        // More than 3 digits wouldn't fit the fixed width
        return Math.Clamp(Math.Round(value!.Value), 0, 999).ToString("0", CultureInfo.InvariantCulture) + unit;
    }
}
