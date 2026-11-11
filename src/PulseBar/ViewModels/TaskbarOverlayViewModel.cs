using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PulseBar.Models;
using PulseBar.Utilities;

namespace PulseBar.ViewModels;

/// <summary>
/// Taskbar overlay এর চারটা মান। ObservableProperty শুধু মান বদলালেই notify করে,
/// তাই একই সংখ্যা থাকলে overlay আবার আঁকা হয় না।
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

    /// <summary>UI thread থেকে ডাকতে হবে।</summary>
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

        // ৩ অঙ্কের বেশি হলে নির্দিষ্ট চওড়ায় ধরবে না
        return Math.Clamp(Math.Round(v), 0, 999).ToString("0", CultureInfo.InvariantCulture) + unit;
    }
}
