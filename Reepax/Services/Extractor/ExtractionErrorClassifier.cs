using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

/// <summary>
/// Describes the concrete reason why an archive could not be extracted so that the UI can show
/// a precise status instead of a generic "extraction failed".
/// </summary>
public enum ExtractionErrorKind
{
    /// <summary>Unknown / unclassified error (falls back to the generic message including the exception text).</summary>
    Generic,

    /// <summary>The archive is encrypted and the password is wrong or missing.</summary>
    WrongPassword,

    /// <summary>The target drive ran out of free space while writing the extracted files.</summary>
    DiskFull,

    /// <summary>An archive part is damaged (CRC / format / truncated data). A PAR2 repair is recommended.</summary>
    CorruptArchive,

    /// <summary>A following volume of a multi-part archive (e.g. .part02.rar) is missing.</summary>
    MissingVolume
}

/// <summary>
/// Classifies extraction exceptions and builds the matching localized status messages.
/// Also detects missing follow-up volumes of multi-part archives before extraction is attempted.
/// </summary>
public static class ExtractionErrorClassifier
{
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;

    /// <summary>
    /// Localization keys of all extraction-related error status messages. Used to recognize
    /// (in every supported language) whether a status text describes an extraction error.
    /// </summary>
    private static readonly string[] ErrorStatusKeys =
    {
        "Status_ExtractionFailed",
        "Status_ExtractionPasswordProtected",
        "Status_ExtractionInsufficientDiskSpace",
        "Status_ExtractionDiskFull",
        "Status_ExtractionCorruptArchive",
        "Status_ExtractionMissingVolume",
        "Status_ExtractionMissingVolumeUnknown"
    };

    /// <summary>
    /// Returns true if the given status text is one of the detailed extraction error messages
    /// (German or English, with or without its formatted placeholder values).
    /// </summary>
    public static bool IsExtractionErrorStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        foreach (var key in ErrorStatusKeys)
        {
            if (MatchesTemplate(Strings_de.Map, key, message) || MatchesTemplate(Strings_en.Map, key, message))
                return true;
        }

        return false;
    }

    private static bool MatchesTemplate(IReadOnlyDictionary<string, string> map, string key, string message)
    {
        if (!map.TryGetValue(key, out var template) || string.IsNullOrWhiteSpace(template))
            return false;

        var braceIndex = template.IndexOf('{');
        var prefix = (braceIndex >= 0 ? template.Substring(0, braceIndex) : template).Trim();
        return prefix.Length > 0 && message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines the most specific error kind for an exception (including its inner exceptions).
    /// </summary>
    public static ExtractionErrorKind Classify(Exception ex)
    {
        var chain = new List<Exception>();
        for (var e = ex; e != null; e = e.InnerException)
            chain.Add(e);

        // 1. Disk full while writing
        if (chain.Any(e => e is IOException io && IsDiskFull(io)))
            return ExtractionErrorKind.DiskFull;

        // 2. Wrong / missing password
        foreach (var e in chain)
        {
            var typeName = e.GetType().Name;
            var msg = e.Message ?? string.Empty;
            if (e is System.Security.Cryptography.CryptographicException ||
                typeName == "CryptographicException" ||
                msg.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractionErrorKind.WrongPassword;
            }
        }

        // 3. Missing follow-up volume
        foreach (var e in chain)
        {
            var typeName = e.GetType().Name;
            var msg = e.Message ?? string.Empty;
            if (typeName is "MultiVolumeExtractionException" or "IncompleteArchiveException" or "MultipartStreamRequiredException" ||
                msg.Contains("volume", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("next part", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("missing part", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractionErrorKind.MissingVolume;
            }
        }

        // 4. Damaged archive data
        foreach (var e in chain)
        {
            var typeName = e.GetType().Name;
            var msg = e.Message ?? string.Empty;
            if (e is InvalidDataException || e is EndOfStreamException ||
                typeName is "InvalidFormatException" or "ArchiveException" ||
                msg.Contains("crc", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("checksum", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("damaged", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("truncated", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("unexpected end", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("failed to locate", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("header", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("not a valid", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractionErrorKind.CorruptArchive;
            }
        }

        return ExtractionErrorKind.Generic;
    }

    private static bool IsDiskFull(IOException io)
    {
        var code = io.HResult & 0xFFFF;
        return code == ErrorDiskFull || code == ErrorHandleDiskFull;
    }

    /// <summary>
    /// Builds the localized status text for a failed extraction of <paramref name="archiveFilePath"/>.
    /// </summary>
    public static string BuildStatusMessage(ExtractionErrorKind kind, Exception ex, string archiveFilePath)
    {
        var fileName = Path.GetFileName(archiveFilePath);
        switch (kind)
        {
            case ExtractionErrorKind.WrongPassword:
                return Loc.Get("Status_ExtractionPasswordProtected");

            case ExtractionErrorKind.DiskFull:
                return Loc.Get("Status_ExtractionDiskFull");

            case ExtractionErrorKind.CorruptArchive:
                return Loc.Format("Status_ExtractionCorruptArchive", fileName);

            case ExtractionErrorKind.MissingVolume:
                return BuildMissingVolumeStatus(FindMissingVolume(archiveFilePath, guessNextVolume: true));

            default:
                return Loc.Format("Status_ExtractionFailed", ex.Message);
        }
    }

    /// <summary>
    /// Builds the status text for a missing archive volume. Falls back to a generic hint if the name is unknown.
    /// </summary>
    public static string BuildMissingVolumeStatus(string? missingVolumeFileName)
    {
        return string.IsNullOrWhiteSpace(missingVolumeFileName)
            ? Loc.Get("Status_ExtractionMissingVolumeUnknown")
            : Loc.Format("Status_ExtractionMissingVolume", missingVolumeFileName);
    }

    /// <summary>
    /// Looks next to the primary archive part for a gap in the multi-part sequence
    /// (<c>.part01.rar</c>, <c>.part02.rar</c>, ... / <c>.rar</c> + <c>.r00</c>, <c>.r01</c>, ... / <c>.7z.001</c>, <c>.7z.002</c>, ...).
    /// Returns the file name of the first missing volume, or null if the sequence is complete.
    /// </summary>
    /// <param name="primaryArchivePath">Path of the first part of the archive.</param>
    /// <param name="guessNextVolume">
    /// If true and no gap exists, the name of the volume directly after the last existing one is returned
    /// (used when the archive reader reports that a following volume is missing).
    /// </param>
    public static string? FindMissingVolume(string primaryArchivePath, bool guessNextVolume = false)
    {
        try
        {
            var dir = Path.GetDirectoryName(primaryArchivePath);
            var name = Path.GetFileName(primaryArchivePath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || !Directory.Exists(dir))
                return null;

            // name.part01.rar, name.part02.rar, ...
            var partMatch = Regex.Match(name, @"^(?<base>.+)\.part(?<n>\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (partMatch.Success)
            {
                return FindGap(dir, partMatch.Groups["base"].Value + ".part", ".rar",
                    partMatch.Groups["n"].Value.Length, firstNumber: 1, guessNextVolume);
            }

            // name.7z.001, name.zip.001, name.rar.001, ...
            var typedSplitMatch = Regex.Match(name, @"^(?<base>.+\.(?:7z|zip|rar|tar|gz))\.(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (typedSplitMatch.Success)
            {
                return FindGap(dir, typedSplitMatch.Groups["base"].Value + ".", string.Empty,
                    typedSplitMatch.Groups["n"].Value.Length, firstNumber: 1, guessNextVolume);
            }

            // Legacy RAR: name.rar + name.r00, name.r01, ...
            if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = name.Substring(0, name.Length - 4);
                var legacyWidth = DetectLegacyRarWidth(dir, baseName);
                if (legacyWidth > 0)
                    return FindGap(dir, baseName + ".r", string.Empty, legacyWidth, firstNumber: 0, guessNextVolume);

                return null;
            }

            // name.001, name.002, ...
            var splitMatch = Regex.Match(name, @"^(?<base>.+)\.(?<n>\d+)$", RegexOptions.CultureInvariant);
            if (splitMatch.Success)
            {
                return FindGap(dir, splitMatch.Groups["base"].Value + ".", string.Empty,
                    splitMatch.Groups["n"].Value.Length, firstNumber: 1, guessNextVolume);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[ExtractionErrorClassifier] Volume check failed for '{primaryArchivePath}': {ex.Message}");
        }

        return null;
    }

    private static int DetectLegacyRarWidth(string dir, string baseName)
    {
        var regex = new Regex("^" + Regex.Escape(baseName) + @"\.r(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var m = regex.Match(Path.GetFileName(file));
            if (m.Success)
                return m.Groups["n"].Value.Length;
        }

        return 0;
    }

    private static string? FindGap(string dir, string prefix, string suffix, int width, int firstNumber, bool guessNextVolume)
    {
        var regex = new Regex("^" + Regex.Escape(prefix) + @"(?<n>\d+)" + Regex.Escape(suffix) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var existing = new HashSet<int>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var m = regex.Match(Path.GetFileName(file));
            if (m.Success && int.TryParse(m.Groups["n"].Value, out var number))
                existing.Add(number);
        }

        if (existing.Count == 0)
            return null;

        var max = existing.Max();
        for (int i = firstNumber; i <= max; i++)
        {
            if (!existing.Contains(i))
                return prefix + i.ToString().PadLeft(width, '0') + suffix;
        }

        return guessNextVolume
            ? prefix + (max + 1).ToString().PadLeft(width, '0') + suffix
            : null;
    }
}
