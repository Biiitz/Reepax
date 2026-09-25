using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reepax.Models;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public class PackageGrouperTests
{
    private readonly string _testBaseFolder = @"C:\Downloads\Test";

    [Fact]
    public void CleanPackageBaseName_StripsPartNumbersAndHashes()
    {
        // Arrange
        var hoster = new HosterInfo { DisplayName = "Rapidgator" };

        // Act & Assert
        Assert.Equal("Movie Title 2024 1080p", 
            PackageGrouper.CleanPackageBaseName("Movie.Title.2024.1080p.part01.rar", "https://rapidgator.net/file/123/Movie.Title.2024.1080p.part01.rar", hoster));

        Assert.Equal("Linux Ubuntu 24 04 LTS", 
            PackageGrouper.CleanPackageBaseName("Linux_Ubuntu_24.04_LTS_part2.rar", "https://rapidgator.net/file/123/Linux_Ubuntu_24.04_LTS_part2.rar", hoster));

        Assert.Equal("Sample Game Release", 
            PackageGrouper.CleanPackageBaseName("Sample.Game.Release.7z.001", "https://rapidgator.net/file/123/Sample.Game.Release.7z.001", hoster));
    }

    [Fact]
    public void GroupLinksIntoPackages_GroupsMultiPartArchivesIntoSinglePackage()
    {
        // Arrange
        var links = new List<ExtractedLink>
        {
            new() { Url = "https://rapidgator.net/file/1/Archive.2024.part01.rar", RawFileName = "Archive.2024.part01.rar", Hoster = new HosterInfo { DisplayName = "Rapidgator" } },
            new() { Url = "https://rapidgator.net/file/2/Archive.2024.part02.rar", RawFileName = "Archive.2024.part02.rar", Hoster = new HosterInfo { DisplayName = "Rapidgator" } },
            new() { Url = "https://rapidgator.net/file/3/Archive.2024.part03.rar", RawFileName = "Archive.2024.part03.rar", Hoster = new HosterInfo { DisplayName = "Rapidgator" } }
        };

        // Act
        var packages = PackageGrouper.GroupLinksIntoPackages(links, _testBaseFolder);

        // Assert
        Assert.Single(packages);
        var pkg = packages[0];
        Assert.Equal("Archive 2024", pkg.Name);
        Assert.Equal(3, pkg.Items.Count);
        Assert.Equal(Path.Combine(_testBaseFolder, "Archive 2024"), pkg.SaveDirectory);
        Assert.All(pkg.Items, item => Assert.StartsWith(pkg.SaveDirectory, item.SaveFilePath!));
    }

    [Fact]
    public void GroupLinksIntoPackages_SeparatesDistinctReleases_WhenContextTitlesDiffer()
    {
        // Arrange
        var links = new List<ExtractedLink>
        {
            new() { Url = "https://rg.to/1/Album_Rock_2024.zip", RawFileName = "Album_Rock_2024.zip", ContextTitle = "Album Rock 2024", Hoster = new HosterInfo { DisplayName = "Rapidgator" } },
            new() { Url = "https://rg.to/2/Doc_Tutorial.pdf", RawFileName = "Doc_Tutorial.pdf", ContextTitle = "Doc Tutorial", Hoster = new HosterInfo { DisplayName = "Rapidgator" } }
        };

        // Act
        var packages = PackageGrouper.GroupLinksIntoPackages(links, _testBaseFolder);

        // Assert
        Assert.Equal(2, packages.Count);
        Assert.Contains(packages, p => p.Name == "Album Rock 2024");
        Assert.Contains(packages, p => p.Name == "Doc Tutorial");
    }

    [Fact]
    public void MakeSafeDirectoryName_ComprehensiveDosAndPathInjectionSanitization()
    {
        string[] dosNames = { "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        foreach (var dos in dosNames)
        {
            Assert.Equal("Download_Paket", PackageGrouper.MakeSafeDirectoryName(dos));
            Assert.Equal("Download_Paket", PackageGrouper.MakeSafeDirectoryName(dos.ToLowerInvariant()));
            Assert.Equal("Download_Paket", PackageGrouper.MakeSafeDirectoryName($"{dos}.tar.gz"));
            Assert.Equal("Download_Paket", PackageGrouper.MakeSafeDirectoryName($"  {dos}  "));
        }

        // Path traversal and illegal chars
        Assert.Equal("sample_folder", PackageGrouper.MakeSafeDirectoryName("../../sample/folder"));
        Assert.Equal("C_Windows_System32", PackageGrouper.MakeSafeDirectoryName(@"C:\Windows\System32"));
        Assert.Equal("server_share_data", PackageGrouper.MakeSafeDirectoryName(@"\\server\share\data"));
        Assert.Equal("invalid_name_chars", PackageGrouper.MakeSafeDirectoryName("invalid:name*chars?|<>\0"));
        Assert.Equal("Download_Paket", PackageGrouper.MakeSafeDirectoryName(null!));
        Assert.Equal("Download_Paket", PackageGrouper.MakeSafeDirectoryName("   "));
        Assert.Equal("Download_Paket", PackageGrouper.MakeSafeDirectoryName("....."));
    }

    [Fact]
    public void MakeSafeFileName_ComprehensiveDosAndPathTraversalSanitization()
    {
        string[] dosNames = { "CON", "PRN", "AUX", "NUL", "COM1", "COM9", "LPT1", "LPT8" };
        foreach (var dos in dosNames)
        {
            Assert.Equal($"_{dos}", PackageGrouper.MakeSafeFileName(dos));
            Assert.Equal($"_{dos.ToLowerInvariant()}", PackageGrouper.MakeSafeFileName(dos.ToLowerInvariant()));
            Assert.Equal($"_{dos}.zip", PackageGrouper.MakeSafeFileName($"{dos}.zip"));
            Assert.Equal($"_{dos.ToLowerInvariant()}.part01.rar", PackageGrouper.MakeSafeFileName($"{dos.ToLowerInvariant()}.part01.rar"));
        }

        Assert.Equal("installer.exe", PackageGrouper.MakeSafeFileName("../../installer.exe"));
        Assert.Equal("my_video_1080p.mp4", PackageGrouper.MakeSafeFileName("my:video*1080p.mp4"));
        Assert.Equal("file.bin", PackageGrouper.MakeSafeFileName(null!));
        Assert.Equal("file.bin", PackageGrouper.MakeSafeFileName("   "));
        Assert.Equal("file.bin", PackageGrouper.MakeSafeFileName("....."));
    }

    [Fact]
    public void CreatePackage_WithDosDeviceNameOrPathTraversal_GuaranteesSafePackageAndItemPaths()
    {
        var links = new List<ExtractedLink>
        {
            new()
            {
                Url = "https://example.com/file",
                RawFileName = "AUX.zip",
                Hoster = new HosterInfo { DisplayName = "Example" }
            }
        };

        // 1. DOS device name as package name
        var pkgDos = PackageGrouper.CreatePackage("CON", links, _testBaseFolder);
        Assert.Equal("Download_Paket", pkgDos.Name);
        Assert.Equal(Path.Combine(_testBaseFolder, "Download_Paket"), pkgDos.SaveDirectory);
        Assert.Equal("_AUX.zip", pkgDos.Items[0].FileName);
        Assert.Equal(Path.Combine(pkgDos.SaveDirectory, "_AUX.zip"), pkgDos.Items[0].SaveFilePath);
        Assert.StartsWith(_testBaseFolder, pkgDos.Items[0].SaveFilePath!);

        // 2. Directory traversal in package name and filename
        var traversalLinks = new List<ExtractedLink>
        {
            new()
            {
                Url = "https://example.com/file2",
                RawFileName = "../../custom_setup.exe",
                Hoster = new HosterInfo { DisplayName = "Example" }
            }
        };
        var pkgTraversal = PackageGrouper.CreatePackage("../../Windows/System32", traversalLinks, _testBaseFolder);
        Assert.Equal("Windows_System32", pkgTraversal.Name);
        Assert.Equal(Path.Combine(_testBaseFolder, "Windows_System32"), pkgTraversal.SaveDirectory);
        Assert.Equal("custom_setup.exe", pkgTraversal.Items[0].FileName);
        Assert.Equal(Path.Combine(pkgTraversal.SaveDirectory, "custom_setup.exe"), pkgTraversal.Items[0].SaveFilePath);
        Assert.StartsWith(_testBaseFolder, pkgTraversal.Items[0].SaveFilePath!);
    }

    [Fact]
    public void DirectLinkToken_DoesNotBecomeFolderNameOrPackageName()
    {
        string token1 = "y9PN0SnSOrlAL+VPqdI+HnfkzH7uS7rpsC8nKLtiMlZPDgPin0Ar-tWT9ZJ-fD6lDCMmQ3FhrA2B79qnCIhoC7LNv8IkTwElQaYaGMRKfHEXQkWCzJW8pfEf5MkNHb7TJ8xkKDkqG5dskmlx6s9jBU7m7yJLevY";
        string token2 = "a8K01mNoPqRsT+UVwXy+ZAbcdEfghIjklMnopQrStUvWxYz0123456789-abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

        var rawInput = $"https://dl1.{FastHostResolver.CanonicalDomain}/dl/{token1}\nhttps://dl1.{FastHostResolver.CanonicalDomain}/dl/{token2}";
        var links = LinkExtractor.ExtractLinks(rawInput);

        Assert.Equal(2, links.Count);
        Assert.Equal("download_file", links[0].RawFileName);
        Assert.Equal("download_file", links[1].RawFileName);

        var packages = PackageGrouper.GroupLinksIntoPackages(links, _testBaseFolder);
        Assert.Single(packages);

        var pkg = packages[0];
        // The package name and save directory must NOT be the garbage token!
        Assert.DoesNotContain(token1, pkg.Name);
        Assert.DoesNotContain(token1, pkg.SaveDirectory);
        Assert.DoesNotContain("y9PN0SnS", pkg.SaveDirectory);

        // Safe fallback name is used initially
        Assert.Contains("FastHost", pkg.Name);
        Assert.Contains("FastHost", pkg.SaveDirectory);
        Assert.Equal(2, pkg.Items.Count);

        // File names are also clean parts, not tokens
        Assert.DoesNotContain("y9PN0SnS", pkg.Items[0].FileName);
        Assert.EndsWith(".part01.rar", pkg.Items[0].FileName);
        Assert.EndsWith(".part02.rar", pkg.Items[1].FileName);

        // When metadata resolver discovers real filenames from Content-Disposition:
        pkg.Items[0].FileName = "Elden.Ring.part01.rar";
        pkg.Items[1].FileName = "Elden.Ring.part02.rar";

        LinkMetadataResolverService.TryUpdatePackageName(pkg);

        Assert.Equal("Elden Ring", pkg.Name);
        Assert.Equal(Path.Combine(_testBaseFolder, "Elden Ring"), pkg.SaveDirectory);
    }

    [Fact]
    public void Persistence_AutoHeals_CrypticSaveDirectory()
    {
        string token = "y9PN0SnSOrlAL VPqdI HnfkzH7uS7rpsC8nKLtiMlZPDgPin0Ar-tWT9ZJ-fD6lDCMmQ3FhrA2B79qnCIhoC7LNv8IkTwElQaYaGMRKfHEXQkWCzJW8pfEf5MkNHb7TJ8xkKDkqG5dskmlx6s9jBU7m7yJLevY";
        string corruptedSaveDir = Path.Combine(_testBaseFolder, token);
        string cleanPackageName = "Cyberpunk 2077";

        var healedDir = Services.Storage.DownloadPersistenceService.SanitizeSavedPackageDirectory(corruptedSaveDir, cleanPackageName);
        Assert.Equal(Path.Combine(_testBaseFolder, "Cyberpunk 2077"), healedDir);
    }
}
