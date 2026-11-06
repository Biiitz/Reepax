using System.IO;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public sealed class RoamingMigrationTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("RoamingMigration_");
    private readonly string? _previousAppData = SettingsService.AppDataDirectoryOverride;
    private readonly string? _previousLogs = (string?)typeof(AppLogger)
        .GetField("_customLogsDirectory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null);
    private readonly bool _previousLogging = AppLogger.IsLoggingEnabled;
    private string Source => Path.Combine(_workspace.FullName, "roaming");
    private string Target => Path.Combine(_workspace.FullName, "local");

    public RoamingMigrationTests()
    {
        SettingsService.AppDataDirectoryOverride = Path.Combine(_workspace.FullName, "profile");
        AppLogger.LogsDirectory = Path.Combine(_workspace.FullName, "logs");
        AppLogger.IsLoggingEnabled = false;
    }

    public static IEnumerable<object[]> StoredFiles()
    {
        foreach (string file in new[] { "settings.json", "settings.json.bak", "downloads.json", "downloads.json.bak", "history.json", "history.json.bak" })
        {
            yield return new object[] { file, false };
            yield return new object[] { file, true };
        }
        yield return new object[] { "extensions.json", false };
    }

    [Theory]
    [MemberData(nameof(StoredFiles))]
    public void ExistingCurrentFile_SurvivesLargerLegacyFileAndRepeatedMigration(string file, bool encrypted)
    {
        string current = SampleJson(file, legacy: false);
        string legacy = SampleJson(file, legacy: true);
        if (encrypted)
        {
            current = SecureAppDataStorage.EncryptString(current);
            legacy = SecureAppDataStorage.EncryptString(legacy);
        }
        string sourcePath = Write(Source, file, legacy);
        string targetPath = Write(Target, file, current);
        File.SetLastWriteTimeUtc(sourcePath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(targetPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        byte[] original = File.ReadAllBytes(targetPath);
        byte[] old = File.ReadAllBytes(sourcePath);
        Assert.True(old.Length > original.Length);
        string unrelated = Write(Target, "unrelated.bin", "unrelated-data");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool result = SettingsService.MigrateFromRoamingInternal(Source, Target);
            Assert.Equal(original, File.ReadAllBytes(targetPath));
            Assert.Equal(old, File.ReadAllBytes(sourcePath));
            Assert.False(result);
            Assert.Equal("unrelated-data", File.ReadAllText(unrelated));
            Assert.Empty(Directory.GetFiles(Target, "*.tmp", SearchOption.AllDirectories));
        }

        string json = SecureAppDataStorage.DecryptString(File.ReadAllText(targetPath));
        if (file.StartsWith("settings"))
        {
            var reopened = new SettingsService(targetPath);
            Assert.False(reopened.HasSettingsLoadFailed);
            Assert.Equal("en", reopened.Settings.Language);
        }
        else if (file.StartsWith("downloads") || file.StartsWith("history"))
        {
            var package = Assert.Single(JsonSerializer.Deserialize<List<DownloadPackageDto>>(json)!);
            Assert.Equal("Current collection", package.Name);
        }
    }

    [Theory]
    [InlineData("settings.json", "{}")]
    [InlineData("downloads.json", "[]")]
    [InlineData("settings.json", "")]
    [InlineData("settings.json", "RPX_UNKNOWN_V1:example")]
    [InlineData("settings.json", "{\"Language\":\"en\"}")]
    public void ExistingSmallOrUnsupportedTarget_IsRetained(string file, string current)
    {
        string legacy = file == "settings.json" ? "{\"Language\":\"de\"}" : SampleJson(file, legacy: true);
        string sourcePath = Write(Source, file, legacy);
        string targetPath = Write(Target, file, current);
        byte[] original = File.ReadAllBytes(targetPath);
        Assert.False(SettingsService.MigrateFromRoamingInternal(Source, Target));
        Assert.Equal(original, File.ReadAllBytes(targetPath));
        Assert.Equal(legacy, File.ReadAllText(sourcePath));
    }

    [Fact]
    public void CaseVariantsOfKnownNames_AreMigratedAndRemovedOnlyAfterVerification()
    {
        string settings = Write(Source, "SETTINGS.JSON", "{\"Language\":\"en\"}");
        string manifest = Write(Source, "eXtEnSiOnS/sample/manifest.json", "{\"name\":\"Sample\"}");
        byte[] settingsBytes = File.ReadAllBytes(settings);
        byte[] manifestBytes = File.ReadAllBytes(manifest);
        Assert.True(SettingsService.MigrateFromRoamingInternal(Source, Target));
        Assert.Equal(settingsBytes, File.ReadAllBytes(Path.Combine(Target, "settings.json")));
        Assert.Equal(manifestBytes, File.ReadAllBytes(Path.Combine(Target, "Extensions/sample/manifest.json")));
        Assert.False(Directory.Exists(Source));
    }

    [Theory]
    [InlineData("Extensions/sample/manifest.json", "current")]
    [InlineData("Icons/sample.ico", "")]
    public void ConflictingDirectoryFile_PreservesBothCopies(string relativePath, string current)
    {
        string sourcePath = Write(Source, relativePath, "legacy-data");
        string targetPath = Write(Target, relativePath, current);
        Assert.False(SettingsService.MigrateFromRoamingInternal(Source, Target));
        Assert.Equal(current, File.ReadAllText(targetPath));
        Assert.Equal("legacy-data", File.ReadAllText(sourcePath));
    }

    [Theory]
    [InlineData("unrecognized.json")]
    [InlineData("WebView2/Default/Cookies")]
    public void UnrecognizedSourceData_IsRetainedWhileKnownFilesMigrate(string relativePath)
    {
        string retained = Write(Source, relativePath, "retained-data");
        string settings = Write(Source, "settings.json", SampleJson("settings.json", legacy: false));
        byte[] original = File.ReadAllBytes(settings);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.False(SettingsService.MigrateFromRoamingInternal(Source, Target));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(Target, "settings.json")));
            Assert.Equal(original, File.ReadAllBytes(settings));
            Assert.Equal("retained-data", File.ReadAllText(retained));
        }
    }

    [Fact]
    public void LockedSource_AfterPartialMigrationCanBeRetriedWithoutOverwriting()
    {
        string settings = Write(Source, "settings.json", SampleJson("settings.json", legacy: false));
        string downloads = Write(Source, "downloads.json", SampleJson("downloads.json", legacy: false));
        string extension = Write(Source, "Extensions/sample/manifest.json", "{\"name\":\"Sample\"}");
        byte[] settingsBytes = File.ReadAllBytes(settings);
        byte[] downloadBytes = File.ReadAllBytes(downloads);
        byte[] extensionBytes = File.ReadAllBytes(extension);
        using (File.Open(downloads, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(SettingsService.MigrateFromRoamingInternal(Source, Target));
            Assert.Equal(settingsBytes, File.ReadAllBytes(Path.Combine(Target, "settings.json")));
            Assert.False(File.Exists(Path.Combine(Target, "downloads.json")));
            Assert.Empty(Directory.GetFiles(Target, "*.tmp", SearchOption.AllDirectories));
            Assert.True(File.Exists(settings));
        }
        Assert.True(SettingsService.MigrateFromRoamingInternal(Source, Target));
        Assert.False(Directory.Exists(Source));
        Assert.Equal(settingsBytes, File.ReadAllBytes(Path.Combine(Target, "settings.json")));
        Assert.Equal(downloadBytes, File.ReadAllBytes(Path.Combine(Target, "downloads.json")));
        Assert.Equal(extensionBytes, File.ReadAllBytes(Path.Combine(Target, "Extensions/sample/manifest.json")));
        Assert.True(SettingsService.MigrateFromRoamingInternal(Source, Target));
        Assert.Equal(downloadBytes, File.ReadAllBytes(Path.Combine(Target, "downloads.json")));
    }

    [Fact]
    public void FailedPublication_DoesNotLeavePartialTargetAndCanBeRetried()
    {
        string source = Write(Source, "downloads.json", SampleJson("downloads.json", legacy: false));
        byte[] original = File.ReadAllBytes(source);
        string blockedTarget = Path.Combine(Target, "downloads.json");
        Directory.CreateDirectory(blockedTarget);
        Assert.False(SettingsService.MigrateFromRoamingInternal(Source, Target));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.True(Directory.Exists(blockedTarget));
        Assert.Empty(Directory.GetFiles(Target, "*.tmp", SearchOption.AllDirectories));
        Directory.Delete(blockedTarget);
        Assert.True(SettingsService.MigrateFromRoamingInternal(Source, Target));
        Assert.Equal(original, File.ReadAllBytes(blockedTarget));
        Assert.False(Directory.Exists(Source));
    }

    private static string SampleJson(string file, bool legacy)
    {
        if (file.StartsWith("settings"))
            return JsonSerializer.Serialize(new AppSettings
            {
                DefaultDownloadDirectory = @"C:\Downloads",
                Language = legacy ? "de" : "en",
                ExtractionPasswords = legacy ? [new string('x', 4096)] : []
            });
        if (file.StartsWith("extensions"))
            return JsonSerializer.Serialize(new { sample = new { IsEnabled = !legacy, ExtensionId = legacy ? new string('x', 4096) : "current" } });
        return JsonSerializer.Serialize(new[] { new DownloadPackageDto
        {
            Id = Guid.Parse("0d86b0d5-9b9b-41fa-b9b3-823c69962767"),
            Name = legacy ? new string('x', 4096) : "Current collection", Items = []
        } });
    }

    private static string Write(string directory, string relativePath, string content)
    {
        string path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        AppLogger.Flush();
        SettingsService.AppDataDirectoryOverride = _previousAppData;
        AppLogger.LogsDirectory = _previousLogs!;
        AppLogger.IsLoggingEnabled = _previousLogging;
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(_workspace.Parent?.FullName, parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected temporary directory.");
        _workspace.Delete(recursive: true);
    }
}
