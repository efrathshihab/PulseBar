using PulseBar.Services;
using Velopack;

namespace PulseBar;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must be first: Velopack's install/update/uninstall hooks run here and then exit the process,
        // before the single-instance mutex, settings or any window exist.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(InstallHooks.AfterInstall)
            .OnAfterUpdateFastCallback(InstallHooks.AfterUpdate)
            .OnBeforeUninstallFastCallback(InstallHooks.BeforeUninstall)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
