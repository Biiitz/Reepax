using System;
using System.IO;
using System.Linq;
using Reepax.Models;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class DiskCleanupViewModelTests
{
    [Fact]
    public void GetPackageFilesOnDisk_FindsAllRelatedSidecarsAndCalculatesTotalSize()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Reepax_DiskCleanup_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string mainFile = Path.Combine(tempDir, "game.iso");
            string partFile = Path.Combine(tempDir, "game.iso.part");
            string partSegmentsFile = Path.Combine(tempDir, "game.iso.part.segments");
            string directSegmentsFile = Path.Combine(tempDir, "game.iso.segments");
            string tmpFile = Path.Combine(tempDir, "game.iso.tmp");
            string tempFile = Path.Combine(tempDir, "game.iso.temp");
            string reepaxTmpFile = Path.Combine(tempDir, "game.iso.reepax_tmp");

            // Write 100 bytes to each file
            byte[] bytes = new byte[100];
            File.WriteAllBytes(mainFile, bytes);
            File.WriteAllBytes(partFile, bytes);
            File.WriteAllBytes(partSegmentsFile, bytes);
            File.WriteAllBytes(directSegmentsFile, bytes);
            File.WriteAllBytes(tmpFile, bytes);
            File.WriteAllBytes(tempFile, bytes);
            File.WriteAllBytes(reepaxTmpFile, bytes);

            var pkg = new DownloadPackage
            {
                Name = "GamePackage",
                SaveDirectory = tempDir
            };
            var item = new DownloadItem
            {
                FileName = "game.iso",
                SaveFilePath = mainFile
            };
            pkg.Items.Add(item);

            var detectedFiles = MainViewModel.GetPackageFilesOnDisk(pkg);

            // Verify all created sidecars are detected
            Assert.Contains(Path.GetFullPath(mainFile), detectedFiles);
            Assert.Contains(Path.GetFullPath(partFile), detectedFiles);
            Assert.Contains(Path.GetFullPath(partSegmentsFile), detectedFiles);
            Assert.Contains(Path.GetFullPath(directSegmentsFile), detectedFiles);
            Assert.Contains(Path.GetFullPath(tmpFile), detectedFiles);
            Assert.Contains(Path.GetFullPath(tempFile), detectedFiles);
            Assert.Contains(Path.GetFullPath(reepaxTmpFile), detectedFiles);

            // Verify total file size matches (7 files * 100 bytes = 700 bytes)
            long totalSize = MainViewModel.GetTotalFilesSizeOnDisk(detectedFiles);
            Assert.Equal(700, totalSize);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void IsDedicatedPackageDirectory_ProtectsRootsAndSharedFolders()
    {
        var pkg1 = new DownloadPackage { Id = Guid.NewGuid(), Name = "Package 1" };
        var pkg2 = new DownloadPackage { Id = Guid.NewGuid(), Name = "Package 2" };

        // Protected system directories are never dedicated
        string rootDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        Assert.False(MainViewModel.IsDedicatedPackageDirectory(pkg1, rootDrive));
        Assert.True(MainViewModel.IsProtectedDirectory(rootDrive));

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(MainViewModel.IsDedicatedPackageDirectory(pkg1, userProfile));
        Assert.True(MainViewModel.IsProtectedDirectory(userProfile));

        string tempShared = Path.Combine(Path.GetTempPath(), "Reepax_Shared_Test_" + Guid.NewGuid().ToString("N"));
        string dedicatedSub1 = Path.Combine(tempShared, "Pkg1Sub");
        string dedicatedSub2 = Path.Combine(tempShared, "Pkg2Sub");

        Directory.CreateDirectory(tempShared);
        Directory.CreateDirectory(dedicatedSub1);
        Directory.CreateDirectory(dedicatedSub2);

        try
        {
            // Case 1: Both packages share tempShared as their SaveDirectory
            pkg1.SaveDirectory = tempShared;
            pkg2.SaveDirectory = tempShared;

            var allPackages = new[] { pkg1, pkg2 };

            // Since pkg2 also uses tempShared, it is NOT dedicated to pkg1
            Assert.False(MainViewModel.IsDedicatedPackageDirectory(pkg1, tempShared, allPackages));

            // Case 2: Packages have their own subdirectories
            pkg1.SaveDirectory = dedicatedSub1;
            pkg2.SaveDirectory = dedicatedSub2;

            // dedicatedSub1 is used only by pkg1
            Assert.True(MainViewModel.IsDedicatedPackageDirectory(pkg1, dedicatedSub1, allPackages));
            Assert.True(MainViewModel.IsDedicatedPackageDirectory(pkg2, dedicatedSub2, allPackages));
        }
        finally
        {
            if (Directory.Exists(tempShared))
            {
                try { Directory.Delete(tempShared, true); } catch { }
            }
        }
    }

    [Fact]
    public void DeletePackageFilesFromDisk_DeletesDedicatedFolderCompletely()
    {
        string tempBase = Path.Combine(Path.GetTempPath(), "Reepax_DedDelete_Test_" + Guid.NewGuid().ToString("N"));
        string dedicatedDir = Path.Combine(tempBase, "DedicatedGameFolder");
        string extractedSub = Path.Combine(dedicatedDir, "extracted", "subfolder");
        Directory.CreateDirectory(extractedSub);

        try
        {
            string file1 = Path.Combine(dedicatedDir, "game.part1.rar");
            string file2 = Path.Combine(extractedSub, "game_data.bin");
            File.WriteAllText(file1, "dummy archive data");
            File.WriteAllText(file2, "dummy extracted data");

            var pkg = new DownloadPackage
            {
                Id = Guid.NewGuid(),
                Name = "DedicatedGame",
                SaveDirectory = dedicatedDir
            };
            pkg.Items.Add(new DownloadItem
            {
                FileName = "game.part1.rar",
                SaveFilePath = file1
            });

            Assert.True(Directory.Exists(dedicatedDir));

            // Delete dedicated package files
            MainViewModel.DeletePackageFilesFromDisk(pkg, new[] { pkg });

            // The entire dedicated directory must be deleted
            Assert.False(Directory.Exists(dedicatedDir));
        }
        finally
        {
            if (Directory.Exists(tempBase))
            {
                try { Directory.Delete(tempBase, true); } catch { }
            }
        }
    }

    [Fact]
    public void DeleteSelectedCommand_DeletesSelectedPackageOrItemProperly()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();

        var pkg = new DownloadPackage { Id = Guid.NewGuid(), Name = "TargetPackage" };
        var item1 = new DownloadItem { FileName = "file1.zip" };
        var item2 = new DownloadItem { FileName = "file2.zip" };
        pkg.Items.Add(item1);
        pkg.Items.Add(item2);
        vm.Packages.Add(pkg);

        // Outside Downloads tab (e.g. Settings tab)
        vm.SelectedMainTab = AppMainTab.Settings;
        Assert.False(vm.CanDeleteSelected);

        // Switch to Downloads tab
        vm.SelectedMainTab = AppMainTab.Downloads;

        // When nothing is selected
        vm.SelectedPackage = null;
        vm.SelectedItem = null;
        Assert.False(vm.CanDeleteSelected);

        // Select item1
        vm.SelectedItem = item1;
        Assert.True(vm.CanDeleteSelected);

        // Execute DeleteSelected -> removes item1, pkg still has item2
        vm.DeleteSelected();
        Assert.DoesNotContain(item1, pkg.Items);
        Assert.Contains(item2, pkg.Items);
        Assert.Contains(pkg, vm.Packages);

        // Select pkg
        vm.SelectedItem = null;
        vm.SelectedPackage = pkg;
        Assert.True(vm.CanDeleteSelected);

        // Execute DeleteSelected -> removes entire pkg
        vm.DeleteSelected();
        Assert.DoesNotContain(pkg, vm.Packages);
        Assert.False(vm.CanDeleteSelected);
    }
}
