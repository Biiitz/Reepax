using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public class PackageAndItemRenameTests
{
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
        package.Rename("My Custom Game Name", isUserAction: true);

        // Assert
        Assert.True(package.IsCustomName);
        Assert.Equal("My Custom Game Name", package.Name);

        // Trigger automatic update detection (which would normally rename generic package to "The Game - Updates")
        LinkMetadataResolverService.TryUpdatePackageName(package);

        // Assert that package name is preserved and was not overwritten
        Assert.Equal("My Custom Game Name", package.Name);
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
                ContextTitle = "CoolGame v2"
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
