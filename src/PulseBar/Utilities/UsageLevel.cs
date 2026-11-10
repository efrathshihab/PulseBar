using System.Windows.Media;

namespace PulseBar.Utilities;

/// <summary>মান কতটা উঁচু: স্বাভাবিক, সতর্ক না বিপদ।</summary>
public enum UsageLevel
{
    Unknown,
    Normal,
    Warning,
    Critical,
}

public static class UsageLevels
{
    // CPU/RAM/Disk এর জন্য
    public const double UsageWarning = 60;
    public const double UsageCritical = 85;

    // CPU temperature (°C) এর জন্য
    public const double TempWarning = 70;
    public const double TempCritical = 85;

    /// <summary>Threshold এর রং; বাকি সব রং theme থেকে আসে।</summary>
    public static readonly Color Amber = Color.FromRgb(0xF5, 0x9E, 0x0B);
    public static readonly Color Red = Color.FromRgb(0xE5, 0x48, 0x4D);

    public static readonly SolidColorBrush AmberBrush = Freeze(new SolidColorBrush(Amber));
    public static readonly SolidColorBrush RedBrush = Freeze(new SolidColorBrush(Red));

    public static UsageLevel Evaluate(double? value, double warning, double critical)
    {
        if (value is not { } v)
        {
            return UsageLevel.Unknown;
        }

        return v >= critical ? UsageLevel.Critical
             : v >= warning ? UsageLevel.Warning
             : UsageLevel.Normal;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
