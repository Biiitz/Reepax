using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class PackageRenameOwnershipTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("RenameOwnership_").FullName;
    private readonly MainViewModel _model = new();
    private readonly DownloadPackage[] _previous;

    public PackageRenameOwnershipTests()
    {
        _previous = _model.Packages.ToArray();
        _model.Packages.Clear();
        _model.RenameFailureNotification = message => throw new InvalidOperationException(message);
    }

    [Theory]
    [InlineData("manual", "shared")]
    [InlineData("manual", "nested")]
    [InlineData("manual", "item")]
    [InlineData("manual", "extraction")]
    [InlineData("property", "shared")]
    [InlineData("property", "nested")]
    [InlineData("property", "item")]
    [InlineData("property", "extraction")]
    [InlineData("automatic", "shared")]
    [InlineData("automatic", "nested")]
    [InlineData("automatic", "item")]
    [InlineData("automatic", "extraction")]
    [InlineData("manual", "clipped")]
    [InlineData("property", "clipped")]
    [InlineData("automatic", "clipped")]
    public void SharedDirectory_RenamePreservesAllFilesAndPackagePaths(string mode, string reference)
    {
        string name = mode == "automatic" ? "Package (2 files)" : "SourceFolder";
        string source = Path.Combine(_root, name);
        Directory.CreateDirectory(source);
        byte[] completeBytes = new byte[] { 1, 3, 5, 7 };
        byte[] partialBytes = new byte[] { 2, 4, 6 };
        var first = new DownloadPackage { Name = name, SaveDirectory = source, AutoPar2Repair = false };
        var completed = new DownloadItem
        {
            FileName = "ReleaseBundle.part1.rar", SaveFilePath = Path.Combine(source, "ReleaseBundle.part1.rar"),
            OriginalUrl = "https://example.invalid/first", Status = DownloadStatus.Completed,
            TotalBytes = completeBytes.Length, DownloadedBytes = completeBytes.Length,
            ExpectedChecksum = Convert.ToHexString(SHA256.HashData(completeBytes)),
            CalculatedChecksum = Convert.ToHexString(SHA256.HashData(completeBytes))
        };
        var paused = new DownloadItem
        {
            FileName = "ReleaseBundle.part2.rar", SaveFilePath = Path.Combine(source, "ReleaseBundle.part2.rar"),
            OriginalUrl = "https://example.invalid/second", Status = DownloadStatus.Paused,
            TotalBytes = 10, DownloadedBytes = partialBytes.Length
        };
        first.AddItems(new[] { completed, paused });
        string originalCompletedPath = completed.SaveFilePath!;
        string originalPartialPath = paused.SaveFilePath!;
        var other = new DownloadPackage { Name = "Other", SaveDirectory = reference is "shared" or "clipped" ? source : Path.Combine(_root, "Elsewhere"), AutoPar2Repair = false };
        if (reference == "nested") other.SaveDirectory = Path.Combine(source, "Nested");
        if (reference == "extraction") other.ExtractionDirectory = Path.Combine(source, "Extracted");
        string otherPath = reference == "item" ? Path.Combine(source, "OtherFiles", "other.bin")
            : Path.Combine(other.ExtractionDirectory ?? other.SaveDirectory, "other.bin");
        other.Items.Add(new DownloadItem { FileName = "other.bin", SaveFilePath = otherPath, Status = DownloadStatus.Paused });
        var files = new Dictionary<string, byte[]>
        {
            [originalCompletedPath] = completeBytes,
            [originalPartialPath + ".part"] = partialBytes,
            [originalPartialPath + ".part.segments"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                TotalBytes = 10, IsSequential = true,
                Segments = new[] { new { Start = 0, End = 9, Done = 3, LocalHashes = new[] { Convert.ToHexString(SHA256.HashData(partialBytes)) } } }
            }),
            [otherPath] = new byte[] { 11, 13, 17 },
            [Path.Combine(source, "unrelated.bin")] = new byte[] { 19, 23 }
        };
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Key)!);
            File.WriteAllBytes(file.Key, file.Value);
        }
        string otherDirectory = other.SaveDirectory;
        string? otherExtraction = other.ExtractionDirectory;
        string expectedName = mode == "automatic" ? "ReleaseBundle" : "Renamed";
        _model.Packages.Add(first);
        if (reference == "clipped") first.ClippedPackages.Add(other);
        else _model.Packages.Add(other);

        if (mode == "manual") _model.RenamePackageWhenAvailable(first, expectedName);
        else if (mode == "property") first.Name = expectedName;
        else LinkMetadataResolverService.TryUpdatePackageName(first);

        Assert.Equal(expectedName, first.Name);
        Assert.Equal(mode == "manual", first.IsCustomName);
        Assert.Equal(source, first.SaveDirectory);
        Assert.Equal(originalCompletedPath, completed.SaveFilePath);
        Assert.Equal(originalPartialPath, paused.SaveFilePath);
        Assert.Equal(otherDirectory, other.SaveDirectory);
        Assert.Equal(otherExtraction, other.ExtractionDirectory);
        Assert.Equal(otherPath, other.Items[0].SaveFilePath);
        Assert.Equal(DownloadStatus.Completed, completed.Status);
        Assert.Equal(DownloadStatus.Paused, paused.Status);
        Assert.Equal(3, paused.DownloadedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(completeBytes)), completed.ExpectedChecksum);
        Assert.Equal(completed.ExpectedChecksum, completed.CalculatedChecksum);
        Assert.False(Directory.Exists(Path.Combine(_root, expectedName)));
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        var dto = Assert.Single(DownloadPersistenceService.SnapshotDtos(new[] { first }));
        Assert.Equal(expectedName, dto.Name);
        Assert.Equal(source, dto.SaveDirectory);
        Assert.Equal(originalCompletedPath, dto.Items[0].SaveFilePath);
        Assert.Equal(originalPartialPath, dto.Items[1].SaveFilePath);

        using var storage = new DownloadPersistenceService(Path.Combine(_root, "downloads.json"));
        storage.SaveDownloads(new[] { first }, sync: true);
        var restored = Assert.Single(storage.LoadDownloads());
        Assert.Equal(expectedName, restored.Name);
        Assert.Equal(source, restored.SaveDirectory);
        Assert.False(restored.RequiresRecoveredPathConfirmation);
        Assert.Equal(originalCompletedPath, restored.Items[0].SaveFilePath);
        Assert.Equal(originalPartialPath, restored.Items[1].SaveFilePath);
        Assert.Equal(DownloadStatus.Completed, restored.Items[0].Status);
        Assert.Equal(DownloadStatus.Paused, restored.Items[1].Status);
        Assert.Equal(3, restored.Items[1].DownloadedBytes);
        Assert.Equal(completed.ExpectedChecksum, restored.Items[0].ExpectedChecksum);
        Assert.Equal(completed.CalculatedChecksum, restored.Items[0].CalculatedChecksum);
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Fact]
    public void DefaultDownloadDirectory_RenameOnlyChangesPackageName()
    {
        var settings = SettingsService.Instance.Settings;
        string previous = settings.DefaultDownloadDirectory;
        string source = Directory.CreateDirectory(Path.Combine(_root, "DefaultDownloads")).FullName;
        string path = Path.Combine(source, "data.bin");
        byte[] bytes = new byte[] { 3, 5, 8 };
        File.WriteAllBytes(path, bytes);
        var package = new DownloadPackage { Name = "DefaultDownloads", SaveDirectory = source, AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = path, Status = DownloadStatus.Paused };
        package.Items.Add(item);
        _model.Packages.Add(package);
        try
        {
            settings.DefaultDownloadDirectory = source;
            _model.RenamePackageWhenAvailable(package, "NewName");

            Assert.Equal("NewName", package.Name);
            Assert.Equal(source, package.SaveDirectory);
            Assert.Equal(path, item.SaveFilePath);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(Directory.Exists(Path.Combine(_root, "NewName")));
        }
        finally { settings.DefaultDownloadDirectory = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnaffectedParentOrSimilarPrefix_DoesNotBlockDedicatedDirectoryRename(bool parentDirectory)
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "SourceFolder")).FullName;
        string otherDirectory = parentDirectory ? _root : Directory.CreateDirectory(source + "Other").FullName;
        string path = Path.Combine(source, "data.bin");
        string otherPath = Path.Combine(otherDirectory, "other.bin");
        byte[] bytes = new byte[] { 1, 2, 3 };
        byte[] otherBytes = new byte[] { 7, 8, 9 };
        File.WriteAllBytes(path, bytes);
        File.WriteAllBytes(otherPath, otherBytes);
        var package = new DownloadPackage { Name = "SourceFolder", SaveDirectory = source, AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = path, Status = DownloadStatus.Paused };
        package.Items.Add(item);
        var other = new DownloadPackage { Name = "Other", SaveDirectory = otherDirectory, AutoPar2Repair = false };
        other.Items.Add(new DownloadItem { FileName = "other.bin", SaveFilePath = otherPath, Status = DownloadStatus.Paused });
        _model.Packages.Add(package);
        _model.Packages.Add(other);

        _model.RenamePackageWhenAvailable(package, "Renamed");

        string target = Path.Combine(_root, "Renamed");
        Assert.Equal("Renamed", package.Name);
        Assert.Equal(target, package.SaveDirectory);
        Assert.Equal(Path.Combine(target, "data.bin"), item.SaveFilePath);
        Assert.Equal(bytes, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Equal(otherDirectory, other.SaveDirectory);
        Assert.Equal(otherPath, other.Items[0].SaveFilePath);
        Assert.Equal(otherBytes, File.ReadAllBytes(otherPath));
        Assert.False(Directory.Exists(source));
    }

    public void Dispose()
    {
        _model.Packages.Clear();
        foreach (var package in _previous) _model.Packages.Add(package);
        Directory.Delete(_root, recursive: true);
    }
}
