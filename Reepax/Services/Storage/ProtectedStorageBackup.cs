using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Reepax.Services.Storage;

internal static class ProtectedStorageBackup
{
    internal static void Update<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        string backup = path + ".bak";
        string? content = ReadValidContent<T>(path, options);
        if (content == null)
        {
            content = ReadValidContent<T>(backup, options);
            // A recovered protected backup must survive a failed primary write unchanged.
            if (content == null || SecureAppDataStorage.IsEncrypted(content)) return;
        }

        string encrypted = SecureAppDataStorage.EncryptString(content);
        string temporary = backup + ".tmp";
        try
        {
            File.WriteAllText(temporary, encrypted);
            File.Move(temporary, backup, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string? ReadValidContent<T>(string path, JsonSerializerOptions? options) where T : class
    {
        if (!File.Exists(path)) return null;
        string? content = DownloadPersistenceService.ReadFileWithRetry(path);
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<T>(SecureAppDataStorage.DecryptString(content), options);
            return data != null ? content : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }
}
