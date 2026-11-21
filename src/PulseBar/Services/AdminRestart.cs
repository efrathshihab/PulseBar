using System.ComponentModel;
using System.Diagnostics;
using PulseBar.Utilities;

namespace PulseBar.Services;

/// <summary>
/// Restarts PulseBar as administrator (for CPU temperature). Starts a new elevated instance with
/// <see cref="Arg"/> through "runas"; that instance asks this one to quit (single instance replace) and
/// only then takes over, so the old one keeps running until the new one has actually started.
/// </summary>
public static class AdminRestart
{
    /// <summary>Tells the new instance to replace the running one and open the popup.</summary>
    public const string Arg = "--elevated-restart";

    private const int ErrorCancelled = 1223;

    /// <returns>Ok when the elevated instance started; Cancelled when UAC was declined.</returns>
    public static StartupApplyOutcome TryStart()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(AppPaths.ExePath, Arg)
            {
                UseShellExecute = true,
                Verb = "runas",
            });

            Log.Info("Restart as admin: elevated instance started");
            return process is null ? StartupApplyOutcome.Failed : StartupApplyOutcome.Ok;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("Restart as admin: UAC was declined; nothing changed");
            return StartupApplyOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Restart as admin", ex);
            return StartupApplyOutcome.Failed;
        }
    }
}
