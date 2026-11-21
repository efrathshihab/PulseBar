using System.IO;

namespace PulseBar.Utilities;

/// <summary>Debug or Release, and when a Debug build was built (to tell which build is running).</summary>
public static class BuildInfo
{
    // Not const, so if (IsDebug) doesn't trigger an "unreachable code" warning
#if DEBUG
    public static readonly bool IsDebug = true;
#else
    public static readonly bool IsDebug = false;
#endif

    /// <summary>In Debug builds e.g. "dev 21:05"; null in Release.</summary>
    public static string? DevLabel { get; } = IsDebug ? $"dev {BuildTime():HH:mm}" : null;

    private static DateTime BuildTime()
    {
        try
        {
            return File.GetLastWriteTime(typeof(BuildInfo).Assembly.Location);
        }
        catch
        {
            return DateTime.Now;
        }
    }
}
