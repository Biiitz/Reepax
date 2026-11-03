using System.IO;
using System.Reflection;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public sealed class ProtectedStorageRecoveryTests : IDisposable
{
    private const string PrivateValue = "example-private-value";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ProtectedStorage_");
    private readonly string? _previousAppData = SettingsService.AppDataDirectoryOverride;
    private readonly string? _previousLogs = (string?)typeof(AppLogger)
        .GetField("_customLogsDirectory", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
    private readonly bool _previousLogging = AppLogger.IsLoggingEnabled;

    public ProtectedStorageRecoveryTests()
    {
        SettingsService.AppDataDirectoryOverride = _directory.FullName;
        AppLogger.LogsDirectory = Path.Combine(_directory.FullName, "logs");
        AppLogger.IsLoggingEnabled = false;
    }

    [Theory]
    [InlineData("settings", "changed")]
    [InlineData("downloads", "changed")]
    [InlineData("history", "changed")]
    [InlineData("settings", "truncated")]
    [InlineData("downloads", "truncated")]
    [InlineData("history", "truncated")]
    [InlineData("settings", "unknown")]
    [InlineData("downloads", "unknown")]
    [InlineData("history", "unknown")]
    [InlineData("settings", "empty")]
    [InlineData("downloads", "empty")]
    [InlineData("history", "empty")]
    public void InvalidProtectedFile_BlocksDefaultSaveAndCanBeOpenedAgainAfterRecovery(string kind, string damage)
    {
        string path = StoragePath(kind);
        string encrypted = SecureAppDataStorage.EncryptString(SampleJson(kind));
        string invalid = Damage(encrypted, damage);
        File.WriteAllText(path, invalid);
        byte[] before = File.ReadAllBytes(path);
        using var store = new Store(kind, path);
        Assert.True(store.LoadFailed);
        Assert.Null(store.PrivateData);
        store.Save();
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
        foreach (string retained in Directory.GetFiles(_directory.FullName, "*.corrupt_*.bak"))
            Assert.Equal(before, File.ReadAllBytes(retained));

        File.WriteAllText(path, encrypted);
        store.Reload();
        Assert.False(store.LoadFailed);
        Assert.Equal(PrivateValue, store.PrivateData);
        store.Save();
        AssertProtected(path);
        AssertProtected(path + ".bak");
        using var reopened = new Store(kind, path);
        Assert.False(reopened.LoadFailed);
        Assert.Equal(PrivateValue, reopened.PrivateData);
    }

    [Theory]
    [InlineData("settings", false)]
    [InlineData("downloads", false)]
    [InlineData("history", false)]
    [InlineData("settings", true)]
    [InlineData("downloads", true)]
    [InlineData("history", true)]
    public void BackupRecovery_FailedPublicationPreservesTheLastValidProtectedFile(string kind, bool invalidModel)
    {
        string path = StoragePath(kind);
        string encrypted = SecureAppDataStorage.EncryptString(SampleJson(kind));
        string invalid = invalidModel
            ? SecureAppDataStorage.EncryptString(kind == "settings" ? "{\"MaxConcurrentBackgroundDownloads\":{}}" : "[{\"Name\":{}}]")
            : Damage(encrypted, "changed");
        File.WriteAllText(path, invalid);
        File.WriteAllText(path + ".bak", encrypted);
        byte[] primary = File.ReadAllBytes(path);
        byte[] backup = File.ReadAllBytes(path + ".bak");
        using var store = new Store(kind, path);
        Assert.False(store.LoadFailed);
        Assert.Equal(PrivateValue, store.PrivateData);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Save();
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"));
        }
        using (var recovered = new Store(kind, path))
        {
            Assert.False(recovered.LoadFailed);
            Assert.Equal(PrivateValue, recovered.PrivateData);
        }
        store.Save();
        AssertProtected(path);
        AssertProtected(path + ".bak");
        using var reopened = new Store(kind, path);
        Assert.False(reopened.LoadFailed);
        Assert.Equal(PrivateValue, reopened.PrivateData);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("downloads")]
    [InlineData("history")]
    public void PlaintextMigration_InterruptedSaveCanBeRepeatedWithoutLeavingPlaintextBackups(string kind)
    {
        string path = StoragePath(kind);
        string plain = SampleJson(kind);
        File.WriteAllText(path, plain);
        byte[] original = File.ReadAllBytes(path);
        string encrypted = SecureAppDataStorage.EncryptString(plain);
        File.WriteAllText(path + ".tmp", encrypted[..(encrypted.Length / 2)]);
        using (var store = new Store(kind, path))
        {
            Assert.False(store.LoadFailed);
            Assert.Equal(PrivateValue, store.PrivateData);
            using (File.Open(path + ".tmp", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                store.Save();
                Assert.Equal(original, File.ReadAllBytes(path));
            }
        }
        using var retry = new Store(kind, path);
        Assert.False(retry.LoadFailed);
        Assert.Equal(PrivateValue, retry.PrivateData);
        retry.Save();
        AssertProtected(path);
        AssertProtected(path + ".bak");
        Assert.False(File.Exists(path + ".tmp"));
        retry.Save();
        AssertProtected(path);
        AssertProtected(path + ".bak");
        using var reopened = new Store(kind, path);
        Assert.False(reopened.LoadFailed);
        Assert.Equal(PrivateValue, reopened.PrivateData);
    }

    private string StoragePath(string kind) => Path.Combine(_directory.FullName, kind + ".json");

    [Theory]
    [InlineData("settings")]
    [InlineData("downloads")]
    [InlineData("history")]
    public void PlaintextBackupWithoutPrimary_IsRecoveredAndProtectedOnTheNextSave(string kind)
    {
        string path = StoragePath(kind);
        File.WriteAllText(path + ".bak", SampleJson(kind));
        using var store = new Store(kind, path);
        Assert.False(store.LoadFailed);
        Assert.Equal(PrivateValue, store.PrivateData);
        Assert.False(File.Exists(path));
        store.Save();
        AssertProtected(path);
        AssertProtected(path + ".bak");
        using var reopened = new Store(kind, path);
        Assert.False(reopened.LoadFailed);
        Assert.Equal(PrivateValue, reopened.PrivateData);
    }

    private string SampleJson(string kind)
    {
        if (kind == "settings")
            return JsonSerializer.Serialize(new AppSettings { Language = "de", ExtractionPasswords = [PrivateValue] });
        var package = new DownloadPackageDto
        {
            Id = Guid.Parse("d149eac5-75f9-4b6c-a1ae-47313780c401"), Name = "Protected sample", IsCustomName = true,
            SaveDirectory = Path.Combine(_directory.FullName, "planned"), AutoPar2Repair = false,
            Items = [new DownloadItemDto
            {
                Id = Guid.Parse("a199d404-caa1-41bf-aa00-a95c80ec97b9"), FileName = "payload.bin",
                OriginalUrl = "https://example.invalid/payload.bin", Cookies = PrivateValue, Status = DownloadStatus.Paused
            }]
        };
        return JsonSerializer.Serialize(new[] { package });
    }

    private static string Damage(string encrypted, string damage)
    {
        byte[] blob = Convert.FromBase64String(encrypted[SecureAppDataStorage.HeaderPrefix.Length..]);
        return damage switch
        {
            "changed" => ChangeByte(blob),
            "truncated" => SecureAppDataStorage.HeaderPrefix + Convert.ToBase64String(blob[..(blob.Length / 2)]),
            "unknown" => "RPX_UNKNOWN_V1:" + Convert.ToBase64String(blob),
            "empty" => "",
            _ => throw new ArgumentOutOfRangeException(nameof(damage))
        };
    }

    private static string ChangeByte(byte[] blob)
    {
        blob[^1] ^= 0x40;
        return SecureAppDataStorage.HeaderPrefix + Convert.ToBase64String(blob);
    }

    private static void AssertProtected(string path)
    {
        string content = File.ReadAllText(path);
        Assert.StartsWith(SecureAppDataStorage.HeaderPrefix, content);
        Assert.DoesNotContain(PrivateValue, content);
        Assert.Contains(PrivateValue, SecureAppDataStorage.DecryptString(content));
    }

    private sealed class Store : IDisposable
    {
        private readonly string _kind;
        private readonly SettingsService? _settings;
        private readonly DownloadPersistenceService? _persistence;
        private List<DownloadPackage> _packages = [];

        internal Store(string kind, string path)
        {
            _kind = kind;
            if (kind == "settings") _settings = new SettingsService(path);
            else
            {
                _persistence = new DownloadPersistenceService(Path.Combine(Path.GetDirectoryName(path)!, "downloads.json"));
                Reload();
            }
        }

        internal bool LoadFailed => _kind switch
        {
            "settings" => _settings!.HasSettingsLoadFailed,
            "downloads" => _persistence!.HasDownloadsLoadFailed,
            _ => _persistence!.HasHistoryLoadFailed
        };

        internal string? PrivateData => _kind == "settings"
            ? _settings!.Settings.ExtractionPasswords.SingleOrDefault()
            : _packages.SingleOrDefault()?.Items.SingleOrDefault()?.Cookies;

        internal void Reload()
        {
            if (_kind == "settings") _settings!.LoadSettings();
            else _packages = _kind == "downloads" ? _persistence!.LoadDownloads() : _persistence!.LoadHistory();
        }

        internal void Save()
        {
            if (_kind == "settings") _settings!.SaveSettings();
            else if (_kind == "downloads") _persistence!.SaveDownloads(_packages, sync: true);
            else _persistence!.SaveHistory(_packages, sync: true);
        }

        public void Dispose() => _persistence?.Dispose();
    }

    public void Dispose()
    {
        AppLogger.Flush();
        SettingsService.AppDataDirectoryOverride = _previousAppData;
        // Restore the unset override as well as explicitly configured directories.
        AppLogger.LogsDirectory = _previousLogs!;
        AppLogger.IsLoggingEnabled = _previousLogging;
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(_directory.Parent?.FullName, parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected temporary directory.");
        _directory.Delete(recursive: true);
    }
}
