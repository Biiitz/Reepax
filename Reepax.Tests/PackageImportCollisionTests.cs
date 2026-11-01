using System.IO;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class PackageImportCollisionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ImportCollision_");
    private readonly string _previousDirectory = SettingsService.Instance.Settings.DefaultDownloadDirectory;
    private readonly string _previousLanguage = LocalizationService.Instance.CurrentLanguage;
    private readonly DownloadPackage[] _previousPackages = QueueManager.Instance.Packages.ToArray();

    public PackageImportCollisionTests()
    {
        DownloadPersistenceService.IsTestEnvironment = true;
        SettingsService.Instance.Settings.DefaultDownloadDirectory = _root.FullName;
        QueueManager.Instance.Packages.Clear();
    }

    private static string Json(params string[] names) => JsonSerializer.Serialize(new ReepaxPackageDto
    {
        Name = "Archive", SubDirectory = "Archive", AutoPar2Repair = false,
        Items = names.Select((name, index) => new ReepaxPackageItemDto
        {
            FileName = name, OriginalUrl = $"https://example.invalid/file/{index}", TotalBytes = 100, HosterName = "Source"
        }).ToList()
    });

    [Theory]
    [InlineData("payload.bin", "payload.bin")]
    [InlineData("payload.bin", "PAYLOAD.BIN")]
    [InlineData("payload?.bin", "payload*.bin")]
    [InlineData("payload.bin", "payload.bin.part")]
    [InlineData("payload.bin", "payload.bin.part.segments")]
    [InlineData("payload.bin.part", "payload.bin")]
    public void Import_RejectsOverlappingFinalAndSidecarTargets(string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => PackageExportImportService.ImportFromJson(Json(first, second)));
        Assert.False(Directory.Exists(Path.Combine(_root.FullName, "Archive")));
        Assert.Empty(QueueManager.Instance.Packages);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ConflictingImport_IsRejectedAtomicallyWithLocalizedMessage(string language)
    {
        LocalizationService.Instance.CurrentLanguage = language;
        var model = new MainViewModel();
        model.Packages.Clear();
        string json = Json("payload.bin", "PAYLOAD.BIN");
        var error = Assert.Throws<InvalidDataException>(() => PackageExportImportService.ImportFromJson(json));
        Assert.Equal(Loc.Get("Import_OverlappingTargets"), error.Message);
        Assert.Null(await PackageExportImportService.ImportAndAddAsync(WritePackage(json), model));
        Assert.Equal(Loc.Format("Dialog_ImportErrorMessage", error.Message), model.StatusSummary);
        Assert.Empty(model.Packages);
        Assert.False(Directory.Exists(Path.Combine(_root.FullName, "Archive")));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData(".part", false)]
    [InlineData(".part", true)]
    [InlineData(".part.segments", false)]
    [InlineData(".part.segments", true)]
    public async Task ExistingDiskTarget_IsPreservedAndPreventsImport(string suffix, bool directory)
    {
        var model = new MainViewModel();
        model.Packages.Clear();
        string target = Path.Combine(_root.FullName, "Archive", "payload.bin" + suffix);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (directory) Directory.CreateDirectory(target);
        string file = directory ? Path.Combine(target, "original.bin") : target;
        byte[] original = { 9, 8, 7, 6 };
        File.WriteAllBytes(file, original);
        Assert.Null(await PackageExportImportService.ImportAndAddAsync(WritePackage(Json("payload.bin")), model));
        Assert.Empty(model.Packages);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Contains(Loc.Get("Import_OccupiedTargets"), model.StatusSummary);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData(".part", false)]
    [InlineData(".part", true)]
    [InlineData(".part.segments", false)]
    [InlineData(".part.segments", true)]
    public async Task RegisteredTargets_ReserveFinalAndWorkingPathsWithoutFiles(string suffix, bool fallbackPath)
    {
        var model = new MainViewModel();
        model.Packages.Clear();
        var existing = new DownloadPackage
        {
            Name = "Existing", IsCustomName = true, AutoPar2Repair = false,
            SaveDirectory = Path.Combine(_root.FullName, "Archive")
        };
        var item = new DownloadItem
        {
            FileName = "PAYLOAD.BIN" + suffix,
            SaveFilePath = fallbackPath ? null : Path.Combine(existing.SaveDirectory, "PAYLOAD.BIN" + suffix),
            Status = DownloadStatus.Completed, TotalBytes = 100, DownloadedBytes = 100, IsEnabled = !fallbackPath
        };
        existing.Items.Add(item);
        model.Packages.Add(existing);
        Assert.Null(await PackageExportImportService.ImportAndAddAsync(WritePackage(Json("payload.bin")), model));
        Assert.Same(existing, Assert.Single(model.Packages));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(100, item.DownloadedBytes);
        Assert.False(Directory.Exists(existing.SaveDirectory));
        model.Packages.Clear();
    }

    [Fact]
    public async Task ConcurrentImports_OnlyOneCanReserveTheSameTargets()
    {
        var model = new MainViewModel();
        model.Packages.Clear();
        string path = WritePackage(Json("archive.part1.rar", "archive.part2.rar"));
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PackageExportImportService.ImportAndAddAsync(path, model)));
        Assert.Single(results.Where(package => package != null));
        var added = Assert.Single(model.Packages);
        Assert.Equal(new[] { "archive.part1.rar", "archive.part2.rar" }, added.Items.Select(item => item.FileName));
        Assert.False(Directory.Exists(added.SaveDirectory));
        model.Packages.Clear();
    }

    [Fact]
    public async Task NonOverlappingImports_KeepArchiveNamesAndAllowSharedDirectories()
    {
        var model = new MainViewModel();
        model.Packages.Clear();
        var first = await PackageExportImportService.ImportAndAddAsync(WritePackage(Json("archive.part01.rar", "archive.part02.rar")), model);
        var second = await PackageExportImportService.ImportAndAddAsync(WritePackage(Json("other.7z.001", "other.7z.002")), model);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.SaveDirectory, second.SaveDirectory);
        Assert.Equal(new[] { "archive.part01.rar", "archive.part02.rar" }, first.Items.Select(item => item.FileName));
        Assert.Equal(new[] { "other.7z.001", "other.7z.002" }, second.Items.Select(item => item.FileName));
        Assert.All(model.Packages.SelectMany(package => package.Items), item =>
        {
            Assert.Equal(DownloadStatus.Queued, item.Status);
            Assert.Equal(0, item.DownloadedBytes);
            Assert.Equal(100, item.TotalBytes);
        });
        Assert.False(Directory.Exists(first.SaveDirectory));
        model.Packages.Clear();
    }

    [Fact]
    public async Task Import_RechecksReservationsAfterWaitingForPathOperations()
    {
        var model = new MainViewModel();
        model.Packages.Clear();
        var reserved = PackageExportImportService.ImportFromJson(Json("payload.bin"));
        using var lease = await PackagePathOperationGate.Instance.EnterAsync();
        Task<DownloadPackage?> importing = PackageExportImportService.ImportAndAddAsync(WritePackage(Json("payload.bin")), model);
        model.Packages.Add(reserved);
        lease.Dispose();
        Assert.Null(await importing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(reserved, Assert.Single(model.Packages));
        model.Packages.Clear();
    }

    [Fact]
    public async Task BlockedPackageDirectory_IsRejectedWithoutChangingItsContents()
    {
        var model = new MainViewModel();
        model.Packages.Clear();
        string blocker = Path.Combine(_root.FullName, "Archive");
        File.WriteAllText(blocker, "existing user file");
        Assert.Null(await PackageExportImportService.ImportAndAddAsync(WritePackage(Json("payload.bin")), model));
        Assert.Equal("existing user file", File.ReadAllText(blocker));
        Assert.Empty(model.Packages);
        Assert.Contains(Loc.Get("Import_UnsafeDirectory"), model.StatusSummary);
    }

    [Fact]
    public async Task LegacyImport_PreservesDifferentPartNamesAndDisabledItems()
    {
        var model = new MainViewModel();
        model.Packages.Clear();
        var dto = JsonSerializer.Deserialize<ReepaxPackageDto>(Json("archive.part01.rar", "archive.part02.rar"))!;
        dto.Format = "sdlr";
        dto.Items[1].IsEnabled = false;
        var imported = await PackageExportImportService.ImportAndAddAsync(WritePackage(JsonSerializer.Serialize(dto)), model);
        Assert.NotNull(imported);
        Assert.True(imported.Items[0].IsEnabled);
        Assert.False(imported.Items[1].IsEnabled);
        Assert.Equal("archive.part02.rar", imported.Items[1].FileName);
        Assert.EndsWith("archive.part02.rar", imported.Items[1].SaveFilePath);
        Assert.False(Directory.Exists(imported.SaveDirectory));
        model.Packages.Clear();
    }

    private string WritePackage(string json)
    {
        string path = Path.Combine(_root.FullName, Guid.NewGuid().ToString("N") + ".repx");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        QueueManager.Instance.Packages.Clear();
        foreach (var package in _previousPackages) QueueManager.Instance.Packages.Add(package);
        SettingsService.Instance.Settings.DefaultDownloadDirectory = _previousDirectory;
        LocalizationService.Instance.CurrentLanguage = _previousLanguage;
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(_root.Parent?.FullName, temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected temporary directory.");
        _root.Delete(recursive: true);
    }
}
