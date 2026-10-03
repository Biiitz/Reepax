using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class ExtractionErrorClassifierTests : IDisposable
{
    private readonly string _tempDirectory;

    public ExtractionErrorClassifierTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "Reepax_ExtrErrTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch { }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_tempDirectory, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public void FindMissingVolume_PartRarWithGap_ReturnsFirstMissingPart()
    {
        var primary = Touch("game.part01.rar");
        Touch("game.part03.rar");

        Assert.Equal("game.part02.rar", ExtractionErrorClassifier.FindMissingVolume(primary));
    }

    [Fact]
    public void FindMissingVolume_CompletePartRarSequence_ReturnsNull()
    {
        var primary = Touch("game.part1.rar");
        Touch("game.part2.rar");
        Touch("game.part3.rar");

        Assert.Null(ExtractionErrorClassifier.FindMissingVolume(primary));
    }

    [Fact]
    public void FindMissingVolume_LegacyRarWithGap_ReturnsMissingRNumber()
    {
        var primary = Touch("movie.rar");
        Touch("movie.r00");
        Touch("movie.r02");

        Assert.Equal("movie.r01", ExtractionErrorClassifier.FindMissingVolume(primary));
    }

    [Fact]
    public void FindMissingVolume_SevenZipSplitWithGap_ReturnsMissingVolume()
    {
        var primary = Touch("data.7z.001");
        Touch("data.7z.003");

        Assert.Equal("data.7z.002", ExtractionErrorClassifier.FindMissingVolume(primary));
    }

    [Fact]
    public void FindMissingVolume_GuessNext_ReturnsVolumeAfterLastExisting()
    {
        var primary = Touch("pack.part01.rar");
        Touch("pack.part02.rar");

        Assert.Null(ExtractionErrorClassifier.FindMissingVolume(primary));
        Assert.Equal("pack.part03.rar", ExtractionErrorClassifier.FindMissingVolume(primary, guessNextVolume: true));
    }

    [Fact]
    public void FindMissingVolume_StandaloneArchive_ReturnsNull()
    {
        var primary = Touch("single.zip");

        Assert.Null(ExtractionErrorClassifier.FindMissingVolume(primary));
    }

    [Fact]
    public void Classify_DiskFullIOException_ReturnsDiskFull()
    {
        var ex = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));

        Assert.Equal(ExtractionErrorKind.DiskFull, ExtractionErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_CryptographicException_ReturnsWrongPassword()
    {
        Assert.Equal(ExtractionErrorKind.WrongPassword,
            ExtractionErrorClassifier.Classify(new CryptographicException("bad key")));
    }

    [Fact]
    public void Classify_InvalidDataAndCrcErrors_ReturnCorruptArchive()
    {
        Assert.Equal(ExtractionErrorKind.CorruptArchive,
            ExtractionErrorClassifier.Classify(new InvalidDataException("Central directory corrupt.")));
        Assert.Equal(ExtractionErrorKind.CorruptArchive,
            ExtractionErrorClassifier.Classify(new InvalidOperationException("rar crc mismatch")));
    }

    [Fact]
    public void Classify_MissingVolumeMessage_ReturnsMissingVolume()
    {
        Assert.Equal(ExtractionErrorKind.MissingVolume,
            ExtractionErrorClassifier.Classify(new InvalidOperationException("Unable to open next volume.")));
    }

    [Fact]
    public void Classify_UnknownException_ReturnsGeneric()
    {
        Assert.Equal(ExtractionErrorKind.Generic,
            ExtractionErrorClassifier.Classify(new InvalidOperationException("Zip Slip Sicherheitsrisiko erkannt.")));
    }

    [Fact]
    public void IsExtractionErrorStatus_RecognizesDetailedMessagesInBothLanguages()
    {
        foreach (var map in new[] { Strings_de.Map, Strings_en.Map })
        {
            Assert.True(ExtractionErrorClassifier.IsExtractionErrorStatus(map["Status_ExtractionPasswordProtected"]));
            Assert.True(ExtractionErrorClassifier.IsExtractionErrorStatus(map["Status_ExtractionDiskFull"]));
            Assert.True(ExtractionErrorClassifier.IsExtractionErrorStatus(string.Format(map["Status_ExtractionCorruptArchive"], "a.part02.rar")));
            Assert.True(ExtractionErrorClassifier.IsExtractionErrorStatus(string.Format(map["Status_ExtractionMissingVolume"], "a.part02.rar")));
            Assert.True(ExtractionErrorClassifier.IsExtractionErrorStatus(string.Format(map["Status_ExtractionInsufficientDiskSpace"], "5 GB", "1 GB")));
            Assert.True(ExtractionErrorClassifier.IsExtractionErrorStatus(string.Format(map["Status_ExtractionFailed"], "boom")));
        }

        Assert.False(ExtractionErrorClassifier.IsExtractionErrorStatus(Loc.Get("Status_Completed")));
        Assert.False(ExtractionErrorClassifier.IsExtractionErrorStatus(Loc.Get("Status_CompletedAndExtracted")));
        Assert.False(ExtractionErrorClassifier.IsExtractionErrorStatus(null));
    }

    [Fact]
    public void DetailedErrorStatusKeys_ExistInBothLanguages()
    {
        string[] keys =
        {
            "Status_ExtractionPasswordProtected", "Status_ExtractionDiskFull", "Status_ExtractionCorruptArchive",
            "Status_ExtractionMissingVolume", "Status_ExtractionMissingVolumeUnknown"
        };

        foreach (var key in keys)
        {
            Assert.True(Strings_de.Map.ContainsKey(key), $"Missing German string: {key}");
            Assert.True(Strings_en.Map.ContainsKey(key), $"Missing English string: {key}");
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_MultiPartWithMissingPart_ReportsMissingPartName()
    {
        var primary = Touch("broken.part01.rar");
        Touch("broken.part03.rar");

        string? lastStatus = null;
        var result = await ArchiveExtractionService.Instance.ExtractArchiveAsync(
            primary, Path.Combine(_tempDirectory, "out"), status => lastStatus = status);

        Assert.False(result);
        Assert.Equal(Loc.Format("Status_ExtractionMissingVolume", "broken.part02.rar"), lastStatus);
    }

    [Fact]
    public async Task ExtractArchiveAsync_CorruptZip_ReportsCorruptArchiveInsteadOfPasswordPrompt()
    {
        var archivePath = Path.Combine(_tempDirectory, "garbage.zip");
        var rnd = new Random(1234);
        var bytes = new byte[2048];
        rnd.NextBytes(bytes);
        // Valid local file header signature but garbage afterwards
        bytes[0] = 0x50; bytes[1] = 0x4B; bytes[2] = 0x03; bytes[3] = 0x04;
        File.WriteAllBytes(archivePath, bytes);

        var originalHandler = ArchiveExtractionService.PasswordPromptHandler;
        var promptInvoked = false;
        ArchiveExtractionService.PasswordPromptHandler = _ =>
        {
            promptInvoked = true;
            return Task.FromResult<(string?, bool)>((null, false));
        };

        try
        {
            string? lastStatus = null;
            var result = await ArchiveExtractionService.Instance.ExtractArchiveAsync(
                archivePath, Path.Combine(_tempDirectory, "out2"), status => lastStatus = status);

            Assert.False(result);
            Assert.False(promptInvoked);
            Assert.NotNull(lastStatus);
            Assert.NotEqual(Loc.Get("Status_ExtractionPasswordProtected"), lastStatus);
            Assert.True(ExtractionErrorClassifier.IsExtractionErrorStatus(lastStatus));
        }
        finally
        {
            ArchiveExtractionService.PasswordPromptHandler = originalHandler;
        }
    }

    [Fact]
    public void DownloadPackage_RecalculateAggregates_KeepsDetailedExtractionErrorStatus()
    {
        var package = new DownloadPackage { Name = "Pkg" };
        var item = new DownloadItem
        {
            FileName = "a.part01.rar",
            Status = DownloadStatus.Completed,
            IsEnabled = true,
            TotalBytes = 10,
            DownloadedBytes = 10
        };
        package.Items.Add(item);

        var message = Loc.Format("Status_ExtractionMissingVolume", "a.part02.rar");
        package.RecalculateAggregates();
        package.StatusMessage = message;
        package.RecalculateAggregates();

        Assert.Equal(message, package.StatusMessage);
    }
}
