using System;
using System.Diagnostics;
using System.IO;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class ShowFileInExplorerTests
{
    [Fact]
    public void ShowFileInExplorer_WhenFileExists_SelectsFileInExplorer()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            ProcessStartInfo? capturedPsi = null;
            bool result = MainWindow.ShowFileInExplorer(tempFile, psi => capturedPsi = psi);

            Assert.True(result);
            Assert.NotNull(capturedPsi);
            Assert.Equal("explorer.exe", capturedPsi.FileName);
            Assert.Equal($"/select,\"{Path.GetFullPath(tempFile)}\"", capturedPsi.Arguments);
            Assert.True(capturedPsi.UseShellExecute);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void ShowFileInExplorer_WhenFileDoesNotExist_ButParentDirectoryExists_OpensParentDirectory()
    {
        var tempDir = Path.GetTempPath();
        var nonExistentFile = Path.Combine(tempDir, $"missing_{Guid.NewGuid():N}.tmp");

        Assert.False(File.Exists(nonExistentFile));
        Assert.True(Directory.Exists(tempDir));

        ProcessStartInfo? capturedPsi = null;
        bool result = MainWindow.ShowFileInExplorer(nonExistentFile, psi => capturedPsi = psi);

        Assert.True(result);
        Assert.NotNull(capturedPsi);
        Assert.Equal("explorer.exe", capturedPsi.FileName);
        var expectedDir = Path.GetFullPath(Path.GetDirectoryName(nonExistentFile)!);
        Assert.Equal($"\"{expectedDir}\"", capturedPsi.Arguments);
        Assert.True(capturedPsi.UseShellExecute);
    }

    [Fact]
    public void ShowFileInExplorer_WhenDirectoryPathProvided_OpensDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            ProcessStartInfo? capturedPsi = null;
            bool result = MainWindow.ShowFileInExplorer(tempDir, psi => capturedPsi = psi);

            Assert.True(result);
            Assert.NotNull(capturedPsi);
            Assert.Equal("explorer.exe", capturedPsi.FileName);
            Assert.Equal($"\"{Path.GetFullPath(tempDir)}\"", capturedPsi.Arguments);
            Assert.True(capturedPsi.UseShellExecute);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void ShowFileInExplorer_WhenPathIsNullOrWhitespace_ReturnsFalse(string? path)
    {
        bool launcherCalled = false;
        bool result = MainWindow.ShowFileInExplorer(path, _ => launcherCalled = true);

        Assert.False(result);
        Assert.False(launcherCalled);
    }

    [Fact]
    public void ShowFileInExplorer_WhenPathAndParentDirectoryDoNotExist_ReturnsFalse()
    {
        var nonExistentPath = @"Z:\NonExistent_Directory_XYZ_123456\NonExistentFile.bin";

        bool launcherCalled = false;
        bool result = MainWindow.ShowFileInExplorer(nonExistentPath, _ => launcherCalled = true);

        Assert.False(result);
        Assert.False(launcherCalled);
    }

    [Fact]
    public void ShowFileInExplorer_WhenPathHasSurroundingQuotes_TrimsAndResolvesCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            ProcessStartInfo? capturedPsi = null;
            var quotedPath = $"\"{tempFile}\"";

            bool result = MainWindow.ShowFileInExplorer(quotedPath, psi => capturedPsi = psi);

            Assert.True(result);
            Assert.NotNull(capturedPsi);
            Assert.Equal("explorer.exe", capturedPsi.FileName);
            Assert.Equal($"/select,\"{Path.GetFullPath(tempFile)}\"", capturedPsi.Arguments);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void ShowFileInExplorer_WhenLauncherThrows_CatchesAndReturnsFalseGracefully()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            bool result = MainWindow.ShowFileInExplorer(tempFile, _ => throw new InvalidOperationException("Process start error"));

            Assert.False(result);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void Localization_MenuShowInExplorer_IsDefinedInGermanAndEnglish()
    {
        var loc = LocalizationService.Instance;
        var prevLang = loc.CurrentLanguage;

        try
        {
            loc.CurrentLanguage = "en";
            Assert.Equal("Show in folder", loc["Menu_ShowInExplorer"]);

            loc.CurrentLanguage = "de";
            Assert.Equal("Im Ordner anzeigen", loc["Menu_ShowInExplorer"]);
        }
        finally
        {
            loc.CurrentLanguage = prevLang;
        }
    }
}
