using Reepax.Models;
using Reepax.Services.Localization;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    internal Func<IReadOnlyList<string>, bool>? RecoveredPathConfirmationOverride { get; set; }

    private bool ConfirmRecoveredCleanupPaths(CleanupSnapshot snapshot)
    {
        var directories = snapshot.GetUnconfirmedPackages().Select(package => package.SaveDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (RecoveredPathConfirmationOverride != null)
            return RecoveredPathConfirmationOverride(directories);
        return Views.ConfirmDialog.Show(Loc.Get("Dialog_ConfirmRecoveredPaths_Title"),
            Loc.Format("Dialog_ConfirmRecoveredPaths_Message", string.Join(Environment.NewLine, directories)),
            isDanger: true);
    }

    private sealed partial class CleanupSnapshot
    {
        internal IEnumerable<DownloadPackage> GetUnconfirmedPackages() => Packages.Where(package =>
            package.RequiresRecoveredPathConfirmation &&
            (_packageIds.Contains(package.Id) || package.Items.Any(item => _itemIds.Contains(item.Id))));

        internal bool HasUnconfirmedPaths => GetUnconfirmedPackages().Any();

        internal void ConfirmRecoveredPaths()
        {
            foreach (var package in GetUnconfirmedPackages())
                package.RequiresRecoveredPathConfirmation = false;
        }

        private void EnsureRecoveredPathsConfirmed()
        {
            if (HasUnconfirmedPaths)
                throw new InvalidOperationException("Recovered package paths require explicit confirmation before cleanup.");
        }
    }
}
