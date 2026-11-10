using System.Globalization;

namespace PulseBar.Utilities;

/// <summary>
/// Bytes কে File Explorer এর মতো 1024 ভিত্তিক লেখায় বদলায়: "7.2 GB", "476 GB"।
/// </summary>
public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>যেমন "7.2 GB"।</summary>
    public static string Format(ulong bytes)
    {
        int unit = PickUnit(bytes);
        return $"{FormatNumber(bytes, unit)} {Units[unit]}";
    }

    /// <summary>Speed, যেমন "850 KB/s", "12.4 MB/s"।</summary>
    public static string FormatSpeed(ulong bytesPerSec) => $"{Format(bytesPerSec)}/s";

    /// <summary>দুটো মান total এর একক দিয়ে: যেমন "7.2 / 15.8 GB"।</summary>
    public static string FormatPair(ulong used, ulong total)
    {
        int unit = PickUnit(total);
        return $"{FormatNumber(used, unit)} / {FormatNumber(total, unit)} {Units[unit]}";
    }

    private static int PickUnit(ulong bytes)
    {
        int unit = 0;
        double value = bytes;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit;
    }

    private static string FormatNumber(ulong bytes, int unit)
    {
        double value = bytes / Math.Pow(1024, unit);
        if (unit == 0)
        {
            return bytes.ToString(CultureInfo.CurrentCulture);
        }

        // ১০০ এর কম হলে এক দশমিক ("7.2", "15.8"), বড় হলে পূর্ণ সংখ্যা ("476")
        return value < 100
            ? value.ToString("0.#", CultureInfo.CurrentCulture)
            : value.ToString("0", CultureInfo.CurrentCulture);
    }
}
