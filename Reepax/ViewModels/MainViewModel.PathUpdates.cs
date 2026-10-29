using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Reepax.Models;
using Reepax.Services.Storage;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    internal void RenamePackageWhenAvailable(DownloadPackage package, string name)
    {
        RunWhenCleanupPathsAvailable(() =>
        {
            if (Packages.Contains(package))
                package.Rename(name, isUserAction: true);
        });
    }

    internal void RenameItemWhenAvailable(DownloadItem item, string name)
    {
        RunWhenCleanupPathsAvailable(() =>
        {
            if (Packages.Any(package => package.Items.Contains(item)))
                item.Rename(name, isUserAction: true);
        });
    }

    private static void RunWhenCleanupPathsAvailable(Action action)
    {
        if (PackagePathOperationGate.Instance.TryEnter(out var lease))
        {
            using (lease)
                action();
        }
        else
        {
            _ = RunWhenCleanupPathsAvailableAsync(action);
        }
    }

    private static async Task RunWhenCleanupPathsAvailableAsync(Action action)
    {
        try
        {
            using var lease = await PackagePathOperationGate.Instance.EnterAsync().ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (!DownloadPersistenceService.IsTestEnvironment && dispatcher != null && !dispatcher.CheckAccess())
                await dispatcher.InvokeAsync(action);
            else
                action();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[MainViewModel] Could not update downloads: {ex.Message}");
        }
    }
}
