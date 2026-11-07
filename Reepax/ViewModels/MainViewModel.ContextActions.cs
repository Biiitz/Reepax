using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.Verification;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    private bool _isChangingDownloadPaths;
    internal Action<Uri> DefaultBrowserLauncher { get; set; } = uri =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    internal DownloadItem[] GetItemsForContextAction(DownloadItem item)
    {
        if (!Packages.Any(package => package.Items.Contains(item))) return Array.Empty<DownloadItem>();
        return item.IsSelected
            ? Packages.SelectMany(package => package.Items).Where(entry => entry.IsSelected).Distinct().ToArray()
            : new[] { item };
    }

    internal DownloadPackage[] GetPackagesForContextAction(DownloadPackage package)
    {
        if (!Packages.Contains(package)) return Array.Empty<DownloadPackage>();
        return package.IsSelected ? Packages.Where(entry => entry.IsSelected).ToArray() : new[] { package };
    }

    internal void OpenItemsInDefaultBrowser(DownloadItem item)
    {
        var urls = GetItemsForContextAction(item).Select(GetBrowserUri).Where(uri => uri != null).ToArray();
        int opened = 0;
        foreach (var uri in urls)
        {
            try { DefaultBrowserLauncher(uri!); opened++; }
            catch (Exception) { AppLogger.Warn("[MainViewModel] Could not open a link in the default browser."); }
        }
        StatusSummary = urls.Length == 0 ? Loc.Get("Status_NoBrowserLinks")
            : Loc.Format("Status_BrowserLinksOpened", opened, urls.Length);
    }

    private static Uri? GetBrowserUri(DownloadItem item)
    {
        foreach (var value in new[] { item.OriginalUrl, item.DirectDownloadUrl })
        {
            if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return uri;
        }
        return null;
    }

    internal bool CanChangeDownloadPaths(DownloadPackage[] packages) => !_isChangingDownloadPaths && !IsBusy &&
        packages.Length > 0 && packages.All(package => Packages.Contains(package) &&
            !IsPackageBusyForPathChange(package) &&
            package.ItemsSnapshot.All(item => !item.IsUnavailableForProcessing));

    private bool IsPackageBusyForPathChange(DownloadPackage package) => package.IsExtracting ||
        ArchiveExtractionService.Instance.IsPackageExtracting(package.Id) ||
        Par2RepairService.Instance.GetActiveRepairs().ContainsKey(package.Id) ||
        package.NextTaskSteps.Any(step => step.State is NextTaskStepState.Running or NextTaskStepState.Transitioning) ||
        package.ItemsSnapshot.Any(item => item.IsVerifyingIntegrity || DownloadEngine.Instance.IsDownloading(item.Id) ||
            _queueManager.IsItemActive(item));

    internal async Task<bool> ChangeDownloadPathsAsync(DownloadPackage[] packages, string destination)
    {
        packages = packages.Distinct().ToArray();
        if (!CanChangeDownloadPaths(packages))
        {
            StatusSummary = Loc.Get("Status_DownloadPathBusy");
            RenameFailureNotification(StatusSummary);
            return false;
        }
        _isChangingDownloadPaths = true;
        IsBusy = true;
        BusyMessage = Loc.Get("Status_ChangingDownloadPath");
        DownloadItem[] items = Array.Empty<DownloadItem>();
        int moved = 0;
        bool cleaned = true;
        try
        {
            using var lease = await PackagePathOperationGate.Instance.EnterAsync();
            if (packages.Any(package => !Packages.Contains(package)))
                throw new InvalidOperationException("The selected packages are no longer available.");
            items = packages.SelectMany(package => package.ItemsSnapshot).Distinct().ToArray();
            if (items.Any(item => item.IsUnavailableForProcessing))
                throw new InvalidOperationException("The selected downloads are no longer available.");
            foreach (var item in items) item.SetPathChangePending(true);
            if (packages.Any(IsPackageBusyForPathChange))
                throw new InvalidOperationException("A selected package is being processed.");
            if (!ConfirmRecoveredPathsForMove(packages)) return false;
            var allPackages = Packages.ToArray();
            var plans = await Task.Run(() => PackageDirectoryMoveService.Prepare(packages, allPackages, destination));
            foreach (var plan in plans)
            {
                cleaned &= await ExecuteDownloadPathPlanAsync(plan);
                moved++;
            }
            StatusSummary = Loc.Format(cleaned ? "Status_DownloadPathsChanged" : "Status_DownloadPathsCleanupIncomplete", moved);
            if (!cleaned) RenameFailureNotification(StatusSummary);
            return true;
        }
        catch (Exception)
        {
            AppLogger.Warn("[MainViewModel] Could not relocate all selected downloads.");
            StatusSummary = Loc.Format("Status_DownloadPathFailed", moved, packages.Length);
            RenameFailureNotification(StatusSummary);
            return false;
        }
        finally
        {
            foreach (var item in items) item.SetPathChangePending(false);
            _isChangingDownloadPaths = false;
            IsBusy = false;
            BusyMessage = string.Empty;
            _queueManager.ProcessQueue();
        }
    }

    private bool ConfirmRecoveredPathsForMove(DownloadPackage[] packages)
    {
        var recovered = packages.Where(package => package.RequiresRecoveredPathConfirmation).ToArray();
        if (recovered.Length == 0) return true;
        IReadOnlyList<string> paths = recovered.Select(package => package.SaveDirectory).ToArray();
        var confirmed = RecoveredPathConfirmationOverride?.Invoke(paths) ??
            (!DownloadPersistenceService.IsTestEnvironment && Views.ConfirmDialog.Show(
                Loc.Get("Dialog_ConfirmRecoveredPaths_Title"),
                Loc.Format("Dialog_ConfirmRecoveredMove_Message", string.Join(Environment.NewLine, paths)), isDanger: true));
        if (!confirmed) return false;
        foreach (var package in recovered) package.RequiresRecoveredPathConfirmation = false;
        return true;
    }

    private static async Task<bool> ExecuteDownloadPathPlanAsync(PackageDirectoryMoveService.Plan plan)
    {
        var cleaned = await Task.Run(() => new PackageDirectoryMoveService().ExecuteAsync(plan));
        plan.Package.SaveDirectory = plan.Target;
        foreach (var entry in plan.ItemPaths) entry.Key.SaveFilePath = entry.Value;
        plan.Package.ExtractionDirectory = plan.ExtractionDirectory;
        DownloadPersistenceService.Instance.RequestSave();
        return cleaned;
    }
}
