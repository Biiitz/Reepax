using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public class ArchiveExtractionTests
{
    [Fact]
    public void IsArchiveFile_CorrectlyIdentifiesArchivesAndMultiPart()
    {
        var service = ArchiveExtractionService.Instance;

        Assert.True(service.IsArchiveFile("game.zip"));
        Assert.True(service.IsArchiveFile("movie.rar"));
        Assert.True(service.IsArchiveFile("pack.7z"));
        Assert.True(service.IsArchiveFile("data.tar.gz"));
        Assert.True(service.IsArchiveFile("game.part1.rar"));
        Assert.True(service.IsArchiveFile("game.part2.rar"));
        Assert.True(service.IsArchiveFile("file.001"));

        Assert.False(service.IsArchiveFile("song.mp3"));
        Assert.False(service.IsArchiveFile("setup.exe"));
        Assert.False(service.IsArchiveFile("document.pdf"));
    }

    [Fact]
    public void IsPrimaryArchivePart_IdentifiesFirstVolume()
    {
        var service = ArchiveExtractionService.Instance;

        Assert.True(service.IsPrimaryArchivePart("standalone.zip"));
        Assert.True(service.IsPrimaryArchivePart("game.part1.rar"));
        Assert.True(service.IsPrimaryArchivePart("game.part01.rar"));
        Assert.True(service.IsPrimaryArchivePart("archive.001"));

        Assert.False(service.IsPrimaryArchivePart("game.part2.rar"));
        Assert.False(service.IsPrimaryArchivePart("game.part05.rar"));
        Assert.False(service.IsPrimaryArchivePart("archive.002"));
    }

    [Fact]
    public async Task CheckAndExtractPackage_WhenAutoExtractDisabled_DoesNothing()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ExtractTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var zipPath = Path.Combine(testDir, "test.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("sample.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Sample content inside archive");
            }

            var package = new DownloadPackage
            {
                Name = "TestPackage",
                SaveDirectory = testDir,
                AutoExtractArchives = false // Package-level extraction disabled
            };

            var item = new DownloadItem
            {
                FileName = "test.zip",
                SaveFilePath = zipPath,
                Status = DownloadStatus.Completed,
                IsEnabled = true
            };
            package.Items.Add(item);

            await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package);

            // Output file sample.txt must NOT exist because extraction was skipped
            var extractedFile = Path.Combine(testDir, "sample.txt");
            Assert.False(File.Exists(extractedFile));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CheckAndExtractPackage_WhenPartsStillDownloading_DoesNotExtract()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_PartialTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            SettingsService.Instance.Settings.AutoExtractArchives = true;

            var zipPath = Path.Combine(testDir, "test.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("sample.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Sample content");
            }

            var package = new DownloadPackage
            {
                Name = "MultiPartPackage",
                SaveDirectory = testDir,
                AutoExtractArchives = true
            };

            // Part 1: Completed
            package.Items.Add(new DownloadItem
            {
                FileName = "test.zip",
                SaveFilePath = zipPath,
                Status = DownloadStatus.Completed,
                IsEnabled = true
            });

            // Part 2: Still Downloading
            package.Items.Add(new DownloadItem
            {
                FileName = "test_part2.rar",
                SaveFilePath = Path.Combine(testDir, "test_part2.rar"),
                Status = DownloadStatus.Downloading,
                IsEnabled = true
            });

            await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package);

            // Must NOT extract because Part 2 is still Downloading
            var extractedFile = Path.Combine(testDir, "sample.txt");
            Assert.False(File.Exists(extractedFile));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CheckAndExtractPackage_WhenAllPartsCompleted_ExtractsSuccessfully()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_AllCompletedTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var zipPath = Path.Combine(testDir, "test_done.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("extracted_result.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Finished unpacking perfectly!");
            }

            var package = new DownloadPackage
            {
                Name = "FullyCompletedPackage",
                SaveDirectory = testDir,
                AutoExtractArchives = true
            };

            package.Items.Add(new DownloadItem
            {
                FileName = "test_done.zip",
                SaveFilePath = zipPath,
                Status = DownloadStatus.Completed,
                IsEnabled = true
            });

            await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package);

            var extractedFile = Path.Combine(testDir, "extracted_result.txt");
            Assert.True(File.Exists(extractedFile));
            var text = File.ReadAllText(extractedFile).Trim();
            Assert.Equal("Finished unpacking perfectly!", text);
            Assert.Equal(Loc.Get("Status_CompletedAndExtracted"), package.StatusMessage);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_SuccessfullyExtractsZip()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ZipTest_" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(testDir, "Extracted");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(extractDir);

        try
        {
            var zipPath = Path.Combine(testDir, "archive.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("hello.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Hello from inside zip!");
            }

            var success = await ArchiveExtractionService.Instance.ExtractArchiveAsync(zipPath, extractDir);

            Assert.True(success);
            var extractedFile = Path.Combine(extractDir, "hello.txt");
            Assert.True(File.Exists(extractedFile));
            var content = File.ReadAllText(extractedFile).Trim();
            Assert.Equal("Hello from inside zip!", content);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void DriveHardwareDetector_DetectsDriveTypeAndReturnsRecommendation()
    {
        var type = DriveHardwareDetector.DetectDriveType("C:\\");
        Assert.NotEqual(DriveStorageType.Unknown, type);

        bool isRecommended = DriveHardwareDetector.IsLowResourceRecommended("C:\\");
        // For SATA SSD / HDD, it should be true
        if (type is DriveStorageType.Hdd or DriveStorageType.SataSsd)
        {
            Assert.True(isRecommended);
        }

        var description = DriveHardwareDetector.GetDriveStorageDescription("C:\\");
        Assert.NotEmpty(description);
    }

    [Fact]
    public void DriveHardwareDetector_StrictAsciiDriveLetterValidation_SafelyHandlesInvalidInputs()
    {
        // Invalid input paths must return default SataSsd safely without crashing or running commands
        Assert.Equal(DriveStorageType.SataSsd, DriveHardwareDetector.DetectDriveType(";;;invalid;input"));
        Assert.Equal(DriveStorageType.SataSsd, DriveHardwareDetector.DetectDriveType("1:"));
        Assert.Equal(DriveStorageType.SataSsd, DriveHardwareDetector.DetectDriveType(""));
        Assert.Equal(DriveStorageType.SataSsd, DriveHardwareDetector.DetectDriveType(null));
    }

    [Fact]
    public async Task ExtractArchiveAsync_WithLowResourceExtraction_ExtractsSuccessfully()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_LowResTest_" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(testDir, "Extracted");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(extractDir);

        try
        {
            SettingsService.Instance.Settings.LowResourceExtraction = true;

            var zipPath = Path.Combine(testDir, "archive.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("test_low_res.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Low resource extraction content");
            }

            var success = await ArchiveExtractionService.Instance.ExtractArchiveAsync(zipPath, extractDir);

            Assert.True(success);
            var extractedFile = Path.Combine(extractDir, "test_low_res.txt");
            Assert.True(File.Exists(extractedFile));
            var content = File.ReadAllText(extractedFile).Trim();
            Assert.Equal("Low resource extraction content", content);
        }
        finally
        {
            SettingsService.Instance.Settings.LowResourceExtraction = null;
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void MainViewModel_LowResourceExtraction_PersistsAndToggles()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        bool initial = vm.LowResourceExtraction;

        vm.ToggleLowResourceExtraction();
        Assert.Equal(!initial, vm.LowResourceExtraction);
        Assert.Equal(!initial, SettingsService.Instance.Settings.LowResourceExtraction);

        vm.ToggleLowResourceExtraction();
        Assert.Equal(initial, vm.LowResourceExtraction);
        Assert.Equal(initial, SettingsService.Instance.Settings.LowResourceExtraction);
    }

    [Fact]
    public void DownloadPackage_ExtractionOptions_ArePreservedAndApplied()
    {
        var package = new DownloadPackage
        {
            Name = "CustomOptionsPkg",
            AutoExtractArchives = false,
            LowResourceExtraction = true
        };

        Assert.False(package.AutoExtractArchives);
        Assert.True(package.LowResourceExtraction);
        Assert.False(package.DeleteArchiveAfterExtraction);

        package.DeleteArchiveAfterExtraction = true;
        Assert.True(package.DeleteArchiveAfterExtraction);
    }

    [Fact]
    public void ArePartOfSameArchive_MatchesMultiPartAndRejectsDifferentArchives()
    {
        var service = ArchiveExtractionService.Instance;

        Assert.True(service.ArePartOfSameArchive("game.part01.rar", "game.part02.rar"));
        Assert.True(service.ArePartOfSameArchive("game.part1.rar", "game.part2.rar"));
        Assert.True(service.ArePartOfSameArchive("archive.001", "archive.002"));
        Assert.True(service.ArePartOfSameArchive("standalone.zip", "standalone.zip"));

        Assert.False(service.ArePartOfSameArchive("game1.part01.rar", "game2.part01.rar"));
        Assert.False(service.ArePartOfSameArchive("game.part01.rar", "readme.txt"));
    }

    [Fact]
    public async Task ExtractArchiveAsync_ZipSlip_ThrowsExceptionAndDoesNotExtractOutsideTarget()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ZipSlip_" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(testDir, "TargetExtract");
        var outsideDir = Path.Combine(testDir, "Outside");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(outsideDir);

        try
        {
            var zipPath = Path.Combine(testDir, "test_escape.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../Outside/escaped.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Sample content");
            }

            var success = await ArchiveExtractionService.Instance.ExtractArchiveAsync(zipPath, extractDir);
            Assert.False(success);

            var escapedFile = Path.Combine(outsideDir, "escaped.txt");
            Assert.False(File.Exists(escapedFile));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CheckAndExtractPackage_WithDeleteArchives_OnlyDeletesExtractedArchives()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_SelectiveDelete_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            DownloadPersistenceService.IsTestEnvironment = true;

            var zipPath = Path.Combine(testDir, "extracted.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("doc.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Documentation");
            }

            var nonArchivePath = Path.Combine(testDir, "installer.exe");
            await File.WriteAllTextAsync(nonArchivePath, "Fake binary executable content");

            var package = new DownloadPackage
            {
                Name = "SelectiveDeletePkg",
                SaveDirectory = testDir,
                AutoExtractArchives = true,
                DeleteArchiveAfterExtraction = true
            };

            package.Items.Add(new DownloadItem
            {
                FileName = "extracted.zip",
                SaveFilePath = zipPath,
                Status = DownloadStatus.Completed,
                IsEnabled = true
            });

            package.Items.Add(new DownloadItem
            {
                FileName = "installer.exe",
                SaveFilePath = nonArchivePath,
                Status = DownloadStatus.Completed,
                IsEnabled = true
            });

            await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package);

            // zip should be deleted because it was extracted
            Assert.False(File.Exists(zipPath));
            // non-archive installer.exe must still exist and NOT be deleted
            Assert.True(File.Exists(nonArchivePath));
            // Extracted content exists
            Assert.True(File.Exists(Path.Combine(testDir, "doc.txt")));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ZipSlip_ZipArchive_TraversingEntry_RejectedAndDoesNotExtractOutside()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ZipSlip_" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(testDir, "Target");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var zipPath = Path.Combine(testDir, "test_traversal.zip");
            using (var zipStream = new FileStream(zipPath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../test_traversal.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Sample traversal content");
            }

            var success = await ArchiveExtractionService.Instance.ExtractArchiveAsync(zipPath, targetDir);

            Assert.False(success);

            var outsideFile = Path.Combine(testDir, "test_traversal.txt");
            Assert.False(File.Exists(outsideFile), "File was written outside target directory.");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ZipSlip_SharpCompress_TarArchive_TraversingEntry_RejectedAndDoesNotExtractOutside()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_TarSlip_" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(testDir, "Target");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var tarPath = Path.Combine(testDir, "test_traversal.tar");
            using (var tar = SharpCompress.Archives.Tar.TarArchive.CreateArchive())
            {
                var memory = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Sample tar traversal content"));
                tar.AddEntry("../test_tar_traversal.txt", memory, false);
                using var fs = File.Create(tarPath);
                tar.SaveTo(fs, SharpCompress.Common.CompressionType.None);
            }

            var success = await ArchiveExtractionService.Instance.ExtractArchiveAsync(tarPath, targetDir);

            Assert.False(success);

            var outsideFile = Path.Combine(testDir, "test_tar_traversal.txt");
            Assert.False(File.Exists(outsideFile), "File was written outside target directory.");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ZipSlip_SharpCompress_SubdirTraversal_RejectedAndDoesNotExtractOutside()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_SubdirSlip_" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(testDir, "Target");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var tarPath = Path.Combine(testDir, "sub_traversal.tar");
            using (var tar = SharpCompress.Archives.Tar.TarArchive.CreateArchive())
            {
                var memory = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Subdir traversal content"));
                tar.AddEntry("safe_sub/../../test_sub_escape.txt", memory, false);
                using var fs = File.Create(tarPath);
                tar.SaveTo(fs, SharpCompress.Common.CompressionType.None);
            }

            var success = await ArchiveExtractionService.Instance.ExtractArchiveAsync(tarPath, targetDir);

            Assert.False(success);

            var outsideFile = Path.Combine(testDir, "test_sub_escape.txt");
            Assert.False(File.Exists(outsideFile), "File was written outside target directory.");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_ValidNestedDirectoryEntry_ExtractsSuccessfully()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ValidNested_" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(testDir, "Target");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var zipPath = Path.Combine(testDir, "valid_nested.zip");
            using (var zipStream = new FileStream(zipPath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("nested/deep/inside/readme.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Valid nested content");
            }

            var success = await ArchiveExtractionService.Instance.ExtractArchiveAsync(zipPath, targetDir);

            Assert.True(success);

            var extractedFile = Path.Combine(targetDir, "nested", "deep", "inside", "readme.txt");
            Assert.True(File.Exists(extractedFile));
            Assert.Equal("Valid nested content", File.ReadAllText(extractedFile).Trim());
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CheckAndExtractPackage_WhenUpdatePackage_ExtractsIntoSubfolder()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_UpdateExtractTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var zipPath = Path.Combine(testDir, "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.zip");
            using (var zipStream = new FileStream(zipPath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("setup.exe");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Installer Content");
            }

            var package = new DownloadPackage
            {
                Name = "The Blood of Dawnwalker - Updates",
                SaveDirectory = testDir,
                AutoExtractArchives = true
            };

            var item = new DownloadItem
            {
                FileName = "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.zip",
                SaveFilePath = zipPath,
                Status = DownloadStatus.Completed,
                IsEnabled = true
            };
            package.Items.Add(item);

            await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package);

            var expectedSubfolder = Path.Combine(testDir, "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release");
            var extractedFile = Path.Combine(expectedSubfolder, "setup.exe");

            Assert.True(Directory.Exists(expectedSubfolder));
            Assert.True(File.Exists(extractedFile));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void FindVerifyBatFilePath_WhenBatFileInRoot_FindsFilePath()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_BatTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var batPath = Path.Combine(testDir, "Verify BIN files before installation.bat");
            File.WriteAllText(batPath, "@echo off\r\nquickSFV.exe -c MD5\\*.md5");

            var package = new DownloadPackage
            {
                Name = "Game Package",
                SaveDirectory = testDir
            };

            var found = DownloadPackage.FindVerifyBatFilePath(package);
            Assert.NotNull(found);
            Assert.True(File.Exists(found));
            Assert.Equal(batPath, found);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void FindVerifyBatFilePath_WhenBatFileInSubfolder_FindsFilePath()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_BatTest_" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(testDir, "Extracted Game");
        Directory.CreateDirectory(subDir);

        try
        {
            var batPath = Path.Combine(subDir, "Verify BIN files before installation.bat");
            File.WriteAllText(batPath, "@echo off\r\nquickSFV.exe -c MD5\\*.md5");

            var package = new DownloadPackage
            {
                Name = "Game Package",
                SaveDirectory = testDir
            };

            var found = DownloadPackage.FindVerifyBatFilePath(package);
            Assert.NotNull(found);
            Assert.Equal(batPath, found);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void FindVerifyBatFilePath_WhenCasingDiffers_FindsFilePath()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_BatTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var batPath = Path.Combine(testDir, "verify bin files before installation.bat");
            File.WriteAllText(batPath, "@echo off\r\nquickSFV.exe -c MD5\\*.md5");

            var package = new DownloadPackage
            {
                Name = "Game Package",
                SaveDirectory = testDir
            };

            var found = DownloadPackage.FindVerifyBatFilePath(package);
            Assert.NotNull(found);
            Assert.True(File.Exists(found));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void FindVerifyBatFilePath_WhenNoBatFile_ReturnsNull()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_BatTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            File.WriteAllText(Path.Combine(testDir, "setup.exe"), "dummy");

            var package = new DownloadPackage
            {
                Name = "Game Package",
                SaveDirectory = testDir
            };

            var found = DownloadPackage.FindVerifyBatFilePath(package);
            Assert.Null(found);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void CheckAndRefreshVerifyBatFile_WhenIncomplete_ReturnsFalse()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_BatTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var batPath = Path.Combine(testDir, "Verify BIN files before installation.bat");
            File.WriteAllText(batPath, "@echo off");

            var package = new DownloadPackage
            {
                Name = "Game Package",
                SaveDirectory = testDir,
                Status = DownloadStatus.Downloading
            };
            var item = new DownloadItem
            {
                FileName = "game.part1.rar",
                Status = DownloadStatus.Downloading,
                IsEnabled = true
            };
            package.Items.Add(item);

            var canVerify = package.CheckAndRefreshVerifyBatFile();
            Assert.False(canVerify);
            Assert.False(package.HasVerifyBatFile);
            Assert.Null(package.VerifyBatFilePath);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void CheckAndRefreshVerifyBatFile_WhenCompletedAndBatPresent_ReturnsTrueAndSetsProperty()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_BatTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var batPath = Path.Combine(testDir, "Verify BIN files before installation.bat");
            File.WriteAllText(batPath, "@echo off");

            var package = new DownloadPackage
            {
                Name = "Game Package",
                SaveDirectory = testDir,
                Status = DownloadStatus.Completed
            };
            var item = new DownloadItem
            {
                FileName = "game.bin",
                SaveFilePath = Path.Combine(testDir, "game.bin"),
                Status = DownloadStatus.Completed,
                IsEnabled = true
            };
            package.Items.Add(item);

            var canVerify = package.CheckAndRefreshVerifyBatFile();
            Assert.True(canVerify);
            Assert.True(package.HasVerifyBatFile);
            Assert.Equal(batPath, package.VerifyBatFilePath);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void CheckAndRefreshVerifyBatFile_WhenAutoExtractPending_ReturnsFalseUntilExtractDone()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_BatTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var batPath = Path.Combine(testDir, "Verify BIN files before installation.bat");
            File.WriteAllText(batPath, "@echo off");

            var package = new DownloadPackage
            {
                Name = "Game Package",
                SaveDirectory = testDir,
                Status = DownloadStatus.Completed,
                AutoExtractArchives = true
            };
            package.EnsureNextTaskSteps();

            var item = new DownloadItem
            {
                FileName = "game.zip",
                SaveFilePath = Path.Combine(testDir, "game.zip"),
                Status = DownloadStatus.Completed,
                IsEnabled = true
            };
            package.Items.Add(item);

            // Step Extract is pending
            Assert.False(package.CheckAndRefreshVerifyBatFile());
            Assert.False(package.HasVerifyBatFile);

            // Step Extract is now done
            package.SetNextTaskDone("Extract");
            Assert.True(package.CheckAndRefreshVerifyBatFile());
            Assert.True(package.HasVerifyBatFile);
            Assert.Equal(batPath, package.VerifyBatFilePath);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_ReportsAccurateProgressAndDecimalPercentage()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ExtractProgress_" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(testDir, "Extracted");
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(extractDir);

        try
        {
            var zipPath = Path.Combine(testDir, "test_archive.zip");
            using (var zipStream = new FileStream(zipPath, FileMode.Create))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry1 = archive.CreateEntry("file1.bin");
                using (var entryStream = entry1.Open())
                {
                    var data = new byte[200_000];
                    new Random(42).NextBytes(data);
                    entryStream.Write(data, 0, data.Length);
                }

                var entry2 = archive.CreateEntry("file2.bin");
                using (var entryStream = entry2.Open())
                {
                    var data = new byte[300_000];
                    new Random(43).NextBytes(data);
                    entryStream.Write(data, 0, data.Length);
                }
            }

            var progressValues = new List<double>();
            var statusMessages = new List<string>();

            var success = await ArchiveExtractionService.Instance.ExtractArchiveAsync(
                zipPath,
                extractDir,
                statusCallback: msg => statusMessages.Add(msg),
                lowResourceMode: false,
                progressCallback: pct => progressValues.Add(pct));

            Assert.True(success);
            Assert.NotEmpty(progressValues);
            Assert.Equal(0.0, progressValues[0]);
            Assert.Equal(100.0, progressValues[^1]);

            // Ensure progress is monotonic (never goes backwards)
            for (int i = 1; i < progressValues.Count; i++)
            {
                Assert.True(progressValues[i] >= progressValues[i - 1], $"Progress went backwards at index {i}: {progressValues[i - 1]} -> {progressValues[i]}");
            }

            Assert.True(File.Exists(Path.Combine(extractDir, "file1.bin")));
            Assert.True(File.Exists(Path.Combine(extractDir, "file2.bin")));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("Verify BIN files before installation.bat", true)]
    [InlineData("verify bin files before installation.bat", true)]
    [InlineData("VERIFY BIN FILES BEFORE INSTALLATION.BAT", true)]
    [InlineData("Verify BIN files before installation.cmd", true)]
    [InlineData("QuickCheck.bat", true)]
    [InlineData("quickcheck.bat", true)]
    [InlineData("chkcrcre.bat", true)]
    [InlineData("verify_files.bat", true)]
    [InlineData("verify.bat", true)]
    [InlineData("verify.cmd", true)]
    [InlineData("verify bin.bat", true)]
    [InlineData("MyGame_verify_bin.bat", true)]
    [InlineData("setup.exe", false)]
    [InlineData("setup.bat", false)]
    [InlineData("install.bat", false)]
    [InlineData("verify.txt", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsVerifyBatFile_AccuratelyIdentifiesVerifyScripts(string fileName, bool expected)
    {
        Assert.Equal(expected, DownloadPackage.IsVerifyBatFile(fileName));
    }

    [Fact]
    public void FindVerifyBatFilePath_FindsBatInPackageNameSubdirectory()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_BatSubdirTest_" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(testDir, "SuperGame");
        Directory.CreateDirectory(subDir);

        try
        {
            var batPath = Path.Combine(subDir, "QuickCheck.bat");
            File.WriteAllText(batPath, "@echo off");

            var package = new DownloadPackage
            {
                Name = "SuperGame",
                SaveDirectory = testDir,
                Status = DownloadStatus.Completed
            };
            package.Items.Add(new DownloadItem
            {
                FileName = "game.bin",
                Status = DownloadStatus.Completed,
                IsEnabled = true
            });

            var canVerify = package.CheckAndRefreshVerifyBatFile();
            Assert.True(canVerify);
            Assert.True(package.HasVerifyBatFile);
            Assert.Equal(batPath, package.VerifyBatFilePath);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void RefreshAndCheckExistsOnDisk_WhenFolderExistsWithFiles_ReturnsTrue()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ExistTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            File.WriteAllText(Path.Combine(testDir, "game.iso"), "test");
            var package = new DownloadPackage
            {
                Name = "Game",
                SaveDirectory = testDir
            };
            package.Items.Add(new DownloadItem { FileName = "game.iso", SaveFilePath = Path.Combine(testDir, "game.iso") });

            Assert.True(package.RefreshAndCheckExistsOnDisk());
            Assert.True(package.ExistsOnDisk());
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void RefreshAndCheckExistsOnDisk_WhenFolderDeleted_ReturnsFalse()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ExistTest_" + Guid.NewGuid().ToString("N"));
        var package = new DownloadPackage
        {
            Name = "NonExistentGame",
            SaveDirectory = testDir
        };
        package.Items.Add(new DownloadItem { FileName = "game.iso", SaveFilePath = Path.Combine(testDir, "game.iso") });

        Assert.False(package.RefreshAndCheckExistsOnDisk());
        Assert.False(package.ExistsOnDisk());
    }

    [Fact]
    public void RefreshAndCheckExistsOnDisk_WhenFolderRenamedInWindows_DetectsAndSelfHealsSaveDirectory()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "Reepax_RenameTest_" + Guid.NewGuid().ToString("N"));
        var oldPkgDir = Path.Combine(rootDir, "Game_Original");
        var newPkgDir = Path.Combine(rootDir, "Game_RenamedInWindows");
        Directory.CreateDirectory(newPkgDir); // Simulating user renaming folder in Explorer

        try
        {
            var filePath = Path.Combine(newPkgDir, "data.pack");
            File.WriteAllText(filePath, "content");

            var package = new DownloadPackage
            {
                Name = "Game_Original",
                SaveDirectory = oldPkgDir // Points to old directory that no longer exists
            };
            package.Items.Add(new DownloadItem { FileName = "data.pack", SaveFilePath = Path.Combine(oldPkgDir, "data.pack") });

            // Act: Reepax checks if it exists
            bool exists = package.RefreshAndCheckExistsOnDisk();

            // Assert: recognized the folder renamed in Windows, updated SaveDirectory!
            Assert.True(exists);
            Assert.Equal(newPkgDir, package.SaveDirectory);
            Assert.Equal(filePath, package.Items[0].SaveFilePath);
        }
        finally
        {
            try { Directory.Delete(rootDir, true); } catch { }
        }
    }

    [Fact]
    public void MenuOpenPackageFolder_LocalizedString_IsOnlyOpenPackage()
    {
        Assert.Equal("Open Package", Reepax.Services.Localization.Strings_de.Map["Menu_OpenPackageFolder"]);
        Assert.Equal("Open Package", Reepax.Services.Localization.Strings_en.Map["Menu_OpenPackageFolder"]);
    }

    [Fact]
    public void DeleteToRecycleBin_ZeroByteFile_DeletesDirectlyWithoutError()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "Reepax_ZeroByte_" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(tempFile, Array.Empty<byte>());
        Assert.True(File.Exists(tempFile));
        Assert.Equal(0, new FileInfo(tempFile).Length);

        ArchiveExtractionService.DeleteToRecycleBin(tempFile);

        Assert.False(File.Exists(tempFile));
    }

    [Fact]
    public void DeleteToRecycleBin_NonExistentOrEmpty_DoesNotThrow()
    {
        ArchiveExtractionService.DeleteToRecycleBin(string.Empty);
        ArchiveExtractionService.DeleteToRecycleBin("   ");
        ArchiveExtractionService.DeleteToRecycleBin(@"C:\NonExistentPath_XYZ\NonExistentFile_" + Guid.NewGuid().ToString("N") + ".rar");
    }

    [Theory]
    [InlineData("game.zip.part")]
    [InlineData("game.zip.part.segments")]
    [InlineData("game.zip.segments")]
    [InlineData("download.tmp")]
    [InlineData("data.temp")]
    [InlineData("database.bak")]
    [InlineData("file.crdownload")]
    [InlineData("file.reepax_tmp")]
    [InlineData("file.aria2")]
    [InlineData("file.1")]
    [InlineData("~lockfile")]
    [InlineData("__perm_test_abc.tmp")]
    public void IsTempFile_RecognizesTemporaryExtensionsAndPrefixes(string relativeName)
    {
        var path = Path.Combine(@"C:\FakeDownloads", relativeName);
        Assert.True(ArchiveExtractionService.IsTempFile(path));
    }

    [Theory]
    [InlineData("game.zip")]
    [InlineData("archive.rar")]
    [InlineData("installer.exe")]
    [InlineData("video.mp4")]
    [InlineData("document.pdf")]
    public void IsTempFile_RejectsStandardUserFiles(string relativeName)
    {
        var path = Path.Combine(@"C:\FakeDownloads", relativeName);
        Assert.False(ArchiveExtractionService.IsTempFile(path));
    }

    [Fact]
    public void IsTempFile_RecognizesFilesInWindowsTemp()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "AnyFile_" + Guid.NewGuid().ToString("N") + ".data");
        Assert.True(ArchiveExtractionService.IsTempFile(tempFile));
    }

    [Fact]
    public void DeleteToRecycleBin_TempFilesWithContent_DeletesDirectlyWithoutRecycleBin()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_TempDelTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var segFile = Path.Combine(testDir, "test.zip.part.segments");
            var partFile = Path.Combine(testDir, "test.zip.part");
            var tmpFile = Path.Combine(testDir, "test.tmp");

            File.WriteAllText(segFile, "{\"Segments\": []}");
            File.WriteAllBytes(partFile, new byte[] { 1, 2, 3, 4 });
            File.WriteAllText(tmpFile, "temporary data");

            Assert.True(File.Exists(segFile));
            Assert.True(File.Exists(partFile));
            Assert.True(File.Exists(tmpFile));

            ArchiveExtractionService.DeleteToRecycleBin(segFile);
            ArchiveExtractionService.DeleteToRecycleBin(partFile);
            ArchiveExtractionService.DeleteToRecycleBin(tmpFile);

            Assert.False(File.Exists(segFile));
            Assert.False(File.Exists(partFile));
            Assert.False(File.Exists(tmpFile));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void DeleteOrMoveToTemp_DeletesFileDirectly()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_DirectDel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var file = Path.Combine(testDir, "test.part");
            File.WriteAllText(file, "temporary content");
            Assert.True(File.Exists(file));

            ArchiveExtractionService.DeleteOrMoveToTemp(file);
            Assert.False(File.Exists(file));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void PurgeTemporaryFilesInDirectory_DeletesOnlyTempFilesAndLeavesUserFiles()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_PurgeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var subDir = Path.Combine(testDir, "nested");
        Directory.CreateDirectory(subDir);

        try
        {
            var userFile = Path.Combine(testDir, "important_document.pdf");
            var partFile = Path.Combine(testDir, "download.iso.part");
            var segFile = Path.Combine(testDir, "download.iso.part.segments");
            var tmpFile = Path.Combine(subDir, "cache.tmp");
            var zeroFile = Path.Combine(subDir, "empty.txt");
            var backupFile = Path.Combine(subDir, "repaired.rar.1");

            File.WriteAllText(userFile, "real user data");
            File.WriteAllBytes(partFile, new byte[] { 1, 2, 3 });
            File.WriteAllText(segFile, "{}");
            File.WriteAllText(tmpFile, "temp data");
            File.WriteAllBytes(zeroFile, Array.Empty<byte>());
            File.WriteAllText(backupFile, "backup corrupt data");

            ArchiveExtractionService.PurgeTemporaryFilesInDirectory(testDir);

            Assert.True(File.Exists(userFile), "User file should be kept untouched");
            Assert.False(File.Exists(partFile), ".part file should be deleted");
            Assert.False(File.Exists(segFile), ".part.segments file should be deleted");
            Assert.False(File.Exists(tmpFile), ".tmp file should be deleted");
            Assert.False(File.Exists(zeroFile), "Zero-byte file should be deleted");
            Assert.False(File.Exists(backupFile), ".1 backup file should be deleted");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void CleanEmptySubdirectories_RemovesEmptyFoldersBottomUp()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_CleanSubDirTest_" + Guid.NewGuid().ToString("N"));
        var deepEmpty = Path.Combine(testDir, "a", "b", "c");
        var nonEmpty = Path.Combine(testDir, "keep", "nested");
        Directory.CreateDirectory(deepEmpty);
        Directory.CreateDirectory(nonEmpty);

        try
        {
            var keepFile = Path.Combine(nonEmpty, "file.txt");
            File.WriteAllText(keepFile, "content");

            ArchiveExtractionService.CleanEmptySubdirectories(testDir);

            Assert.False(Directory.Exists(Path.Combine(testDir, "a")), "Empty tree should be removed");
            Assert.True(Directory.Exists(nonEmpty), "Directory with files should be retained");
            Assert.True(File.Exists(keepFile), "File should be retained");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void DeleteToRecycleBin_DirectoryWithOnlyTempFiles_DeletesDirectlyWithoutRecycleBin()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_OnlyTempDirTest_" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(testDir, "sub");
        Directory.CreateDirectory(subDir);

        try
        {
            var partFile = Path.Combine(testDir, "file.zip.part");
            var segFile = Path.Combine(testDir, "file.zip.part.segments");
            var tmpFile = Path.Combine(subDir, "scratch.tmp");
            File.WriteAllBytes(partFile, new byte[] { 10, 20 });
            File.WriteAllText(segFile, "{}");
            File.WriteAllText(tmpFile, "temp");

            ArchiveExtractionService.DeleteToRecycleBin(testDir);

            Assert.False(Directory.Exists(testDir), "Directory containing only temp files should be directly deleted");
        }
        finally
        {
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void HasSufficientDiskSpace_WhenSpaceIsPlenty_ReturnsTrue()
    {
        var tempPath = Path.GetTempPath();
        bool result = ArchiveExtractionService.HasSufficientDiskSpace(tempPath, 1024, out long availableFreeSpace, out long requiredWithBuffer);

        Assert.True(result);
        Assert.True(availableFreeSpace > 0);
        Assert.True(requiredWithBuffer > 1024);
        Assert.True(availableFreeSpace >= requiredWithBuffer);
    }

    [Fact]
    public void HasSufficientDiskSpace_WhenRequiredExceedsAvailable_ReturnsFalse()
    {
        var tempPath = Path.GetTempPath();
        // Request an impossible amount of space (500 TB)
        long impossibleBytes = 500_000_000_000_000L;
        bool result = ArchiveExtractionService.HasSufficientDiskSpace(tempPath, impossibleBytes, out long availableFreeSpace, out long requiredWithBuffer);

        Assert.False(result);
        Assert.True(availableFreeSpace > 0);
        Assert.True(requiredWithBuffer > impossibleBytes);
        Assert.True(availableFreeSpace < requiredWithBuffer);
    }

    [Fact]
    public void HasSufficientDiskSpace_OnUncPath_ReturnsTrueTolerantly()
    {
        var uncPath = @"\\127.0.0.1\SharedFolder\Downloads";
        bool result = ArchiveExtractionService.HasSufficientDiskSpace(uncPath, 1_000_000, out long availableFreeSpace, out long requiredWithBuffer);

        Assert.True(result);
        Assert.Equal(-1, availableFreeSpace);
    }

    [Fact]
    public void HasSufficientDiskSpace_OnEmptyOrNullPath_ReturnsTrueTolerantly()
    {
        bool resultEmpty = ArchiveExtractionService.HasSufficientDiskSpace("", 1000, out long available1, out _);
        bool resultNull = ArchiveExtractionService.HasSufficientDiskSpace(null!, 1000, out long available2, out _);

        Assert.True(resultEmpty);
        Assert.True(resultNull);
        Assert.Equal(-1, available1);
        Assert.Equal(-1, available2);
    }

    [Fact]
    public void HasExtractableArchives_DetectsArchivesInMixedPackages()
    {
        var service = ArchiveExtractionService.Instance;

        var packageWithArchives = new DownloadPackage { Name = "Game Package" };
        packageWithArchives.Items.Add(new DownloadItem { FileName = "game.part01.rar", SaveFilePath = @"C:\Downloads\game.part01.rar" });
        packageWithArchives.Items.Add(new DownloadItem { FileName = "game.part02.rar", SaveFilePath = @"C:\Downloads\game.part02.rar" });
        packageWithArchives.Items.Add(new DownloadItem { FileName = "fg-optional-voice.bin", SaveFilePath = @"C:\Downloads\fg-optional-voice.bin" });

        Assert.True(service.HasExtractableArchives(packageWithArchives));

        var packageWithoutArchives = new DownloadPackage { Name = "Bin Only Package" };
        packageWithoutArchives.Items.Add(new DownloadItem { FileName = "setup.exe", SaveFilePath = @"C:\Downloads\setup.exe" });
        packageWithoutArchives.Items.Add(new DownloadItem { FileName = "data.bin", SaveFilePath = @"C:\Downloads\data.bin" });

        Assert.False(service.HasExtractableArchives(packageWithoutArchives));
    }

    [Fact]
    public void PowerManagementService_AcquireAndRelease_ReferenceCountingWorks()
    {
        var service = Services.SystemIntegration.PowerManagementService.Instance;

        service.ReleaseAll();
        Assert.False(service.IsKeepAwakeActive);

        service.AcquireKeepAwake("Test Operation 1");
        Assert.True(service.IsKeepAwakeActive);

        service.AcquireKeepAwake("Test Operation 2");
        Assert.True(service.IsKeepAwakeActive);

        service.ReleaseKeepAwake("Test Operation 1");
        Assert.True(service.IsKeepAwakeActive);

        service.ReleaseKeepAwake("Test Operation 2");
        Assert.False(service.IsKeepAwakeActive);
    }

    [Fact]
    public async Task ExtractArchiveAsync_WhenCancelled_ReturnsFalsePromptly()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_CancelTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var zipPath = Path.Combine(testDir, "test_cancel.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("dummy.dat");
                using var writer = new BinaryWriter(entry.Open());
                // Write 1 MB of dummy data
                writer.Write(new byte[1024 * 1024]);
            }

            var outDir = Path.Combine(testDir, "extracted");
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel(); // Pre-cancelled

            var result = await ArchiveExtractionService.Instance.ExtractArchiveAsync(
                zipPath,
                outDir,
                cancellationToken: cts.Token);

            Assert.False(result);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task CheckAndExtractPackage_WhenAutoExtractDisabled_WithForceTrue_ExtractsSuccessfully()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_ForceExtract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var zipPath = Path.Combine(testDir, "archive.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("manual_extracted.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine("Hello manual extraction!");
            }

            var package = new DownloadPackage
            {
                Name = "ForcePackage",
                SaveDirectory = testDir,
                AutoExtractArchives = false
            };

            var item = new DownloadItem
            {
                FileName = "archive.zip",
                SaveFilePath = zipPath,
                Status = DownloadStatus.Completed,
                IsEnabled = true
            };
            package.Items.Add(item);

            var success = await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true);

            Assert.True(success);
            var extractedFile = Path.Combine(testDir, "manual_extracted.txt");
            Assert.True(File.Exists(extractedFile));
            var content = File.ReadAllText(extractedFile);
            Assert.Contains("Hello manual extraction!", content);
            Assert.Contains(package.NextTaskSteps, s => s.Key == "Extract" && s.State == NextTaskStepState.Done);
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_WithPauseAndResume_ExtractsWithoutDataLoss()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Reepax_PauseResumeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var zipPath = Path.Combine(testDir, "payload.zip");
            var originalBytes = new byte[2 * 1024 * 1024]; // 2 MB
            new Random(42).NextBytes(originalBytes);

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("data.bin");
                using var stream = entry.Open();
                stream.Write(originalBytes, 0, originalBytes.Length);
            }

            var outDir = Path.Combine(testDir, "out");
            using var pauseEvent = new System.Threading.ManualResetEventSlim(true);
            bool wasPausedTriggered = false;

            var extractTask = ArchiveExtractionService.Instance.ExtractArchiveAsync(
                zipPath,
                outDir,
                pauseEvent: pauseEvent,
                pauseStateChanged: paused =>
                {
                    if (paused) wasPausedTriggered = true;
                });

            // Pause extraction shortly after start
            pauseEvent.Reset();
            await Task.Delay(50);

            // Resume extraction
            pauseEvent.Set();
            var success = await extractTask;

            Assert.True(success);
            Assert.True(wasPausedTriggered);
            var extractedPath = Path.Combine(outDir, "data.bin");
            Assert.True(File.Exists(extractedPath));
            var extractedBytes = File.ReadAllBytes(extractedPath);
            Assert.Equal(originalBytes.Length, extractedBytes.Length);
            Assert.True(originalBytes.SequenceEqual(extractedBytes));
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void DownloadPackage_AreDownloadsCompleted_CorrectlyDetectsCompletion()
    {
        var package = new DownloadPackage { Name = "TestPackage" };
        var item1 = new DownloadItem { FileName = "part1.rar", Status = DownloadStatus.Completed, IsEnabled = true };
        var item2 = new DownloadItem { FileName = "part2.rar", Status = DownloadStatus.Downloading, IsEnabled = true };
        package.Items.Add(item1);
        package.Items.Add(item2);

        Assert.False(package.AreDownloadsCompleted);

        item2.Status = DownloadStatus.Completed;
        Assert.True(package.AreDownloadsCompleted);
    }

    [Fact]
    public void ArchiveExtractionService_PauseAndResumeTracking_WorksCorrectly()
    {
        var packageId = Guid.NewGuid();
        var service = ArchiveExtractionService.Instance;

        Assert.False(service.IsPackageExtractionPaused(packageId));
        service.PausePackageExtraction(packageId); // No-op for untracked ID, doesn't crash
        Assert.False(service.IsPackageExtractionPaused(packageId));
    }

    [Fact]
    public void DownloadPackage_IsExtracted_DetectsExtractionStatesAndPreservesExtractStep()
    {
        var package = new DownloadPackage { Name = "TestExtractedPackage", AutoExtractArchives = true };

        // Initially not extracted
        Assert.False(package.IsExtracted);

        // When status message is completed & extracted
        package.StatusMessage = Loc.Get("Status_CompletedAndExtracted");
        Assert.True(package.IsExtracted);

        // When NextTaskStep Extract is Done
        package.StatusMessage = Loc.Get("Status_Queued");
        package.EnsureNextTaskSteps();
        var extractStep = package.NextTaskSteps.FirstOrDefault(s => s.Key == "Extract");
        Assert.NotNull(extractStep);
        extractStep!.State = NextTaskStepState.Done;
        Assert.True(package.IsExtracted);

        // CanEditPackage should be false when extracted
        Assert.False(package.CanEditPackage);

        // When AutoExtractArchives is turned off, EnsureNextTaskSteps preserves the Done Extract step
        package.AutoExtractArchives = false;
        package.EnsureNextTaskSteps();
        var preservedExtractStep = package.NextTaskSteps.FirstOrDefault(s => s.Key == "Extract");
        Assert.NotNull(preservedExtractStep);
        Assert.Equal(NextTaskStepState.Done, preservedExtractStep!.State);
        Assert.True(package.IsExtracted);
    }

    [Fact]
    public async Task MainViewModel_ExtractPackageAsync_SkipsWhenAlreadyExtracted()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        var package = new DownloadPackage { Name = "AlreadyExtractedPkg", IsExtracted = true };
        vm.Packages.Add(package);

        await vm.ExtractPackageAsync(package);

        // StatusSummary should reflect that it is already completed and extracted
        Assert.Equal(Loc.Get("Status_CompletedAndExtracted"), vm.StatusSummary);
        Assert.False(ArchiveExtractionService.Instance.IsPackageExtracting(package.Id));
    }

    [Fact]
    public async Task MainViewModel_ExtractPackageAsync_SkipsWhenCurrentlyExtracting()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        var package = new DownloadPackage { Name = "ExtractingPkg", IsExtracting = true };
        vm.Packages.Add(package);

        // Calling ExtractPackageAsync when already extracting must not start a new extraction
        await vm.ExtractPackageAsync(package);

        Assert.False(ArchiveExtractionService.Instance.IsPackageExtracting(package.Id));
    }

    [Fact]
    public async Task ArchiveExtractionService_CheckAndExtractPackageAsync_SkipsWhenAlreadyExtracted()
    {
        var package = new DownloadPackage { Name = "DirectExtractedPkg", IsExtracted = true };
        var result = await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package, force: true);

        // Must return true immediately and not start extraction
        Assert.True(result);
        Assert.False(ArchiveExtractionService.Instance.IsPackageExtracting(package.Id));
    }

    [Fact]
    public void AddLinksDialog_PrefillsExtractionSettingsFromGlobalDefaults()
    {
        var thread = new System.Threading.Thread(() =>
        {
            var settings = SettingsService.Instance.Settings;
            bool prevAuto = settings.AutoExtractArchives;
            bool? prevLow = settings.LowResourceExtraction;
            bool prevDel = settings.DeleteArchiveAfterExtraction;
            bool prevRec = settings.MoveArchiveToRecycleBin;

            try
            {
                settings.AutoExtractArchives = true;
                settings.LowResourceExtraction = true;
                settings.DeleteArchiveAfterExtraction = true;
                settings.MoveArchiveToRecycleBin = false;

                var dialog = new Reepax.Views.AddLinksDialog("https://rapidgator.net/file/123/file.part1.rar");
                var autoBox = (System.Windows.Controls.CheckBox)dialog.FindName("AutoExtractCheckBox");
                var lowBox = (System.Windows.Controls.CheckBox)dialog.FindName("LowResourceCheckBox");
                var delBox = (System.Windows.Controls.CheckBox)dialog.FindName("DeleteArchivesCheckBox");
                var recBox = (System.Windows.Controls.CheckBox)dialog.FindName("RecycleArchivesCheckBox");

                Assert.True(autoBox.IsChecked == true);
                Assert.True(lowBox.IsChecked == true);
                Assert.True(delBox.IsChecked == true);
                Assert.False(recBox.IsChecked == true);
            }
            finally
            {
                settings.AutoExtractArchives = prevAuto;
                settings.LowResourceExtraction = prevLow;
                settings.DeleteArchiveAfterExtraction = prevDel;
                settings.MoveArchiveToRecycleBin = prevRec;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    [Fact]
    public void AddLinksDialog_TogglingAutoExtractRestoresConfiguredDefaults()
    {
        var thread = new System.Threading.Thread(() =>
        {
            var settings = SettingsService.Instance.Settings;
            bool prevAuto = settings.AutoExtractArchives;
            bool? prevLow = settings.LowResourceExtraction;
            bool prevDel = settings.DeleteArchiveAfterExtraction;
            bool prevRec = settings.MoveArchiveToRecycleBin;

            try
            {
                settings.AutoExtractArchives = false;
                settings.LowResourceExtraction = true;
                settings.DeleteArchiveAfterExtraction = false;
                settings.MoveArchiveToRecycleBin = true;

                var dialog = new Reepax.Views.AddLinksDialog("https://rapidgator.net/file/123/file.part1.rar");
                var autoBox = (System.Windows.Controls.CheckBox)dialog.FindName("AutoExtractCheckBox");
                var lowBox = (System.Windows.Controls.CheckBox)dialog.FindName("LowResourceCheckBox");
                var delBox = (System.Windows.Controls.CheckBox)dialog.FindName("DeleteArchivesCheckBox");
                var recBox = (System.Windows.Controls.CheckBox)dialog.FindName("RecycleArchivesCheckBox");

                Assert.False(autoBox.IsChecked == true);

                // Now toggle on AutoExtract
                autoBox.IsChecked = true;

                Assert.True(lowBox.IsChecked == true);
                Assert.False(delBox.IsChecked == true);
                Assert.True(recBox.IsChecked == true);
            }
            finally
            {
                settings.AutoExtractArchives = prevAuto;
                settings.LowResourceExtraction = prevLow;
                settings.DeleteArchiveAfterExtraction = prevDel;
                settings.MoveArchiveToRecycleBin = prevRec;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    [Fact]
    public void MainViewModel_ArchiveExtractorSettings_PersistProperly()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        var settings = SettingsService.Instance.Settings;

        vm.AutoExtractArchives = true;
        Assert.True(settings.AutoExtractArchives);

        vm.LowResourceExtraction = true;
        Assert.True(settings.LowResourceExtraction == true);

        vm.DeleteArchiveAfterExtraction = true;
        Assert.True(settings.DeleteArchiveAfterExtraction);
        Assert.False(vm.MoveArchiveToRecycleBin);
        Assert.False(settings.MoveArchiveToRecycleBin);

        vm.MoveArchiveToRecycleBin = true;
        Assert.True(settings.MoveArchiveToRecycleBin);
        Assert.False(vm.DeleteArchiveAfterExtraction);
        Assert.False(settings.DeleteArchiveAfterExtraction);

        // When AutoExtractArchives is turned off, LowResourceExtraction is automatically reset to false
        vm.AutoExtractArchives = false;
        Assert.False(vm.AutoExtractArchives);
        Assert.False(vm.LowResourceExtraction);
        Assert.False(settings.LowResourceExtraction == true);
    }
}


