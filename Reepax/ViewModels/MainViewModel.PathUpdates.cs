using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Reepax.Models;
using Reepax.Services.Storage;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    internal Action<string> RenameFailureNotification { get; set; } = message =>
    {
        if (!DownloadPersistenceService.IsTestEnvironment)
            SafeInvoke(() => MessageBox.Show(message, Services.Localization.Loc.Get("Dialog_ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning));
    };

    internal void RenamePackageWhenAvailable(DownloadPackage package, string name)
    {
        RunWhenCleanupPathsAvailable(() =>
        {
            if (Packages.Contains(package))
                TryRenamePackage(package, name);
        });
    }

    private bool TryRenamePackage(DownloadPackage package, string name)
    {
        if (package.Rename(name, isUserAction: true))
            return true;
        StatusSummary = Services.Localization.Loc.Get("Status_PackageRenameFailed");
        RenameFailureNotification(StatusSummary);
        return false;
    }

    internal void RenameItemWhenAvailable(DownloadItem item, string name)
    {
        RunWhenCleanupPathsAvailable(() =>
        {
            var package = Packages.FirstOrDefault(package => package.Items.Contains(item));
            if (package == null)
                return;
            bool busy = item.IsFileRenameBusy || package.IsExtracting ||
                Services.Extractor.ArchiveExtractionService.Instance.IsPackageExtracting(package.Id) ||
                Services.Verification.Par2RepairService.Instance.GetActiveRepairs().ContainsKey(package.Id) ||
                package.NextTaskSteps.Any(step => step.State is NextTaskStepState.Running or NextTaskStepState.Transitioning);
            if (!busy && item.Rename(name, isUserAction: true))
                return;
            StatusSummary = Services.Localization.Loc.Get(busy ? "Status_ItemRenameBusy" : "Status_ItemRenameFailed");
            RenameFailureNotification(StatusSummary);
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
            AppLogger.Warn($"[MainViewModel] Could not update downloads", ex);
        }
    }
}
