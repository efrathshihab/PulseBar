using System.IO;

namespace PulseBar.Utilities;

/// <summary>Debug না Release, আর Debug এ কখন build হয়েছে (যাতে বোঝা যায় কোন build চলছে)।</summary>
public static class BuildInfo
{
    // const না, যাতে if (IsDebug) এ "unreachable code" warning না আসে
#if DEBUG
    public static readonly bool IsDebug = true;
#else
    public static readonly bool IsDebug = false;
#endif

    /// <summary>Debug build এ যেমন "dev 21:05"; Release এ null।</summary>
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
