using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Verification;

public class ChecksumVerificationResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CalculatedChecksum { get; set; }
    public string? Algorithm { get; set; }

    public static ChecksumVerificationResult Success(string? checksum = null, string? algorithm = null) =>
        new() { IsValid = true, CalculatedChecksum = checksum, Algorithm = algorithm };

    public static ChecksumVerificationResult Failure(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

public class ChecksumVerificationService
{
    private static readonly Lazy<ChecksumVerificationService> _instance = new(() => new ChecksumVerificationService());
    public static ChecksumVerificationService Instance => _instance.Value;

    private static readonly uint[] _crc32Table = InitializeCrc32Table();

    private static uint[] InitializeCrc32Table()
    {
        const uint polynomial = 0xEDB88320;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint temp = i;
            for (int j = 0; j < 8; j++)
            {
                if ((temp & 1) == 1)
                    temp = (temp >> 1) ^ polynomial;
                else
                    temp >>= 1;
            }
            table[i] = temp;
        }
        return table;
    }

    public async Task<ChecksumVerificationResult> VerifyFileIntegrityAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        if (item == null)
            return ChecksumVerificationResult.Failure(Loc.Get("Validation_NoItemProvided"));

        var filePath = item.SaveFilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return ChecksumVerificationResult.Failure(Loc.Format("Validation_FileDoesNotExist", filePath ?? string.Empty));

        return await Task.Run(() =>
        {
            try
            {
                var fileInfo = new FileInfo(filePath);

                // 1. Check non-empty file
                if (fileInfo.Length == 0)
                {
                    return ChecksumVerificationResult.Failure(Loc.Get("Validation_FileIsEmpty"));
                }

                // 2. Check total byte size match against server header if known
                if (item.TotalBytes > 0 && fileInfo.Length != item.TotalBytes)
                {
                    return ChecksumVerificationResult.Failure(
                        Loc.Format("Validation_FileSizeMismatch", item.TotalBytes, fileInfo.Length));
                }

                // 3. Detect HTML error pages downloaded instead of binary archive parts (e.g. Cloudflare / 404 / 403)
                if (IsHtmlErrorPage(filePath))
                {
                    return ChecksumVerificationResult.Failure(
                        Loc.Get("Validation_HtmlErrorPageDetected"));
                }

                // 4. Validate expected checksum if present
                if (!string.IsNullOrWhiteSpace(item.ExpectedChecksum))
                {
                    var normalizedExpected = NormalizeChecksum(item.ExpectedChecksum);
                    var (calculated, algorithm) = CalculateChecksumForExpected(filePath, normalizedExpected);

                    if (!string.Equals(calculated, normalizedExpected, StringComparison.OrdinalIgnoreCase))
                    {
                        return ChecksumVerificationResult.Failure(
                            Loc.Format("Validation_ChecksumMismatch", algorithm, normalizedExpected, calculated));
                    }

                    return ChecksumVerificationResult.Success(calculated, algorithm);
                }

                // 5. If no explicit checksum is available, validate archive internal header & structure integrity
                // Note: Only validate primary parts (.part1.rar, .001, .zip) since split parts (.part2.rar, .002) do not contain archive headers.
                if (ArchiveExtractionService.Instance.IsArchiveFile(filePath))
                {
                    if (ArchiveExtractionService.Instance.IsPrimaryArchivePart(filePath))
                    {
                        if (!ValidateArchiveStructure(filePath, out var structureError))
                        {
                            return ChecksumVerificationResult.Failure(
                                Loc.Format("Validation_ArchiveCheckFailed", structureError ?? string.Empty));
                        }
                    }
                }

                // 6. File is valid! Compute SHA256 for audit logging
                var sha256 = CalculateSha256(filePath);
                return ChecksumVerificationResult.Success(sha256, "SHA256");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[ChecksumVerifier] Unerwarteter Fehler bei Überprüfung von '{filePath}'", ex);
                return ChecksumVerificationResult.Failure(Loc.Format("Validation_GenericIntegrityError", ex.Message));
            }
        }, cancellationToken);
    }

    public bool IsHtmlErrorPage(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[Math.Min(2048, stream.Length)];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0) return false;

            var text = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimStart();
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            bool isLegitHtmlExt = ext is ".html" or ".htm" or ".xhtml";
            bool isLegitXmlExt = ext is ".xml" or ".svg";

            // If it's a valid HTML file intentionally downloaded, only flag if it contains error signatures
            if (isLegitHtmlExt)
            {
                return ContainsHtmlErrorSignature(text);
            }

            // If it's a valid XML/SVG file intentionally downloaded, only flag if it contains explicit error responses
            if (isLegitXmlExt)
            {
                return ContainsXmlErrorSignature(text) || ContainsHtmlErrorSignature(text);
            }

            // For binary/archive/media/executable/other files:
            // Flag if the server returned an HTML page or XML error instead of the requested binary
            bool hasHtmlTagStart = text.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                                   text.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                                   text.StartsWith("<head", StringComparison.OrdinalIgnoreCase);

            if (hasHtmlTagStart)
            {
                return true;
            }

            if (ArchiveExtractionService.Instance.IsArchiveFile(filePath) && (ContainsHtmlErrorSignature(text) || ContainsXmlErrorSignature(text)))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsHtmlErrorSignature(string text)
    {
        return text.Contains("<title>404 Not Found</title>", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<title>403 Forbidden</title>", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<title>Access Denied</title>", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<title>500 Internal Server Error</title>", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<title>502 Bad Gateway</title>", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<title>503 Service Unavailable</title>", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<title>Just a moment...</title>", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<title>Attention Required! | Cloudflare</title>", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("cf-error-details", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ddos-guard", StringComparison.OrdinalIgnoreCase) ||
               (text.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) && text.Contains("error", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsXmlErrorSignature(string text)
    {
        return (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<Error", StringComparison.OrdinalIgnoreCase)) &&
               (text.Contains("<Error><Code>", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("<Error><Message>", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("<ErrorCode>", StringComparison.OrdinalIgnoreCase));
    }

    public bool ValidateArchiveStructure(string filePath, out string? error)
    {
        error = null;
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            // ZIP archive structure test
            if (ext == ".zip")
            {
                using var zip = ZipFile.OpenRead(filePath);
                // Enumerate entry headers to ensure central directory is intact
                var count = zip.Entries.Count;
                if (count == 0 && new FileInfo(filePath).Length > 100)
                {
                    error = Loc.Get("Validation_ZipNoValidEntries");
                    return false;
                }
                return true;
            }

            // RAR, 7Z, TAR, GZ, ISO, CAB structure test via SharpCompress
            using var archive = ArchiveFactory.OpenArchive(filePath);
            if (archive == null)
            {
                error = Loc.Get("Validation_ArchiveFormatUnreadable");
                return false;
            }

            // Read entries headers to check for corrupted block structures
            try
            {
                foreach (var entry in archive.Entries)
                {
                    _ = entry.Key;
                    _ = entry.Size;
                }
            }
            catch (Exception entryEx)
            {
                // For multi-volume archives (e.g. .part01.rar), entries span across multiple parts.
                // As long as not all parts have finished downloading, enumeration is expected to be
                // incomplete ("ArchiveEntry is incomplete..."). This is NOT evidence of corruption;
                // definitive verification happens during extraction.
                AppLogger.Info($"[ChecksumVerifier] Archive entry check skipped for '{Path.GetFileName(filePath)}': {entryEx.Message}");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string NormalizeChecksum(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var clean = raw.Trim().Trim('[', ']', '(', ')', '"', '\'');
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?i)^(?:sha256|sha1|md5|crc32|sha-256|sha-1|urn:sha256:|0x)\s*[:=]?\s*", "");
        return clean.Trim().ToLowerInvariant();
    }

    public (string Hash, string Algorithm) CalculateChecksumForExpected(string filePath, string expected)
    {
        var cleanExpected = NormalizeChecksum(expected);
        if (cleanExpected.Length == 64)
        {
            return (CalculateSha256(filePath), "SHA256");
        }
        if (cleanExpected.Length == 40)
        {
            return (CalculateSha1(filePath), "SHA1");
        }
        if (cleanExpected.Length == 32)
        {
            return (CalculateMd5(filePath), "MD5");
        }
        if (cleanExpected.Length == 8)
        {
            return (CalculateCrc32Hex(filePath), "CRC32");
        }

        // Default to SHA256
        return (CalculateSha256(filePath), "SHA256");
    }

    public string CalculateSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public string CalculateSha1(string filePath)
    {
        using var sha1 = SHA1.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        var hashBytes = sha1.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public string CalculateMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        var hashBytes = md5.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public string CalculateCrc32Hex(string filePath)
    {
        uint crc = CalculateCrc32(filePath);
        return crc.ToString("x8");
    }

    public uint CalculateCrc32(string filePath)
    {
        uint crc = 0xFFFFFFFF;
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        var buffer = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                byte index = (byte)((crc & 0xFF) ^ buffer[i]);
                crc = (crc >> 8) ^ _crc32Table[index];
            }
        }

        return ~crc;
    }
}
