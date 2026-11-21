using PulseBar.Models;
using PulseBar.Utilities;
using Velopack;

namespace PulseBar.Services;

/// <summary>
/// Velopack install/update/uninstall hooks. Each runs in a short-lived process started by Update.exe,
/// which then exits (Velopack kills it after 30 seconds), so nothing here shows UI or asks for UAC.
/// None of them ever deletes the user's data folder (%LocalAppData%\PulseBar).
/// </summary>
public static class InstallHooks
{
    public static void AfterInstall(SemanticVersion version)
    {
        // Setup.exe creates the Start menu shortcut itself (vpk pack --shortcuts)
        Log.Info($"Installed {version}");
    }

    /// <summary>The login entry should still start current\PulseBar.exe; fix it if it doesn't.</summary>
    public static void AfterUpdate(SemanticVersion version)
    {
        try
        {
            Log.Info($"Updated to {version}");
            if (AppPaths.InstalledExePath is not { } exe)
            {
                Log.Error("After update: not running from the install's current folder; login entry not checked");
                return;
            }

            // Read only: the hook must not rewrite the user's settings
            var settings = new AppSettings();
            new SettingsStore().Load(settings);
            if (settings.LaunchAtLogin != LaunchAtLogin.Off &&
                new StartupManager(exe).Verify(settings.LaunchAtLogin) is { } issue)
            {
                Log.Error($"After update: {issue}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("After update hook", ex);
        }
    }

    /// <summary>Removes the Run key value and the "PulseBar" task. User data is kept.</summary>
    public static void BeforeUninstall(SemanticVersion version)
    {
        try
        {
            Log.Info($"Uninstalling {version}");
            new StartupManager(AppPaths.ExePath).RemoveForUninstall();
        }
        catch (Exception ex)
        {
            Log.Error("Before uninstall hook", ex);
        }
    }
}
