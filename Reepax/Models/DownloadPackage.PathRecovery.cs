namespace Reepax.Models;

public partial class DownloadPackage
{
    private bool _requiresRecoveredPathConfirmation;

    public bool RequiresRecoveredPathConfirmation
    {
        get => _requiresRecoveredPathConfirmation;
        set => SetProperty(ref _requiresRecoveredPathConfirmation, value);
    }

    private void ApplyRecoveredSaveDirectory(string directory, bool persistChanges)
    {
        RequiresRecoveredPathConfirmation = true;
        SaveDirectory = directory;
        UpdateItemSaveFilePaths();
        if (persistChanges)
            Services.Storage.DownloadPersistenceService.Instance.RequestSave();
    }
}
