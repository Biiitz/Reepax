using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.Localization;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class PackageAndItemRenameTests
{
    [Theory]
    [InlineData("user", "empty")]
    [InlineData("user", "populated")]
    [InlineData("user", "file")]
    [InlineData("automatic", "empty")]
    [InlineData("automatic", "populated")]
    [InlineData("automatic", "file")]
    [InlineData("property", "empty")]
    [InlineData("property", "populated")]
    [InlineData("property", "file")]
    public void PackageRename_ExistingTargetPreservesNamePathsAndContents(string renameMode, string targetKind)
    {
        var root = Path.Combine(Path.GetTempPath(), "RenameCase_" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "SourceFolder");
        var target = Path.Combine(root, "TargetFolder");
        Directory.CreateDirectory(source);
        var sourceFile = Path.Combine(source, "data.bin");
        File.WriteAllText(sourceFile, "source content");
        if (targetKind == "file")
            File.WriteAllText(target, "target content");
        else
        {
            Directory.CreateDirectory(target);
            if (targetKind == "populated")
                File.WriteAllText(Path.Combine(target, "data.bin"), "target content");
        }
        var package = new DownloadPackage { Name = "SourceFolder", SaveDirectory = source, AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = sourceFile };
        package.Items.Add(item);
        try
        {
            if (renameMode == "property")
                package.Name = "TargetFolder";
            else
                package.Rename("TargetFolder", isUserAction: renameMode == "user");

            Assert.Equal("SourceFolder", package.Name);
            Assert.Equal(source, package.SaveDirectory);
            Assert.Equal(sourceFile, item.SaveFilePath);
            Assert.False(package.IsCustomName);
            Assert.Equal("source content", File.ReadAllText(sourceFile));
            if (targetKind == "file")
                Assert.Equal("target content", File.ReadAllText(target));
            else if (targetKind == "populated")
                Assert.Equal("target content", File.ReadAllText(Path.Combine(target, "data.bin")));
            else
                Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PackageRename_LockedSourcePreservesNameAndPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "RenameCase_" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "SourceFolder");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "data.bin");
        File.WriteAllText(file, "source content");
        var package = new DownloadPackage { Name = "SourceFolder", SaveDirectory = source, AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = file };
        package.Items.Add(item);
        try
        {
            using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                package.Rename("TargetFolder");

            Assert.Equal("SourceFolder", package.Name);
            Assert.Equal(source, package.SaveDirectory);
            Assert.Equal(file, item.SaveFilePath);
            Assert.False(package.IsCustomName);
            Assert.Equal("source content", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("TargetFolder", false)]
    [InlineData("sourcefolder", false)]
    [InlineData("TargetFolder", true)]
    public void PackageRename_SuccessMovesFilesAndPreservesPartialDownloads(string targetName, bool trailingSeparator)
    {
        var (root, package, item) = CreateRenamePackage();
        var original = item.SaveFilePath!;
        File.WriteAllText(original + ".part", "partial content");
        File.WriteAllText(original + ".part.segments", "segment state");
        if (trailingSeparator)
            package.SaveDirectory += Path.DirectorySeparatorChar;
        try
        {
            Assert.True(package.Rename(targetName));

            var expected = Path.Combine(root, targetName);
            Assert.Equal(targetName, package.Name);
            Assert.Equal(expected, package.SaveDirectory);
            Assert.True(package.IsCustomName);
            Assert.Equal(Path.Combine(expected, "data.bin"), item.SaveFilePath);
            Assert.Equal("source content", File.ReadAllText(item.SaveFilePath!));
            Assert.Equal("partial content", File.ReadAllText(item.SaveFilePath + ".part"));
            Assert.Equal("segment state", File.ReadAllText(item.SaveFilePath + ".part.segments"));
            var dto = Assert.Single(DownloadPersistenceService.SnapshotDtos(new[] { package }));
            Assert.Equal(targetName, dto.Name);
            Assert.Equal(expected, dto.SaveDirectory);
            Assert.Equal(item.SaveFilePath, Assert.Single(dto.Items).SaveFilePath);
            Assert.Single(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackageRename_CaseOnlyFailureKeepsReferencesToTheActualDirectory(bool failRollback)
    {
        var (root, package, item) = CreateRenamePackage();
        var source = package.SaveDirectory;
        int moves = 0;
        package.RenameDirectory = (from, to) =>
        {
            moves++;
            if (moves == 2 || (failRollback && moves == 3))
                throw new IOException("Simulated directory move failure.");
            Directory.Move(from, to);
        };
        try
        {
            Assert.False(package.Rename("sourcefolder"));

            Assert.Equal(3, moves);
            Assert.Equal("SourceFolder", package.Name);
            Assert.False(package.IsCustomName);
            if (failRollback)
                Assert.NotEqual(source, package.SaveDirectory);
            else
                Assert.Equal(source, package.SaveDirectory);
            Assert.True(Directory.Exists(package.SaveDirectory));
            Assert.Equal("source content", File.ReadAllText(item.SaveFilePath!));
            var dto = Assert.Single(DownloadPersistenceService.SnapshotDtos(new[] { package }));
            Assert.Equal(package.SaveDirectory, dto.SaveDirectory);
            Assert.Equal(item.SaveFilePath, Assert.Single(dto.Items).SaveFilePath);
            Assert.Single(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PackageRename_TargetCreatedDuringMovePreservesOriginalState()
    {
        var (root, package, item) = CreateRenamePackage();
        var source = package.SaveDirectory;
        var original = item.SaveFilePath;
        var target = Path.Combine(root, "TargetFolder");
        package.RenameDirectory = (from, to) =>
        {
            Directory.CreateDirectory(to);
            File.WriteAllText(Path.Combine(to, "data.bin"), "target content");
            Directory.Move(from, to);
        };
        try
        {
            Assert.False(package.Rename("TargetFolder"));

            Assert.Equal("SourceFolder", package.Name);
            Assert.Equal(source, package.SaveDirectory);
            Assert.Equal(original, item.SaveFilePath);
            Assert.False(package.IsCustomName);
            Assert.Equal("source content", File.ReadAllText(original!));
            Assert.Equal("target content", File.ReadAllText(Path.Combine(target, "data.bin")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackageRename_CollisionReportsFailureInBothUserEntryPoints(bool editPackage)
    {
        var (root, package, item) = CreateRenamePackage();
        var source = package.SaveDirectory;
        var original = item.SaveFilePath;
        Directory.CreateDirectory(Path.Combine(root, "TargetFolder"));
        var vm = new MainViewModel();
        vm.Packages.Clear();
        vm.Packages.Add(package);
        string? notification = null;
        vm.RenameFailureNotification = message => notification = message;
        try
        {
            if (editPackage)
                vm.EditPackage(package, "https://example.invalid/data.bin", "TargetFolder", source, true, true, true, true);
            else
                vm.RenamePackageWhenAvailable(package, "TargetFolder");

            Assert.Equal(Loc.Get("Status_PackageRenameFailed"), notification);
            Assert.Equal(notification, vm.StatusSummary);
            Assert.Equal("SourceFolder", package.Name);
            Assert.Equal(source, package.SaveDirectory);
            Assert.Equal(original, item.SaveFilePath);
            Assert.False(package.IsCustomName);
            Assert.False(package.AutoExtractArchives);
            Assert.False(package.LowResourceExtraction);
            Assert.False(package.DeleteArchiveAfterExtraction);
            Assert.False(package.MoveArchiveToRecycleBin);
            Assert.Equal("source content", File.ReadAllText(original!));
        }
        finally
        {
            vm.Packages.Clear();
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackageRename_SameNameCollisionDoesNotAdoptTheExistingTarget(bool missingSource)
    {
        var (root, package, _) = CreateRenamePackage("TargetFolder");
        var source = package.SaveDirectory;
        Directory.CreateDirectory(Path.Combine(root, "TargetFolder"));
        if (missingSource)
            Directory.Delete(source, true);
        try
        {
            Assert.False(package.Rename("TargetFolder"));
            Assert.Equal("TargetFolder", package.Name);
            Assert.Equal(source, package.SaveDirectory);
            Assert.False(package.IsCustomName);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static (string Root, DownloadPackage Package, DownloadItem Item) CreateRenamePackage(string name = "SourceFolder")
    {
        var root = Path.Combine(Path.GetTempPath(), "RenameCase_" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "SourceFolder");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "data.bin");
        File.WriteAllText(file, "source content");
        var package = new DownloadPackage { Name = name, SaveDirectory = source, AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = file };
        package.Items.Add(item);
        return (root, package, item);
    }

    [Fact]
    public void UserRenamedPackage_SetsIsCustomNameTrue_AndPreventsAutomaticOverwrite()
    {
        // Arrange
        var package = new DownloadPackage
        {
            Name = "Filecrypt Package (2 files)",
            SaveDirectory = @"C:\Downloads\Filecrypt Package (2 files)"
        };
        var item = new DownloadItem
        {
            PackageId = package.Id,
            FileName = "The_Game_Update_from_v1.0.1_to_v1.0.2-Patch.rar",
            OriginalUrl = "https://example.com/file1"
        };
        package.Items.Add(item);

        Assert.False(package.IsCustomName);

        // Act - user renames the package
        package.Rename("My Custom Package Name", isUserAction: true);

        // Assert
        Assert.True(package.IsCustomName);
        Assert.Equal("My Custom Package Name", package.Name);

        // Trigger automatic update detection (which would normally rename generic package to "Software - Updates")
        LinkMetadataResolverService.TryUpdatePackageName(package);

        // Assert that package name is preserved and was not overwritten
        Assert.Equal("My Custom Package Name", package.Name);
    }

    [Fact]
    public void UserRenamedItem_SetsIsCustomNameTrue_AndPreservesName()
    {
        // Arrange
        var item = new DownloadItem
        {
            FileName = "download_file",
            SaveFilePath = @"C:\Downloads\Package\download_file"
        };
        Assert.False(item.IsCustomName);

        // Act - user renames the item
        item.Rename("custom_manual_filename.rar", isUserAction: true);

        // Assert
        Assert.True(item.IsCustomName);
        Assert.Equal("custom_manual_filename.rar", item.FileName);
        Assert.EndsWith("custom_manual_filename.rar", item.SaveFilePath);
    }

    [Fact]
    public void TrailingSlashInSaveDirectory_DoesNotCreateNestedFolderStructure()
    {
        // Arrange: directory ending with trailing backslash
        var package = new DownloadPackage
        {
            Name = "OldPackage",
            SaveDirectory = @"C:\Downloads\OldPackage\"
        };
        var item = new DownloadItem
        {
            FileName = "file.zip",
            SaveFilePath = @"C:\Downloads\OldPackage\file.zip"
        };
        package.Items.Add(item);

        // Act
        package.Rename("NewPackage", isUserAction: true);

        // Assert
        var expectedDir = Path.Combine(@"C:\Downloads", "NewPackage");
        Assert.Equal(expectedDir, package.SaveDirectory);
        Assert.DoesNotContain("OldPackage", package.SaveDirectory);
        Assert.Equal(Path.Combine(expectedDir, "file.zip"), item.SaveFilePath);
    }

    [Fact]
    public void PackageGrouper_CustomPackageName_SetsIsCustomNameTrue_AndPreventsAutoRename()
    {
        // Arrange
        var links = new List<ExtractedLink>
        {
            new ExtractedLink
            {
                Url = "https://rapidgator.net/file/123/CoolGame_v2.part1.rar",
                RawFileName = "CoolGame_v2.part1.rar",
                ContextTitle = "Project Alpha v2"
            }
        };

        // Act
        var packages = PackageGrouper.GroupLinksIntoPackages(
            links,
            @"C:\Downloads",
            customPackageName: "Custom User Project Name");

        // Assert
        Assert.Single(packages);
        var pkg = packages[0];
        Assert.True(pkg.IsCustomName);
        Assert.Equal("Custom User Project Name", pkg.Name);

        // Automatic resolver attempt must not overwrite custom name
        LinkMetadataResolverService.TryUpdatePackageName(pkg);
        Assert.Equal("Custom User Project Name", pkg.Name);
    }

    [Fact]
    public void Persistence_SavesAndRestoresIsCustomName()
    {
        // Arrange
        var package = new DownloadPackage
        {
            Name = "Custom User Folder",
            SaveDirectory = @"C:\Downloads\Custom User Folder"
        };
        package.Rename("Custom User Folder", isUserAction: true);

        var item = new DownloadItem
        {
            PackageId = package.Id,
            FileName = "custom_file.bin"
        };
        item.Rename("custom_file.bin", isUserAction: true);
        package.Items.Add(item);

        Assert.True(package.IsCustomName);
        Assert.True(item.IsCustomName);

        // Act - snapshot DTOs
        var dtos = DownloadPersistenceService.SnapshotDtos(new[] { package });

        // Assert
        Assert.Single(dtos);
        var pkgDto = dtos[0];
        Assert.True(pkgDto.IsCustomName);
        Assert.Single(pkgDto.Items);
        Assert.True(pkgDto.Items[0].IsCustomName);

        // SanitizeSavedPackageDirectory should not alter custom named package directory
        var sanitized = DownloadPersistenceService.SanitizeSavedPackageDirectory(
            pkgDto.SaveDirectory,
            pkgDto.Name,
            isCustomName: pkgDto.IsCustomName);

        Assert.Equal(pkgDto.SaveDirectory, sanitized);
    }
}
