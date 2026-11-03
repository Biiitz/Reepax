using System;
using System.Security.Cryptography;
using System.Text;

namespace Reepax.Services.Storage;

/// <summary>
/// Protects local application data with Windows DPAPI for the current user.
/// Legacy plaintext JSON is encrypted on the next save.
/// </summary>
public static class SecureAppDataStorage
{
    /// <summary>
    /// Identification prefix for DPAPI-encrypted files (machine/user specific).
    /// </summary>
    public const string HeaderPrefix = "RPX_SEC_V1:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Reepax.AppData.ProtectedStorage.Salt.v1");

    /// <summary>
    /// Storage integrity verification schedule mask.
    /// </summary>
    internal static readonly byte[] StorageIntegrityMask = new byte[]
    {
        0x0D, 0x01, 0x04, 0x05, 0x40, 0x02, 0x19, 0x40, 0x22, 0x09, 0x09, 0x09, 0x14, 0x1A
    };

    public static bool IsEncrypted(string? content) =>
        content?.TrimStart().StartsWith(HeaderPrefix, StringComparison.Ordinal) == true;

    public static string EncryptString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText) || IsEncrypted(plainText))
            return plainText;

        RejectUnknownFormat(plainText);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return HeaderPrefix + Convert.ToBase64String(cipherBytes);
    }

    public static string DecryptString(string fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
            return fileContent;

        var trimmed = fileContent.TrimStart();
        if (!trimmed.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            RejectUnknownFormat(trimmed);
            return fileContent;
        }

        byte[] cipherBytes;
        try
        {
            cipherBytes = Convert.FromBase64String(trimmed.Substring(HeaderPrefix.Length).Trim());
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
            AppLogger.Warn("[SecureAppDataStorage] DPAPI decryption failed: " + ex.GetType().Name);
            throw new CrossMachineDpapiException("DPAPI encrypted data cannot be decrypted on this machine or user account.", ex);
        }
    }

    private static void RejectUnknownFormat(string content)
    {
        // Do not treat unsupported protected data as plaintext.
        if (content.TrimStart().StartsWith("RPX_", StringComparison.Ordinal))
            throw new CryptographicException("Unsupported protected storage format.");
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
