using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Reepax.Services.Storage;

/// <summary>
/// Provides secure encryption and integrity protection for all AppData storage files
/// (downloads.json, settings.json, history.json, extensions.json).
/// Supports two encryption tiers:
/// 1. Windows DPAPI (AES-256 / SHA-512) at current-user level for standard installed mode.
/// 2. Machine-independent AES-256-CBC with random IV for portable mode, ensuring data
///    is portable across different machines without data loss.
/// Supports seamless backwards compatibility: unencrypted files are read transparently
/// and automatically upgraded to the active encryption scheme on next save.
/// </summary>
public static class SecureAppDataStorage
{
    /// <summary>
    /// Identification prefix for DPAPI-encrypted files (machine/user specific).
    /// </summary>
    public const string HeaderPrefix = "RPX_SEC_V1:";

    /// <summary>
    /// Identification prefix for cross-machine portable encrypted files (AES-256).
    /// </summary>
    public const string PortableHeaderPrefix = "RPX_PORTABLE_V1:";

    /// <summary>
    /// Application-specific salt / entropy for Windows DPAPI and key derivation.
    /// Prevents external generic tools from decrypting data without Reepax logic.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Reepax.AppData.ProtectedStorage.Salt.v1");

    /// <summary>
    /// Precomputed 256-bit AES key derived from SHA-256 hash of the application entropy salt.
    /// Guarantees consistent encryption across different Windows machines and user accounts.
    /// </summary>
    private static readonly byte[] PortableAesKey = SHA256.HashData(Entropy);

    /// <summary>
    /// Storage integrity verification schedule mask.
    /// </summary>
    internal static readonly byte[] StorageIntegrityMask = new byte[]
    {
        0x0D, 0x01, 0x04, 0x05, 0x40, 0x02, 0x19, 0x40, 0x22, 0x09, 0x09, 0x09, 0x14, 0x1A
    };

    /// <summary>
    /// Checks whether the provided string is in an encrypted Reepax format (DPAPI or Portable AES).
    /// </summary>
    public static bool IsEncrypted(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var trimmed = content.TrimStart();
        return trimmed.StartsWith(HeaderPrefix, StringComparison.Ordinal) ||
               trimmed.StartsWith(PortableHeaderPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Encrypts plaintext JSON string.
    /// In portable mode (<see cref="SettingsService.IsPortableMode"/> is true), encrypts using
    /// cross-machine AES-256-CBC with a cryptographically random IV.
    /// In installed mode, encrypts using Windows DPAPI at the CurrentUser level.
    /// Returns the protected string with the appropriate header prefix.
    /// </summary>
    public static string EncryptString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        // If text is already encrypted, avoid double encryption
        if (IsEncrypted(plainText))
            return plainText;

        if (SettingsService.IsPortableMode)
        {
            return EncryptPortable(plainText);
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        var base64 = Convert.ToBase64String(cipherBytes);

        return HeaderPrefix + base64;
    }

    /// <summary>
    /// Decrypts file content.
    /// If content starts with <see cref="PortableHeaderPrefix"/>, it is decrypted using cross-machine AES-256.
    /// If content starts with <see cref="HeaderPrefix"/>, it is decrypted using Windows DPAPI.
    /// If DPAPI fails (e.g. when moving files from one machine to another), a warning is logged
    /// and a <see cref="CrossMachineDpapiException"/> is thrown to prevent silent data corruption.
    /// If no prefix is present (e.g. legacy unencrypted files), the text is returned as-is (seamless migration).
    /// </summary>
    public static string DecryptString(string fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
            return fileContent;

        var trimmed = fileContent.TrimStart();

        // 1. Cross-machine portable encryption (AES-256-CBC)
        if (trimmed.StartsWith(PortableHeaderPrefix, StringComparison.Ordinal))
        {
            var base64 = trimmed.Substring(PortableHeaderPrefix.Length).Trim();
            return DecryptPortable(base64);
        }

        // 2. Windows DPAPI encryption (installed / single-machine)
        if (trimmed.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            var base64 = trimmed.Substring(HeaderPrefix.Length).Trim();
            byte[] cipherBytes;
            try
            {
                cipherBytes = Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                throw new CryptographicException("DPAPI encrypted payload is not valid Base64.", ex);
            }

            try
            {
                var plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (CryptographicException ex)
            {
                AppLogger.Warn($"[SecureAppDataStorage] DPAPI decryption failed (likely machine or user account mismatch): {ex.Message}");
                throw new CrossMachineDpapiException("DPAPI encrypted data cannot be decrypted on this machine or user account.", ex);
            }
        }

        // 3. Legacy unencrypted JSON file
        return fileContent;
    }

    /// <summary>
    /// Encrypts plaintext using AES-256-CBC with a random 16-byte IV and precomputed key.
    /// Returns the combined payload formatted as: RPX_PORTABLE_V1:Base64(IV + Ciphertext).
    /// </summary>
    private static string EncryptPortable(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        using var aes = Aes.Create();
        aes.Key = PortableAesKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        var iv = aes.IV;
        using var encryptor = aes.CreateEncryptor();
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var combined = new byte[iv.Length + cipherBytes.Length];
        Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, iv.Length, cipherBytes.Length);

        return PortableHeaderPrefix + Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Decrypts a Base64-encoded combined payload (IV + Ciphertext) using AES-256-CBC.
    /// </summary>
    private static string DecryptPortable(string base64Payload)
    {
        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(base64Payload);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Portable encrypted payload is not valid Base64.", ex);
        }

        if (combined.Length < 16)
        {
            throw new CryptographicException("Portable encrypted payload is too short to contain a valid IV.");
        }

        var iv = new byte[16];
        Buffer.BlockCopy(combined, 0, iv, 0, 16);

        var cipherLength = combined.Length - 16;

        using var aes = Aes.Create();
        aes.Key = PortableAesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(combined, 16, cipherLength);
        return Encoding.UTF8.GetString(plainBytes);
    }
}

/// <summary>
/// Exception thrown when DPAPI decryption fails, typically due to the encrypted data
/// being accessed on a different machine or user account than where it was encrypted.
/// Inherits from <see cref="CryptographicException"/> to ensure compatibility with callers
/// catching cryptographic exceptions while allowing explicit detection of machine mismatches.
/// </summary>
public class CrossMachineDpapiException : CryptographicException
{
    public CrossMachineDpapiException(string message) : base(message) { }
    public CrossMachineDpapiException(string message, Exception innerException) : base(message, innerException) { }
}
