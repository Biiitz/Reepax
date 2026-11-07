using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class PackageEditingPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("PackageEditing_").FullName;
    private readonly MainViewModel _model = new();
    private readonly DownloadPackage[] _previous;

    public PackageEditingPathTests()
    {
        _previous = _model.Packages.ToArray();
        _model.Packages.Clear();
        _model.RenameFailureNotification = _ => { };
    }

    [Theory]
    [InlineData("name", false)]
    [InlineData("path", false)]
    [InlineData("both", false)]
    [InlineData("name", true)]
    [InlineData("path", true)]
    [InlineData("both", true)]
    public async Task Edit_MovesDataOnlyToTheEffectiveDestinationAndPreservesDownloadState(string mode, bool shared)
    {
        var package = CreatePackage(shared);
        string source = package.SaveDirectory;
        string name = mode == "path" ? package.Name : "Renamed";
        string directory = mode == "name" ? source : Path.Combine(_root, "ChosenDestination");
        string expected = mode == "name" ? shared ? source : Path.Combine(_root, name) : directory;
        var items = package.Items.ToArray();
        var files = SnapshotFiles(source);
        var foreign = _model.Packages.Skip(1).SingleOrDefault();
        var originalIds = items.Select(item => item.Id).ToArray();

        await EditAsync(package, name, directory);

        Assert.Equal(name, package.Name);
        Assert.Equal(expected, package.SaveDirectory);
        Assert.True(package.AutoExtractArchives);
        Assert.True(package.LowResourceExtraction);
        Assert.True(package.DeleteArchiveAfterExtraction);
        Assert.True(package.MoveArchiveToRecycleBin);
        Assert.Equal(originalIds, package.Items.Select(item => item.Id));
        Assert.Equal(DownloadStatus.Completed, items[0].Status);
        Assert.Equal(DownloadStatus.Paused, items[1].Status);
        Assert.Equal(3, items[1].DownloadedBytes);
        Assert.Equal(items[0].ExpectedChecksum, items[0].CalculatedChecksum);
        foreach (var item in items) Assert.Equal(Path.Combine(expected, item.FileName), item.SaveFilePath);
        foreach (var file in files.Where(file => !shared || file.Key != "foreign.bin"))
            Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(expected, file.Key)));
        if (foreign != null)
        {
            Assert.Equal(source, foreign.SaveDirectory);
            Assert.Equal(Path.Combine(source, "foreign.bin"), foreign.Items[0].SaveFilePath);
            Assert.Equal(files["foreign.bin"], File.ReadAllBytes(foreign.Items[0].SaveFilePath!));
            if (source != expected) Assert.False(File.Exists(Path.Combine(expected, "foreign.bin")));
        }
        if (source != expected)
            Assert.All(items, item => Assert.False(File.Exists(Path.Combine(source, item.FileName) + ".part")));
        Assert.Empty(Directory.GetDirectories(_root, ".download-move-*"));

        using var storage = new DownloadPersistenceService(Path.Combine(_root, "downloads.json"));
        storage.SaveDownloads(new[] { package }, sync: true);
        var restored = Assert.Single(storage.LoadDownloads());
        Assert.Equal(name, restored.Name);
        Assert.Equal(expected, restored.SaveDirectory);
        Assert.False(restored.RequiresRecoveredPathConfirmation);
        Assert.Equal(DownloadStatus.Completed, restored.Items[0].Status);
        Assert.Equal(DownloadStatus.Paused, restored.Items[1].Status);
        Assert.Equal(3, restored.Items[1].DownloadedBytes);
        for (int index = 0; index < items.Length; index++)
        {
            Assert.Equal(items[index].SaveFilePath, restored.Items[index].SaveFilePath);
            Assert.Equal(items[index].ExpectedChecksum, restored.Items[index].ExpectedChecksum);
            Assert.Equal(items[index].CalculatedChecksum, restored.Items[index].CalculatedChecksum);
        }
    }

    [Fact]
    public async Task OccupiedDestination_PreservesOriginalNameOptionsDataAndForeignTarget()
    {
        var package = CreatePackage(shared: false);
        string source = package.SaveDirectory;
        var files = SnapshotFiles(source);
        string target = Directory.CreateDirectory(Path.Combine(_root, "ChosenDestination")).FullName;
        byte[] foreign = new byte[] { 23, 29, 31 };
        File.WriteAllBytes(Path.Combine(target, "complete.bin"), foreign);

        await EditAsync(package, "Renamed", target);

        AssertUnchanged(package, source, files);
        Assert.Equal(foreign, File.ReadAllBytes(Path.Combine(target, "complete.bin")));
        Assert.Single(Directory.GetFiles(target));
        Assert.False(Directory.Exists(Path.Combine(_root, "Renamed")));
    }

    [Fact]
    public async Task ExistingEmptyDestination_CanBeSelectedAsTheExactPackageDirectory()
    {
        var package = CreatePackage(shared: false);
        string original = package.SaveDirectory;
        var files = SnapshotFiles(original);
        string target = Directory.CreateDirectory(Path.Combine(_root, "ChosenDestination")).FullName;

        await EditAsync(package, "Renamed", target);

        Assert.Equal("Renamed", package.Name);
        Assert.Equal(target, package.SaveDirectory);
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(target, file.Key)));
        Assert.False(Directory.Exists(original));
        Assert.False(Directory.Exists(Path.Combine(_root, "Renamed")));
    }

    [Fact]
    public async Task EmptyDestinationOccupiedAfterPlanning_DoesNotOverwriteTheNewForeignFile()
    {
        var package = CreatePackage(shared: false);
        string original = package.SaveDirectory;
        var files = SnapshotFiles(original);
        string target = Directory.CreateDirectory(Path.Combine(_root, "ChosenDestination")).FullName;
        var plan = Assert.Single(PackageDirectoryMoveService.Prepare(new[] { package }, _model.Packages.ToArray(),
            target, useExactDestination: true));
        byte[] foreign = new byte[] { 37, 41, 43 };
        File.WriteAllBytes(Path.Combine(target, "foreign.bin"), foreign);

        await Assert.ThrowsAsync<IOException>(() => new PackageDirectoryMoveService().ExecuteAsync(plan));

        AssertUnchanged(package, original, files);
        Assert.Equal(foreign, File.ReadAllBytes(Path.Combine(target, "foreign.bin")));
        Assert.Single(Directory.GetFiles(target));
    }

    [Fact]
    public async Task LockedPartialFile_LeavesTheEditUnappliedAndCanBeRetriedAfterUnlocking()
    {
        var package = CreatePackage(shared: true);
        string source = package.SaveDirectory;
        var files = SnapshotFiles(source);
        string target = Path.Combine(_root, "ChosenDestination");
        using (var locked = new FileStream(package.Items[1].SaveFilePath + ".part", FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            await EditAsync(package, "Renamed", target);
            Assert.Equal("SourceFolder", package.Name);
            Assert.Equal(source, package.SaveDirectory);
            Assert.False(package.AutoExtractArchives);
            Assert.False(package.IsCustomName);
            Assert.False(_model.IsBusy);
            Assert.All(package.Items, item => Assert.False(item.IsUnavailableForProcessing));
            Assert.False(Directory.Exists(target));
            Assert.Empty(Directory.GetDirectories(_root, ".download-move-*"));
        }
        AssertUnchanged(package, source, files);

        await EditAsync(package, "Renamed", target);

        Assert.Equal("Renamed", package.Name);
        Assert.Equal(target, package.SaveDirectory);
        foreach (var file in files.Where(file => file.Key != "foreign.bin"))
            Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(target, file.Key)));
        Assert.Equal(files["foreign.bin"], File.ReadAllBytes(Path.Combine(source, "foreign.bin")));
        Assert.False(_model.IsBusy);
        Assert.All(package.Items, item => Assert.False(item.IsUnavailableForProcessing));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("verifying")]
    [InlineData("reserved")]
    public async Task BusyDownload_EditKeepsItsOriginalDataAndExistingReservation(string state)
    {
        var package = CreatePackage(shared: false);
        string source = package.SaveDirectory;
        var files = SnapshotFiles(source);
        var item = package.Items[1];
        if (state == "active") item.Status = DownloadStatus.Downloading;
        if (state == "verifying") item.IsVerifyingIntegrity = true;
        if (state == "reserved") item.SetPathChangePending(true);
        try
        {
            await EditAsync(package, "Renamed", Path.Combine(_root, "ChosenDestination"));

            AssertUnchanged(package, source, files);
            Assert.Equal(state == "reserved", item.IsUnavailableForProcessing);
            Assert.Equal(state == "verifying", item.IsVerifyingIntegrity);
            Assert.Equal(state == "active" ? DownloadStatus.Downloading : DownloadStatus.Paused, item.Status);
            Assert.False(_model.IsBusy);
            Assert.False(Directory.Exists(Path.Combine(_root, "ChosenDestination")));
        }
        finally
        {
            item.SetPathChangePending(false);
            item.IsVerifyingIntegrity = false;
            item.Status = DownloadStatus.Paused;
        }
    }

    [Fact]
    public async Task RemovalWhileWaitingForPathGate_DoesNotApplyTheStaleEdit()
    {
        var package = CreatePackage(shared: false);
        string source = package.SaveDirectory;
        var files = SnapshotFiles(source);
        Task edit;
        Assert.True(PackagePathOperationGate.Instance.TryEnter(out var lease));
        using (lease)
        {
            edit = EditAsync(package, "Renamed", Path.Combine(_root, "ChosenDestination"));
            Assert.False(edit.IsCompleted);
            AssertUnchanged(package, source, files);
            _model.Packages.Remove(package);
        }
        await edit.WaitAsync(TimeSpan.FromSeconds(10));

        AssertUnchanged(package, source, files);
        Assert.False(_model.IsBusy);
        Assert.False(Directory.Exists(Path.Combine(_root, "ChosenDestination")));
    }

    [Fact]
    public async Task PendingPackage_StoresTheExplicitPathWithoutCreatingADirectory()
    {
        string source = Path.Combine(_root, "UncreatedSource");
        string target = Path.Combine(_root, "ChosenDestination");
        var package = new DownloadPackage { Name = "SourceFolder", SaveDirectory = source, AutoPar2Repair = false };
        package.Items.Add(new DownloadItem { OriginalUrl = "https://example.invalid/payload.bin", FileName = "payload.bin",
            SaveFilePath = Path.Combine(source, "payload.bin"), Status = DownloadStatus.Paused });
        _model.Packages.Add(package);

        await EditAsync(package, "Renamed", target);

        Assert.Equal("Renamed", package.Name);
        Assert.Equal(target, package.SaveDirectory);
        Assert.Equal(Path.Combine(target, "payload.bin"), package.Items[0].SaveFilePath);
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(Path.Combine(_root, "Renamed")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedDialogPath_DoesNotUndoAnInterveningDirectoryChange(bool alreadyChanged)
    {
        var package = CreatePackage(shared: false);
        string originalDirectory = package.SaveDirectory;
        var files = SnapshotFiles(originalDirectory);
        Task edit;
        Assert.True(PackagePathOperationGate.Instance.TryEnter(out var lease));
        using (lease)
        {
            if (alreadyChanged) Assert.True(package.Rename("Intermediate", isUserAction: false));
            edit = _model.EditPackageAsync(package,
                string.Join(Environment.NewLine, package.Items.Select(item => item.OriginalUrl)),
                "Renamed", originalDirectory, true, true, true, true,
                originalDownloadDirectory: alreadyChanged ? originalDirectory : null);
            Assert.False(edit.IsCompleted);
            if (!alreadyChanged) Assert.True(package.Rename("Intermediate", isUserAction: false));
        }
        await edit.WaitAsync(TimeSpan.FromSeconds(10));

        string expected = Path.Combine(_root, "Renamed");
        Assert.Equal("Renamed", package.Name);
        Assert.Equal(expected, package.SaveDirectory);
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(expected, file.Key)));
        Assert.False(Directory.Exists(originalDirectory));
        Assert.False(Directory.Exists(Path.Combine(_root, "Intermediate")));
    }

    [Fact]
    public async Task EditedPartialDownload_ResumesItsRealCheckpointAfterReloadAndProducesExactBytes()
    {
        var package = CreatePackage(shared: false);
        var item = package.Items[1];
        byte[] data = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
        await using var source = new PipelineHttpSource(new Dictionary<string, byte[]> { ["partial.bin"] = data },
            async (_, start, stream, body, token) =>
                await stream.WriteAsync(start == 0 ? body[..Math.Min(513, body.Length)] : body, token));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = 1 };
        int completed = 0;
        engine.DownloadCompleted += _ => Interlocked.Increment(ref completed);
        File.Delete(item.SaveFilePath + ".part");
        File.Delete(item.SaveFilePath + ".part.segments");
        item.DownloadedBytes = 0;
        item.TotalBytes = data.Length;
        item.OriginalUrl = source.Url("partial.bin");
        item.ExpectedChecksum = Convert.ToHexString(SHA256.HashData(data));
        await engine.StartDownloadAsync(item, item.OriginalUrl, null, null, null, null).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal(513, item.DownloadedBytes);
        Assert.Equal(0, completed);
        byte[] checkpoint = File.ReadAllBytes(item.SaveFilePath + ".part.segments");
        item.Status = DownloadStatus.Paused;
        string target = Path.Combine(_root, "ChosenDestination");

        await EditAsync(package, "Renamed", target);

        Assert.Equal(Path.Combine(target, "partial.bin"), item.SaveFilePath);
        Assert.Equal(data[..513], File.ReadAllBytes(item.SaveFilePath + ".part"));
        Assert.Equal(checkpoint, File.ReadAllBytes(item.SaveFilePath + ".part.segments"));
        using var storage = new DownloadPersistenceService(Path.Combine(_root, "downloads.json"));
        storage.SaveDownloads(new[] { package }, sync: true);
        var restored = Assert.Single(storage.LoadDownloads());
        var resumed = restored.Items[1];
        Assert.Equal(DownloadStatus.Paused, resumed.Status);
        Assert.Equal(513, resumed.DownloadedBytes);
        await engine.ResumeDownloadAsync(resumed).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(DownloadStatus.Completed, resumed.Status);
        Assert.Equal(1, completed);
        Assert.Equal(data, File.ReadAllBytes(resumed.SaveFilePath!));
        Assert.Equal(resumed.ExpectedChecksum, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(resumed.SaveFilePath!))));
        Assert.Contains(source.Ranges, request => request.File == "partial.bin" && request.Start == 513);
        Assert.Equal(new byte[] { 2, 3, 5, 7 }, File.ReadAllBytes(restored.Items[0].SaveFilePath!));
        Assert.False(File.Exists(resumed.SaveFilePath + ".part"));
        Assert.False(File.Exists(resumed.SaveFilePath + ".part.segments"));
    }

    private DownloadPackage CreatePackage(bool shared)
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "SourceFolder")).FullName;
        byte[] completed = new byte[] { 2, 3, 5, 7 };
        var package = new DownloadPackage { Name = "SourceFolder", SaveDirectory = source, AutoPar2Repair = false };
        package.AddItems(new[]
        {
            new DownloadItem { FileName = "complete.bin", SaveFilePath = Path.Combine(source, "complete.bin"),
                OriginalUrl = "https://example.invalid/complete.bin", Status = DownloadStatus.Completed,
                DownloadedBytes = completed.Length, TotalBytes = completed.Length,
                ExpectedChecksum = Convert.ToHexString(SHA256.HashData(completed)),
                CalculatedChecksum = Convert.ToHexString(SHA256.HashData(completed)) },
            new DownloadItem { FileName = "partial.bin", SaveFilePath = Path.Combine(source, "partial.bin"),
                OriginalUrl = "https://example.invalid/partial.bin", Status = DownloadStatus.Paused,
                DownloadedBytes = 3, TotalBytes = 10 }
        });
        File.WriteAllBytes(package.Items[0].SaveFilePath!, completed);
        File.WriteAllBytes(package.Items[1].SaveFilePath + ".part", new byte[] { 11, 13, 17 });
        File.WriteAllBytes(package.Items[1].SaveFilePath + ".part.segments", JsonSerializer.SerializeToUtf8Bytes(new
        {
            TotalBytes = 10, IsSequential = true, Segments = new[] { new { Start = 0, End = 9, Done = 3 } }
        }));
        _model.Packages.Add(package);
        if (shared)
        {
            string path = Path.Combine(source, "foreign.bin");
            File.WriteAllBytes(path, new byte[] { 19, 23 });
            var other = new DownloadPackage { Name = "Other", SaveDirectory = source, AutoPar2Repair = false };
            other.Items.Add(new DownloadItem { FileName = "foreign.bin", SaveFilePath = path, Status = DownloadStatus.Paused });
            _model.Packages.Add(other);
        }
        return package;
    }

    private Task EditAsync(DownloadPackage package, string name, string directory)
    {
        return _model.EditPackageAsync(package, string.Join(Environment.NewLine, package.Items.Select(item => item.OriginalUrl)),
            name, directory, true, true, true, true);
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string source) => Directory.GetFiles(source)
        .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);

    private static void AssertUnchanged(DownloadPackage package, string source, Dictionary<string, byte[]> files)
    {
        Assert.Equal("SourceFolder", package.Name);
        Assert.Equal(source, package.SaveDirectory);
        Assert.False(package.IsCustomName);
        Assert.False(package.AutoExtractArchives);
        Assert.False(package.LowResourceExtraction);
        Assert.False(package.DeleteArchiveAfterExtraction);
        Assert.False(package.MoveArchiveToRecycleBin);
        foreach (var item in package.Items) Assert.Equal(Path.Combine(source, item.FileName), item.SaveFilePath);
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(source, file.Key)));
    }

    public void Dispose()
    {
        _model.Packages.Clear();
        foreach (var package in _previous) _model.Packages.Add(package);
        Directory.Delete(_root, recursive: true);
    }
}
