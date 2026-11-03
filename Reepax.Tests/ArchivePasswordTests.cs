using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using SharpCompress.Archives;
using SharpCompress.Readers;
using Xunit;

namespace Reepax.Tests;

public partial class ArchivePasswordTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Func<string, Task<(string? Password, bool Remember)>>? _originalPromptHandler;
    private readonly List<string> _originalSavedPasswords;
    private readonly List<ArchivePasswordEntry> _originalPasswordStatistics;

    public ArchivePasswordTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "Reepax_PwdTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _originalPromptHandler = ArchiveExtractionService.PasswordPromptHandler;
        _originalPasswordStatistics = SettingsService.Instance.Settings.ArchivePasswordStatistics.Select(entry => entry.Snapshot()).ToList();
        _originalSavedPasswords = SettingsService.Instance.Settings.ExtractionPasswords != null
            ? new List<string>(SettingsService.Instance.Settings.ExtractionPasswords)
            : new List<string>();
    }

    public void Dispose()
    {
        ArchiveExtractionService.PasswordPromptHandler = _originalPromptHandler;
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string>(_originalSavedPasswords);
        SettingsService.Instance.Settings.ArchivePasswordStatistics = _originalPasswordStatistics;

        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                var fullDirectory = Path.GetFullPath(_tempDirectory);
                if (Path.GetDirectoryName(fullDirectory) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                    !Path.GetFileName(fullDirectory).StartsWith("Reepax_PwdTests_", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected test directory.");
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void PasswordDetails_LegacySettingsKeepUnknownDateAndZeroCounters()
    {
        var settingsPath = Path.Combine(_tempDirectory, "legacy.json");
        File.WriteAllText(settingsPath, "{\"ExtractionPasswords\":[\" LegacyKey \",\"LegacyKey\",\"legacykey\"]}");
        var service = new SettingsService(settingsPath);
        var entries = service.GetArchivePasswordEntries();
        Assert.Equal(new[] { " LegacyKey ", "LegacyKey", "legacykey" }, entries.Select(entry => entry.Password));
        Assert.All(entries, entry =>
        {
            Assert.Null(entry.AddedAt);
            Assert.Equal(0, entry.UsageCount);
            Assert.Equal(0, entry.SuccessCount);
        });
        service.SaveSettings();
        Assert.All(new SettingsService(settingsPath).GetArchivePasswordEntries(), entry => Assert.Null(entry.AddedAt));
    }

    [Fact]
    public void PasswordDetails_PersistAndDoNotApplyOldSuccessToRemovedAndReaddedPassword()
    {
        var settingsPath = Path.Combine(_tempDirectory, "details.json");
        var service = new SettingsService(settingsPath);
        var before = DateTimeOffset.UtcNow;
        service.RememberArchivePassword("KnownKey");
        var token = service.RecordArchivePasswordAttempt("KnownKey")!;
        service.RecordArchivePasswordSuccess(token);
        var entry = Assert.Single(service.GetArchivePasswordEntries());
        Assert.InRange(entry.AddedAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.Equal(1, entry.UsageCount);
        Assert.Equal(1, entry.SuccessCount);
        service.SaveSettings();
        var loaded = Assert.Single(new SettingsService(settingsPath).GetArchivePasswordEntries());
        Assert.Equal(entry.AddedAt, loaded.AddedAt);
        Assert.Equal(1, loaded.UsageCount);
        Assert.Equal(1, loaded.SuccessCount);
        service.ReplaceArchivePasswords(Array.Empty<string>());
        service.RememberArchivePassword("KnownKey");
        service.RecordArchivePasswordSuccess(token);
        var readded = Assert.Single(service.GetArchivePasswordEntries());
        Assert.Equal(0, readded.UsageCount);
        Assert.Equal(0, readded.SuccessCount);
        service.SaveSettings();
    }

    [Fact]
    public void AddCommand_RejectsDuplicatesWithoutClearingInputAndReenablesAfterRemoval()
    {
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string>();
        SettingsService.Instance.Settings.ArchivePasswordStatistics = new();
        var vm = new Reepax.ViewModels.MainViewModel();
        Assert.False(vm.AddArchivePasswordCommand.CanExecute(null));
        vm.NewArchivePasswordInput = " KnownKey ";
        Assert.True(vm.AddArchivePasswordCommand.CanExecute(null));
        vm.AddArchivePasswordCommand.Execute(null);
        var added = Assert.Single(vm.ArchivePasswordEntries);
        Assert.NotNull(added.AddedAt);
        vm.NewArchivePasswordInput = " KnownKey ";
        Assert.False(vm.CanAddArchivePassword);
        Assert.False(vm.AddArchivePasswordCommand.CanExecute(null));
        vm.AddArchivePassword();
        Assert.Equal(" KnownKey ", vm.NewArchivePasswordInput);
        Assert.Single(vm.ArchivePasswordEntries);
        Assert.Equal(added.AddedAt, vm.ArchivePasswordEntries[0].AddedAt);
        vm.NewArchivePasswordInput = "knownkey";
        Assert.True(vm.CanAddArchivePassword);
        vm.NewArchivePasswordInput = " KnownKey ";
        vm.RemoveArchivePassword(" KnownKey ");
        Assert.True(vm.AddArchivePasswordCommand.CanExecute(null));
        SettingsService.Instance.RememberArchivePassword(" KnownKey ");
        Assert.False(vm.AddArchivePasswordCommand.CanExecute(null));
        Assert.Single(vm.ArchivePasswordEntries);
    }

    [Fact]
    public async Task PasswordUsage_CountsActualAttemptsAndOnlyCompletedExtractionsAsSuccess()
    {
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string> { "WrongKey", "KnownKey", "UnusedKey" };
        SettingsService.Instance.Settings.ArchivePasswordStatistics = new();
        var archive = Path.Combine(_tempDirectory, "encrypted.zip");
        File.WriteAllBytes(archive, CreateEncryptedZip("KnownKey", "payload.txt", "sample payload"));
        var output = Path.Combine(_tempDirectory, "output");
        ArchiveExtractionService.PasswordPromptHandler = _ => throw new InvalidOperationException("Unexpected password prompt.");
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, output));
        var entries = SettingsService.Instance.GetArchivePasswordEntries();
        Assert.Equal(1, entries[0].UsageCount);
        Assert.Equal(0, entries[0].SuccessCount);
        Assert.Equal(1, entries[1].UsageCount);
        Assert.Equal(1, entries[1].SuccessCount);
        Assert.Equal(0, entries[2].UsageCount);
        Assert.True(ArchiveExtractionService.TryTestArchivePassword(archive, "KnownKey", out _));
        Assert.Equal(1, SettingsService.Instance.GetArchivePasswordEntries()[1].UsageCount);
        using var cancellation = new CancellationTokenSource();
        var cancelled = await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, Path.Combine(_tempDirectory, "cancelled"),
            progressCallback: _ => cancellation.Cancel(),
            cancellationToken: cancellation.Token, lowResourceMode: false);
        Assert.False(cancelled);
        var after = SettingsService.Instance.GetArchivePasswordEntries()[1];
        Assert.Equal(2, after.UsageCount);
        Assert.Equal(1, after.SuccessCount);
    }

    [Fact]
    public async Task UnencryptedArchive_DoesNotConsumeOrCountSavedPasswords()
    {
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string> { "KnownKey" };
        SettingsService.Instance.Settings.ArchivePasswordStatistics = new();
        var archive = Path.Combine(_tempDirectory, "plain.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("payload.txt").Open())) writer.Write("sample payload");
        Assert.True(await ArchiveExtractionService.Instance.ExtractArchiveAsync(archive, Path.Combine(_tempDirectory, "plain")));
        var entry = Assert.Single(SettingsService.Instance.GetArchivePasswordEntries());
        Assert.Equal(0, entry.UsageCount);
        Assert.Equal(0, entry.SuccessCount);
    }

    [Fact]
    public void PasswordCounters_ConcurrentUpdatesAreNotLostAndUiRowsRemainStable()
    {
        var path = Path.Combine(_tempDirectory, "concurrent.json");
        var service = new SettingsService(path);
        service.RememberArchivePassword("KnownKey");
        Parallel.For(0, 24, _ =>
        {
            var token = service.RecordArchivePasswordAttempt("KnownKey")!;
            service.RecordArchivePasswordSuccess(token);
        });
        service.SaveSettings();
        var stored = Assert.Single(new SettingsService(path).GetArchivePasswordEntries());
        Assert.Equal(24, stored.UsageCount);
        Assert.Equal(24, stored.SuccessCount);
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string>();
        SettingsService.Instance.Settings.ArchivePasswordStatistics = new();
        var vm = new Reepax.ViewModels.MainViewModel();
        SettingsService.Instance.RememberArchivePassword("KnownKey");
        var row = Assert.Single(vm.ArchivePasswordEntries);
        SettingsService.Instance.RecordArchivePasswordAttempt("KnownKey");
        Assert.Same(row, Assert.Single(vm.ArchivePasswordEntries));
        Assert.Equal(1, row.UsageCount);
    }

    private static uint Crc32(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (crc >> 1) ^ (0xEDB88320u * (crc & 1));
        return crc;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = Crc32(crc, b);
        return ~crc;
    }

    /// <summary>
    /// Creates a valid PKWARE traditional encrypted ZIP archive for unit testing.
    /// </summary>
    internal static byte[] CreateEncryptedZip(string password, string entryName, string textContent)
    {
        byte[] contentBytes = System.Text.Encoding.UTF8.GetBytes(textContent);
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(entryName);
        uint crc = ComputeCrc32(contentBytes);

        var type = typeof(SharpCompress.Common.ArchiveException).Assembly.GetType("SharpCompress.Common.Zip.PkwareTraditionalEncryptionData")!;
        var encoding = new SharpCompress.Common.ArchiveEncoding();
        var encryptor = Activator.CreateInstance(type, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new object[] { password, encoding }, null)!;
        var encryptMethod = type.GetMethod("Encrypt", new[] { typeof(byte[]), typeof(int) })!;

        // 12-byte PKWARE encryption header (11 pseudo-random bytes + 1 verification byte)
        byte[] encHeader = new byte[12];
        for (int i = 0; i < 11; i++) encHeader[i] = (byte)(0x55 + i);
        encHeader[11] = (byte)(crc >> 24); // verification byte = high byte of CRC-32

        byte[] payload = new byte[12 + contentBytes.Length];
        Array.Copy(encHeader, 0, payload, 0, 12);
        Array.Copy(contentBytes, 0, payload, 12, contentBytes.Length);

        byte[] encryptedData = (byte[])encryptMethod.Invoke(encryptor, new object[] { payload, payload.Length })!;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        uint compSize = (uint)encryptedData.Length;
        uint uncompSize = (uint)contentBytes.Length;

        // Local file header
        w.Write(0x04034b50); // Signature
        w.Write((ushort)20); // Version needed (2.0)
        w.Write((ushort)1);  // Flags: 1 = encrypted
        w.Write((ushort)0);  // Compression: 0 = Stored
        w.Write((ushort)0);  // Mod time
        w.Write((ushort)0);  // Mod date
        w.Write(crc);        // CRC-32
        w.Write(compSize);   // Compressed size
        w.Write(uncompSize); // Uncompressed size
        w.Write((ushort)nameBytes.Length);
        w.Write((ushort)0);  // Extra field len
        w.Write(nameBytes);
        w.Write(encryptedData);

        long cdOffset = ms.Position;

        // Central Directory header
        w.Write(0x02014b50); // Signature
        w.Write((ushort)20); // Version made by
        w.Write((ushort)20); // Version needed
        w.Write((ushort)1);  // Flags: encrypted
        w.Write((ushort)0);  // Compression
        w.Write((ushort)0);  // Mod time
        w.Write((ushort)0);  // Mod date
        w.Write(crc);
        w.Write(compSize);
        w.Write(uncompSize);
        w.Write((ushort)nameBytes.Length);
        w.Write((ushort)0);  // Extra len
        w.Write((ushort)0);  // Comment len
        w.Write((ushort)0);  // Disk number start
        w.Write((ushort)0);  // Internal file attr
        w.Write((uint)0);    // External file attr
        w.Write((uint)0);    // Relative offset of local header
        w.Write(nameBytes);

        long cdSize = ms.Position - cdOffset;

        // End of Central Directory
        w.Write(0x06054b50); // Signature
        w.Write((ushort)0);  // Disk number
        w.Write((ushort)0);  // Start disk
        w.Write((ushort)1);  // Total entries on disk
        w.Write((ushort)1);  // Total entries
        w.Write((uint)cdSize);
        w.Write((uint)cdOffset);
        w.Write((ushort)0);  // Comment length

        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void ExtractionPasswords_DefaultSettings_IsEmpty()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.ExtractionPasswords);
        Assert.Empty(settings.ExtractionPasswords);
    }

    [Fact]
    public void ExtractionPasswords_Sanitization_PreservesExactValuesAndRemovesDuplicates()
    {
        var input = new List<string> { " passwordAlpha ", "", "  ", "custom_pwd", "passwordAlpha", "another_pwd " };
        var sanitized = AppSettings.SanitizeExtractionPasswords(input);

        Assert.Equal(new[] { " passwordAlpha ", "  ", "custom_pwd", "passwordAlpha", "another_pwd " }, sanitized);
        Assert.Equal(sanitized, AppSettings.SanitizeExtractionPasswords(sanitized.Concat(sanitized)));

        // Null list falls back to empty
        var nullSanitized = AppSettings.SanitizeExtractionPasswords(null);
        Assert.Empty(nullSanitized);
    }

    [Fact]
    public void ExtractionPasswords_Persistence_SerializesAndLoadsCorrectly()
    {
        var customPath = Path.Combine(_tempDirectory, "test_settings.json");
        var service = new SettingsService(customPath);

        service.Settings.ExtractionPasswords = new List<string> { "passwordOne", "passwordTwo", "passwordThree" };
        service.SaveSettings();

        var reloadedService = new SettingsService(customPath);
        Assert.Equal(3, reloadedService.Settings.ExtractionPasswords.Count);
        Assert.Contains("passwordOne", reloadedService.Settings.ExtractionPasswords);
        Assert.Contains("passwordTwo", reloadedService.Settings.ExtractionPasswords);
        Assert.Contains("passwordThree", reloadedService.Settings.ExtractionPasswords);
    }

    [Fact]
    public void TryTestArchivePassword_WithCorrectPassword_ReturnsTrue()
    {
        var archivePath = Path.Combine(_tempDirectory, "test_correct.zip");
        File.WriteAllBytes(archivePath, CreateEncryptedZip("mySecret123", "data.txt", "TopSecretPayload"));

        bool success = ArchiveExtractionService.TryTestArchivePassword(archivePath, "mySecret123", out bool isEncrypted);
        Assert.True(success);
        Assert.True(isEncrypted);
    }

    [Fact]
    public void TryTestArchivePassword_WithWrongPassword_ReturnsFalse()
    {
        var archivePath = Path.Combine(_tempDirectory, "test_wrong.zip");
        File.WriteAllBytes(archivePath, CreateEncryptedZip("mySecret123", "data.txt", "TopSecretPayload"));

        bool success = ArchiveExtractionService.TryTestArchivePassword(archivePath, "incorrectPassword", out bool isEncrypted);
        Assert.False(success);
        Assert.True(isEncrypted);
    }

    [Fact]
    public void TryTestArchivePassword_WithNullPasswordOnEncrypted_ReturnsFalse()
    {
        var archivePath = Path.Combine(_tempDirectory, "test_null_pwd.zip");
        File.WriteAllBytes(archivePath, CreateEncryptedZip("mySecret123", "data.txt", "TopSecretPayload"));

        bool success = ArchiveExtractionService.TryTestArchivePassword(archivePath, null, out bool isEncrypted);
        Assert.False(success);
        Assert.True(isEncrypted);
    }

    [Fact]
    public void TryTestArchivePassword_WithUnencryptedZip_ReturnsTrueAndNotEncrypted()
    {
        var archivePath = Path.Combine(_tempDirectory, "test_unenc.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("plain.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("Plain unencrypted text");
        }

        bool success = ArchiveExtractionService.TryTestArchivePassword(archivePath, null, out bool isEncrypted);
        Assert.True(success);
        Assert.False(isEncrypted);
    }

    [Fact]
    public async Task ExtractArchiveAsync_WithSavedPasswordMatching_ExtractsWithoutPrompt()
    {
        var archivePath = Path.Combine(_tempDirectory, "auto_pwd.zip");
        var targetDir = Path.Combine(_tempDirectory, "Extracted_Auto");
        var secretText = "Automated extraction with known password successful!";

        File.WriteAllBytes(archivePath, CreateEncryptedZip("knownPassword", "output.txt", secretText));

        // Configure Settings with multiple candidate passwords including the right one
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string> { "wrong1", "wrong2", "knownPassword", "wrong3" };

        bool handlerInvoked = false;
        ArchiveExtractionService.PasswordPromptHandler = _ =>
        {
            handlerInvoked = true;
            return Task.FromResult<(string?, bool)>(("shouldNotBeCalled", false));
        };

        var result = await ArchiveExtractionService.Instance.ExtractArchiveAsync(archivePath, targetDir);

        Assert.True(result);
        Assert.False(handlerInvoked); // Prompt must NOT be called when saved password matches

        var extractedFile = Path.Combine(targetDir, "output.txt");
        Assert.True(File.Exists(extractedFile));
        Assert.Equal(secretText, File.ReadAllText(extractedFile));
    }

    [Fact]
    public async Task ExtractArchiveAsync_WhenSavedPasswordsFail_InvokesPromptHandlerAndRemembersPassword()
    {
        var archivePath = Path.Combine(_tempDirectory, "prompt_pwd.zip");
        var targetDir = Path.Combine(_tempDirectory, "Extracted_Prompt");
        var secretText = "Extraction via user prompt successful!";
        var userPassword = "newPromptSecret99";

        File.WriteAllBytes(archivePath, CreateEncryptedZip(userPassword, "prompt_file.txt", secretText));

        // Configure Settings without the required password
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string> { "unrelated1", "wrongPassword" };

        string? requestedArchiveName = null;
        ArchiveExtractionService.PasswordPromptHandler = archiveName =>
        {
            requestedArchiveName = archiveName;
            return Task.FromResult<(string?, bool)>((userPassword, true)); // User supplies password and checks Remember
        };

        var result = await ArchiveExtractionService.Instance.ExtractArchiveAsync(archivePath, targetDir);

        Assert.True(result);
        Assert.Equal(Path.GetFileName(archivePath), requestedArchiveName);

        var extractedFile = Path.Combine(targetDir, "prompt_file.txt");
        Assert.True(File.Exists(extractedFile));
        Assert.Equal(secretText, File.ReadAllText(extractedFile));

        // Password should now be saved in Settings because Remember was true
        Assert.Contains(userPassword, SettingsService.Instance.Settings.ExtractionPasswords);
        var statistics = SettingsService.Instance.GetArchivePasswordEntries().Single(entry => entry.Password == userPassword);
        Assert.NotNull(statistics.AddedAt);
        Assert.Equal(1, statistics.UsageCount);
        Assert.Equal(1, statistics.SuccessCount);
    }

    [Fact]
    public async Task ExtractArchiveAsync_WhenPromptCancelled_ReportsStatusAndPasswordProtectedFails()
    {
        var archivePath = Path.Combine(_tempDirectory, "cancel_pwd.zip");
        var targetDir = Path.Combine(_tempDirectory, "Extracted_Cancel");

        File.WriteAllBytes(archivePath, CreateEncryptedZip("someSecret", "never_extracted.txt", "content"));
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string> { "nonMatchingKey" };

        ArchiveExtractionService.PasswordPromptHandler = _ => Task.FromResult<(string?, bool)>((null, false)); // Cancelled

        string? lastStatus = null;
        var result = await ArchiveExtractionService.Instance.ExtractArchiveAsync(archivePath, targetDir, status => lastStatus = status);

        Assert.False(result);
        Assert.Equal(Loc.Get("Status_ExtractionPasswordProtected"), lastStatus);
        Assert.False(File.Exists(Path.Combine(targetDir, "never_extracted.txt")));
    }

    [Fact]
    public async Task ExtractArchiveAsync_WhenPromptReturnsWrongPassword_ReportsStatusAndFails()
    {
        var archivePath = Path.Combine(_tempDirectory, "wrong_prompt.zip");
        var targetDir = Path.Combine(_tempDirectory, "Extracted_WrongPrompt");

        File.WriteAllBytes(archivePath, CreateEncryptedZip("actualSecret", "never_extracted.txt", "content"));
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string> { "nonMatchingKey" };

        int promptCount = 0;
        ArchiveExtractionService.PasswordPromptHandler = _ => Task.FromResult<(string?, bool)>(
            (++promptCount == 1 ? "wrongInputPassword" : null, false));

        string? lastStatus = null;
        var result = await ArchiveExtractionService.Instance.ExtractArchiveAsync(archivePath, targetDir, status => lastStatus = status);

        Assert.False(result);
        Assert.Equal(Loc.Get("Status_ExtractionPasswordProtected"), lastStatus);
    }

    [Fact]
    public void ArchivePasswords_ExpandCollapse_TogglesProperly()
    {
        SettingsService.Instance.Settings.IsArchivePasswordsExpanded = false;
        var vm = new Reepax.ViewModels.MainViewModel();
        vm.IsArchivePasswordsExpanded = false;
        Assert.False(vm.IsArchivePasswordsExpanded);

        vm.ToggleArchivePasswordsExpandedCommand.Execute(null);
        Assert.True(vm.IsArchivePasswordsExpanded);

        vm.ToggleArchivePasswordsExpandedCommand.Execute(null);
        Assert.False(vm.IsArchivePasswordsExpanded);
    }

    [Fact]
    public void ArchivePasswords_AddCommand_AddsPasswordAndClearsInput()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        Assert.False(vm.HasNewArchivePasswordInput);

        vm.NewArchivePasswordInput = "MySecretPass_UnitTesting";
        Assert.True(vm.HasNewArchivePasswordInput);

        vm.AddArchivePasswordCommand.Execute(null);

        Assert.Contains("MySecretPass_UnitTesting", vm.ArchivePasswords);
        Assert.Equal(string.Empty, vm.NewArchivePasswordInput);
        Assert.False(vm.HasNewArchivePasswordInput);
    }

    [Fact]
    public void ArchivePasswords_Count_TracksCollectionChanges()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        SettingsService.Instance.ReplaceArchivePasswords(Array.Empty<string>());
        Assert.Equal(0, vm.ArchivePasswordsCount);

        vm.NewArchivePasswordInput = "Pass1";
        vm.AddArchivePasswordCommand.Execute(null);
        Assert.Equal(1, vm.ArchivePasswordsCount);

        vm.NewArchivePasswordInput = "Pass2";
        vm.AddArchivePasswordCommand.Execute(null);
        Assert.Equal(2, vm.ArchivePasswordsCount);

        vm.RemoveArchivePasswordCommand.Execute("Pass1");
        Assert.Equal(1, vm.ArchivePasswordsCount);
    }

    [Fact]
    public void ArchivePasswords_CopyCommand_CopiesToClipboardSafely()
    {
        var vm = new Reepax.ViewModels.MainViewModel();

        // Null/empty does not throw or crash
        var exNull = Record.Exception(() => vm.CopyArchivePasswordCommand.Execute(null));
        Assert.Null(exNull);

        var exEmpty = Record.Exception(() => vm.CopyArchivePasswordCommand.Execute(""));
        Assert.Null(exEmpty);

        // Copy valid string
        var exValid = Record.Exception(() => vm.CopyArchivePasswordCommand.Execute("test_password_123"));
        Assert.Null(exValid);
    }

    [Theory]
    [InlineData("Settings_Card_ArchivePasswords_Title")]
    [InlineData("Settings_Card_ArchivePasswords_Subtitle")]
    [InlineData("Settings_ArchivePasswords_Badge_Label")]
    [InlineData("Settings_ArchivePasswords_Add")]
    [InlineData("Settings_ArchivePasswords_Column_Password")]
    [InlineData("Settings_ArchivePasswords_Column_Added")]
    [InlineData("Settings_ArchivePasswords_Column_Used")]
    [InlineData("Settings_ArchivePasswords_Column_Success")]
    [InlineData("Settings_ArchivePasswords_UnknownDate")]
    [InlineData("Settings_ArchivePasswords_Usage_ToolTip")]
    [InlineData("Settings_ArchivePasswords_Success_ToolTip")]
    [InlineData("Settings_ArchivePasswords_Placeholder")]
    [InlineData("Settings_ArchivePasswords_Remove")]
    [InlineData("Settings_ArchivePasswords_Copy")]
    [InlineData("Settings_ArchivePasswords_Copied")]
    [InlineData("Settings_ArchivePasswords_Empty")]
    [InlineData("Settings_ArchivePasswords_Empty_Detail")]
    [InlineData("Settings_ArchivePasswords_Toggle_ToolTip")]
    [InlineData("PasswordPrompt_Retry")]
    [InlineData("PasswordPrompt_ExactInput")]
    [InlineData("Status_ExtractionUnsupportedEncryptedVolumes")]
    public void ArchivePasswords_LocalizationKeys_ExistInBothLanguages(string key)
    {
        Assert.True(Strings_de.Map.ContainsKey(key), $"Key '{key}' missing in Strings_de");
        Assert.False(string.IsNullOrWhiteSpace(Strings_de.Map[key]), $"Value for '{key}' is empty in Strings_de");

        Assert.True(Strings_en.Map.ContainsKey(key), $"Key '{key}' missing in Strings_en");
        Assert.False(string.IsNullOrWhiteSpace(Strings_en.Map[key]), $"Value for '{key}' is empty in Strings_en");
    }
}
