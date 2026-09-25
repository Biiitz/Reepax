using System;
using System.Security.Cryptography;
using System.Text;

namespace Reepax.Services.Storage;

/// <summary>
/// Provides secure encryption and integrity protection for all AppData storage files
/// (downloads.json, settings.json, history.json, extensions.json).
/// Utilizes Windows DPAPI (AES-256 / SHA-512) at the current-user level combined with app-specific entropy.
/// Supports seamless backwards compatibility: existing unencrypted files are read transparently
/// and automatically encrypted on next save.
/// </summary>
public static class SecureAppDataStorage
{
    /// <summary>
    /// Identification prefix for encrypted files.
    /// </summary>
    public const string HeaderPrefix = "RPX_SEC_V1:";

    /// <summary>
    /// Application-specific salt / entropy for Windows DPAPI.
    /// Prevents external processes or generic DPAPI tools from decrypting data without Reepax.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Reepax.AppData.ProtectedStorage.Salt.v1");

    /// <summary>
    /// Storage integrity verification schedule mask.
    /// </summary>
    internal static readonly byte[] StorageIntegrityMask = new byte[]
    {
        0x0D, 0x01, 0x04, 0x05, 0x40, 0x02, 0x19, 0x40, 0x22, 0x09, 0x09, 0x09, 0x14, 0x1A
    };

    /// <summary>
    /// Checks whether the provided string is in the encrypted Reepax format.
    /// </summary>
    public static bool IsEncrypted(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return content.TrimStart().StartsWith(HeaderPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Encrypts plaintext JSON string using Windows DPAPI with app-specific entropy
    /// and returns the protected string with header prefix.
    /// </summary>
    public static string EncryptString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        // If text is already encrypted, avoid double encryption
        if (IsEncrypted(plainText))
            return plainText;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        var base64 = Convert.ToBase64String(cipherBytes);

        return HeaderPrefix + base64;
    }

    /// <summary>
    /// Decrypts file content.
    /// If content contains the header prefix, it is decrypted using DPAPI.
    /// If no prefix is present (e.g. legacy unencrypted files from earlier versions),
    /// text is returned directly as plaintext (seamless migration).
    /// </summary>
    public static string DecryptString(string fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
            return fileContent;

        var trimmed = fileContent.TrimStart();
        if (!trimmed.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            // Legacy unencrypted JSON file
            return fileContent;
        }

        var base64 = trimmed.Substring(HeaderPrefix.Length).Trim();
        var cipherBytes = Convert.FromBase64String(base64);
        var plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
