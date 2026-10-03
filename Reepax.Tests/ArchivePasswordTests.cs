using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using SharpCompress.Archives;
using SharpCompress.Readers;
using Xunit;

namespace Reepax.Tests;

public class ArchivePasswordTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Func<string, Task<(string? Password, bool Remember)>>? _originalPromptHandler;
    private readonly List<string> _originalSavedPasswords;

    public ArchivePasswordTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "Reepax_PwdTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _originalPromptHandler = ArchiveExtractionService.PasswordPromptHandler;
        _originalSavedPasswords = SettingsService.Instance.Settings.ExtractionPasswords != null
            ? new List<string>(SettingsService.Instance.Settings.ExtractionPasswords)
            : new List<string>();
    }

    public void Dispose()
    {
        ArchiveExtractionService.PasswordPromptHandler = _originalPromptHandler;
        SettingsService.Instance.Settings.ExtractionPasswords = new List<string>(_originalSavedPasswords);

        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch { }
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
    private static byte[] CreateEncryptedZip(string password, string entryName, string textContent)
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
    public void ExtractionPasswords_Sanitization_TrimsAndRemovesDuplicates()
    {
        var input = new List<string> { " passwordAlpha ", "", "  ", "custom_pwd", "passwordAlpha", "another_pwd " };
        var sanitized = AppSettings.SanitizeExtractionPasswords(input);

        Assert.Equal(3, sanitized.Count);
        Assert.Equal("passwordAlpha", sanitized[0]);
        Assert.Equal("custom_pwd", sanitized[1]);
        Assert.Equal("another_pwd", sanitized[2]);

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

        ArchiveExtractionService.PasswordPromptHandler = _ => Task.FromResult<(string?, bool)>(("wrongInputPassword", false));

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
        vm.ArchivePasswords.Clear();
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
    [InlineData("Settings_ArchivePasswords_Placeholder")]
    [InlineData("Settings_ArchivePasswords_Remove")]
    [InlineData("Settings_ArchivePasswords_Copy")]
    [InlineData("Settings_ArchivePasswords_Copied")]
    [InlineData("Settings_ArchivePasswords_Empty")]
    [InlineData("Settings_ArchivePasswords_Empty_Detail")]
    [InlineData("Settings_ArchivePasswords_Toggle_ToolTip")]
    public void ArchivePasswords_LocalizationKeys_ExistInBothLanguages(string key)
    {
        Assert.True(Strings_de.Map.ContainsKey(key), $"Key '{key}' missing in Strings_de");
        Assert.False(string.IsNullOrWhiteSpace(Strings_de.Map[key]), $"Value for '{key}' is empty in Strings_de");

        Assert.True(Strings_en.Map.ContainsKey(key), $"Key '{key}' missing in Strings_en");
        Assert.False(string.IsNullOrWhiteSpace(Strings_en.Map[key]), $"Value for '{key}' is empty in Strings_en");
    }
}
