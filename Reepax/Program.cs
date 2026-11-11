using Reepax.Services.Update;
using Velopack;

namespace Reepax;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var location = ReepaxPackageLocator.Instance;
        if (args.Length == 1 && args[0] == "--installer-cleanup")
        {
            ManagedInstallation.RemoveManagedFiles(location);
            return;
        }
        VelopackApp.Build().SetArgs(args).SetLocator(location).SetAutoApplyOnStartup(false)
            .OnAfterUpdateFastCallback(_ => ManagedInstallation.RefreshRegistration(location, AppUpdateService.AppCurrentVersion))
            .Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
