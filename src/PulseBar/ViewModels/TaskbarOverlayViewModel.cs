using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>
/// The four values of the taskbar overlay. ObservableProperty notifies only when a value changes,
/// so the overlay isn't redrawn while the numbers stay the same.
/// </summary>
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

    [ObservableProperty]
    public partial string GpuTempText { get; set; } = NotAvailable;

    [ObservableProperty]
    public partial UsageLevel GpuTempLevel { get; set; }

    /// <summary>Must be called from the UI thread.</summary>
    public void Update(SystemSnapshot s)
    {
        CpuText = Format(s.CpuPercent, "%");
        CpuLevel = UsageLevels.Evaluate(s.CpuPercent, UsageLevels.UsageWarning, UsageLevels.UsageCritical);

        GpuText = Format(s.GpuPercent, "%");
        GpuLevel = UsageLevels.Evaluate(s.GpuPercent, UsageLevels.UsageWarning, UsageLevels.UsageCritical);

        RamText = Format(s.RamPercent, "%");
        RamLevel = UsageLevels.Evaluate(s.RamPercent, UsageLevels.UsageWarning, UsageLevels.UsageCritical);

        GpuTempText = Format(s.GpuTempCelsius, "°C");
        GpuTempLevel = UsageLevels.Evaluate(s.GpuTempCelsius, UsageLevels.GpuTempWarning, UsageLevels.GpuTempCritical);
    }

    private static string Format(double? value, string unit)
    {
        if (value is not { } v || double.IsNaN(v))
        {
            return NotAvailable;
        }

        // More than 3 digits wouldn't fit the fixed width
        return Math.Clamp(Math.Round(v), 0, 999).ToString("0", CultureInfo.InvariantCulture) + unit;
    }
}
