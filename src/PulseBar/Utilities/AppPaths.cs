using System.IO;

namespace PulseBar.Utilities;

/// <summary>
/// Where PulseBar's exe lives. When installed by Velopack the layout is
/// %LocalAppData%\PulseBarApp\{Update.exe, current\PulseBar.exe}; the current\ path stays the same
/// across updates (only its contents are replaced), so login entries point there.
/// </summary>
/// <remarks>
/// User data lives in %LocalAppData%\PulseBar (settings, clipboard, logs), a different folder from
/// the install folder, so Velopack's uninstall and updates never touch it.
/// </remarks>
public static class AppPaths
{
    public const string ExeName = "PulseBar.exe";

    /// <summary>True when running from a Velopack install (…\current\PulseBar.exe next to ..\Update.exe).</summary>
    public static bool IsInstalledLayout => InstalledExePath is not null;

    /// <summary>The stable current\PulseBar.exe of the install, or null when not installed (dotnet run, Debug).</summary>
    public static string? InstalledExePath
    {
        get
        {
            try
            {
                string dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
                if (!string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetDirectoryName(dir) is not { } root ||
                    !File.Exists(Path.Combine(root, "Update.exe")))
                {
                    return null;
                }

                string exe = Path.Combine(dir, ExeName);
                return File.Exists(exe) ? exe : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>The exe that login entries should start: the installed one, otherwise this process.</summary>
    public static string ExePath =>
        InstalledExePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);
}
