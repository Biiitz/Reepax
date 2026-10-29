using System.IO;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

[Collection("SharedQueue")]
public sealed class RecoveredPathCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RecoveryCleanup_" + Guid.NewGuid().ToString("N"));
    private readonly MainViewModel _model = new();

    public RecoveredPathCleanupTests()
    {
        Directory.CreateDirectory(_root);
        _model.Packages.Clear();
        _model.RecoveredPathConfirmationOverride = _ => false;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ForcedRemoval_DoesNotTreatRecoveredNamesAsFileOwnership(bool refreshFirst, bool sameFolderName)
    {
        var (package, foreignFile) = CreateMissingPackage(sameFolderName);
        if (refreshFirst) Assert.True(package.RefreshAndCheckExistsOnDisk());

        await _model.RemovePackage(package, true);

        Assert.True(File.Exists(foreignFile), "An unconfirmed recovered path must not authorize deletion.");
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
        Assert.Contains(package, _model.Packages);
    }

    [Fact]
    public async Task ItemRemoval_DoesNotDeleteAFileRetargetedByAutomaticRecovery()
    {
        var (package, foreignFile) = CreateMissingPackage(false);
        Assert.True(package.RefreshAndCheckExistsOnDisk());

        await _model.RemoveItem(package.Items[0], true);

        Assert.True(File.Exists(foreignFile), "A recovered item path must be confirmed before deletion.");
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
        Assert.Single(package.Items);
    }

    [Fact]
    public void DirectCleanup_DoesNotDeleteAHeuristicallyDiscoveredFile()
    {
        var (package, foreignFile) = CreateMissingPackage(false);

        Assert.Throws<InvalidOperationException>(() => MainViewModel.DeletePackageFilesFromDisk(package, _model.Packages));

        Assert.True(File.Exists(foreignFile), "Direct cleanup must preserve unconfirmed recovered files.");
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_RequiresASeparatePathConfirmationForDeletionAndRecycling(bool recycle)
    {
        var (package, foreignFile) = CreateMissingPackage(false);
        _model.CleanupConfirmationOverride = _ => (true, true, recycle);
        bool asked = false;
        _model.RecoveredPathConfirmationOverride = paths =>
        {
            asked = true;
            Assert.Equal(Path.GetDirectoryName(foreignFile), Assert.Single(paths));
            return false;
        };
        _model.RecyclePaths = _ => throw new InvalidOperationException("Recycling was not authorized.");

        await _model.RemovePackage(package);

        Assert.True(asked);
        Assert.True(package.RequiresRecoveredPathConfirmation);
        Assert.Contains(package, _model.Packages);
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
        Assert.Equal(Loc.Get("Status_CleanupUnconfirmedPath"), _model.StatusSummary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedRecovery_StillAllowsDeletingAndRecyclingAMovedPackage(bool recycle)
    {
        var (package, file) = CreateMissingPackage(true);
        _model.CleanupConfirmationOverride = _ => (true, true, recycle);
        int confirmations = 0;
        _model.RecoveredPathConfirmationOverride = paths =>
        {
            Assert.Equal(Path.GetDirectoryName(file), Assert.Single(paths));
            Assert.True(File.Exists(file));
            confirmations++;
            return true;
        };
        string bin = Path.Combine(_root, "Recycled");
        _model.RecyclePaths = paths => Directory.Move(Assert.Single(paths), bin);

        await _model.RemovePackage(package);

        Assert.Equal(1, confirmations);
        Assert.DoesNotContain(package, _model.Packages);
        Assert.False(File.Exists(file));
        if (recycle) Assert.Equal("unrelated content", File.ReadAllText(Path.Combine(bin, "data.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListOnlyOrCancelledRemoval_PreservesRecoveredFilesWithoutRequestingPathApproval(bool confirmed)
    {
        var (package, foreignFile) = CreateMissingPackage(false);
        _model.CleanupConfirmationOverride = _ => (confirmed, false, false);
        _model.RecoveredPathConfirmationOverride = _ => throw new InvalidOperationException("No file operation was requested.");

        await _model.RemovePackage(package);

        Assert.Equal(!confirmed, _model.Packages.Contains(package));
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
    }

    [Fact]
    public async Task MultipleSelection_RejectsTheWholeCleanupWhenRecoveredLocationsAreNotConfirmed()
    {
        var (recovered, foreignFile) = CreateMissingPackage(false);
        string directory = Path.Combine(_root, "AuthorizedFolder");
        Directory.CreateDirectory(directory);
        string ownedFile = Path.Combine(directory, "owned.bin");
        File.WriteAllText(ownedFile, "owned content");
        var known = new DownloadPackage { Name = "AuthorizedFolder", SaveDirectory = directory, AutoPar2Repair = false };
        known.Items.Add(new DownloadItem { FileName = "owned.bin", SaveFilePath = ownedFile });
        _model.Packages.Add(known);
        known.IsSelected = recovered.IsSelected = true;
        _model.CleanupConfirmationOverride = _ => (true, true, false);

        await _model.DeleteSelected();

        Assert.Contains(known, _model.Packages);
        Assert.Contains(recovered, _model.Packages);
        Assert.Equal("owned content", File.ReadAllText(ownedFile));
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
    }

    [Fact]
    public async Task PathChangeAfterConfirmation_DoesNotAuthorizeTheNewLocation()
    {
        var (package, foreignFile) = CreateMissingPackage(false);
        string replacement = Path.Combine(_root, "ReplacementFolder");
        Directory.CreateDirectory(replacement);
        string replacementFile = Path.Combine(replacement, "data.bin");
        File.WriteAllText(replacementFile, "replacement content");
        _model.CleanupConfirmationOverride = _ => (true, true, false);
        _model.RecoveredPathConfirmationOverride = _ =>
        {
            package.SaveDirectory = replacement;
            package.UpdateItemSaveFilePaths();
            return true;
        };

        await _model.RemovePackage(package);

        Assert.Contains(package, _model.Packages);
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
        Assert.Equal("replacement content", File.ReadAllText(replacementFile));
        Assert.Equal(Loc.Get("Status_CleanupChanged"), _model.StatusSummary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryConfirmationState_SurvivesSavingAndReloading(bool history)
    {
        var (package, foreignFile) = CreateMissingPackage(false);
        Assert.True(package.RefreshAndCheckExistsOnDisk());
        using var persistence = new DownloadPersistenceService(Path.Combine(_root, "State", "downloads.json"));
        if (history) persistence.SaveHistory([package], sync: true);
        else persistence.SaveDownloads([package], sync: true);
        var restored = Assert.Single(history ? persistence.LoadHistory() : persistence.LoadDownloads());
        _model.Packages.Clear();
        _model.Packages.Add(restored);

        await _model.RemovePackage(restored, true);

        Assert.True(restored.RequiresRecoveredPathConfirmation);
        Assert.Contains(restored, _model.Packages);
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
    }

    [Fact]
    public async Task MultipleMatchingFileNames_StillDoNotProveOwnership()
    {
        var (package, foreignFile) = CreateMissingPackage(false);
        string missing = package.SaveDirectory;
        string additional = Path.Combine(Path.GetDirectoryName(foreignFile)!, "other.bin");
        File.WriteAllText(additional, "other content");
        package.Items.Add(new DownloadItem { FileName = "other.bin", SaveFilePath = Path.Combine(missing, "other.bin") });

        await _model.RemovePackage(package, true);

        Assert.Contains(package, _model.Packages);
        Assert.Equal("unrelated content", File.ReadAllText(foreignFile));
        Assert.Equal("other content", File.ReadAllText(additional));
    }

    [Fact]
    public async Task ConfirmedItemRemoval_PreservesTheOtherRecoveredFiles()
    {
        var (package, selectedFile) = CreateMissingPackage(false);
        string missing = package.SaveDirectory;
        string keptFile = Path.Combine(Path.GetDirectoryName(selectedFile)!, "kept.bin");
        File.WriteAllText(keptFile, "kept content");
        package.Items.Add(new DownloadItem { FileName = "kept.bin", SaveFilePath = Path.Combine(missing, "kept.bin") });
        Assert.True(package.RefreshAndCheckExistsOnDisk());
        var selected = package.Items[0];
        _model.CleanupConfirmationOverride = _ => (true, true, false);
        _model.RecoveredPathConfirmationOverride = paths => paths.Count == 1 && paths[0] == package.SaveDirectory;

        await _model.RemoveItem(selected);

        Assert.Contains(package, _model.Packages);
        Assert.DoesNotContain(selected, package.Items);
        Assert.False(File.Exists(selectedFile));
        Assert.Equal("kept content", File.ReadAllText(keptFile));
        Assert.False(package.RequiresRecoveredPathConfirmation);
    }

    private (DownloadPackage Package, string ForeignFile) CreateMissingPackage(bool sameFolderName)
    {
        string missing = Path.Combine(_root, "ExpectedFolder");
        string unrelated = Path.Combine(_root, sameFolderName ? "ExamplePackage" : "OtherFolder");
        Directory.CreateDirectory(unrelated);
        string foreignFile = Path.Combine(unrelated, "data.bin");
        File.WriteAllText(foreignFile, "unrelated content");
        var package = new DownloadPackage { Name = "ExamplePackage", SaveDirectory = missing, AutoPar2Repair = false };
        package.Items.Add(new DownloadItem { FileName = "data.bin", SaveFilePath = Path.Combine(missing, "data.bin") });
        _model.Packages.Add(package);
        return (package, foreignFile);
    }

    public void Dispose()
    {
        _model.Packages.Clear();
        Directory.Delete(_root, true);
    }
}
