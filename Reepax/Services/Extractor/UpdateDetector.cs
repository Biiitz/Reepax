using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Reepax.Models;

namespace Reepax.Services.Extractor;

/// <summary>
/// Intelligent detection of game updates, patches, and hotfixes,
/// along with extraction of associated game title and target subfolder.
/// </summary>
public static class UpdateDetector
{
    private static readonly Regex UpdatePatternRegex = new(
        @"(?i)(?:[.\s_-](?:update[s]?|patch(?:es)?|hotfix(?:es)?|day[.\s_-]*(?:one|1)[.\s_-]*patch)(?:[.\s_-]|$)|v?\d+(?:\.\d+)+[.\s_-]+to[.\s_-]+v?\d+|^update[s]?[.\s_-]|[.\s_-]update[s]?$)",
        RegexOptions.Compiled);

    private static readonly Regex UpdateSplitRegex = new(
        @"(?i)[.\s_-]+(?:update[s]?(?:[.\s_-]+from)?|patch(?:es)?(?:[.\s_-]+from)?|hotfix(?:es)?|day[.\s_-]*(?:one|1)[.\s_-]*patch|v?\d+(?:\.\d+)+[.\s_-]+to[.\s_-]+v?\d+)(?:[.\s_-]|$)",
        RegexOptions.Compiled);

    private static readonly Regex TrailingUpdatesRegex = new(
        @"(?i)[.\s_-]*(?:updates?|patches|hotfixes)[.\s_-]*$",
        RegexOptions.Compiled);

    private static readonly Regex PrefixRegex = new(
        @"(?i)^(?:filecrypt\s*[\-:]\s*|download(?:\s+file)?[\s\-:]+|www[._\-][a-z0-9\-]+[._\-](?:com|net|org|to|cc|co)[._\-\s]+|\[[^\]]+\]\s*)",
        RegexOptions.Compiled);

    private static readonly Regex PartPatternRegex = new(
        @"(?i)(?:[.\s_-]+(?:part|cd|disk|vol|volume|pt)[.\s_-]*\d+|\.z\d{2,}|\.r\d{2,}|\.(?:00[1-9]|0[1-9]\d|[1-9]\d{2})(?!\d))",
        RegexOptions.Compiled);

    private static readonly Regex CommonArchiveExtRegex = new(
        @"(?i)\.(rar|zip|7z|tar|gz|iso|bin|exe)(?:\.html|\.htm|\.php)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Checks whether a filename, URL, or text represents a game update or patch.
    /// </summary>
    public static bool IsUpdate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var clean = WebUtility.HtmlDecode(text).Trim();

        // If input is a URL, isolate filename / path component
        if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
            clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var urlFileName = LinkExtractor.ExtractFileNameFromUrl(clean);
            if (!string.IsNullOrWhiteSpace(urlFileName) && !LinkExtractor.IsPureNumericOrHash(urlFileName) && UpdatePatternRegex.IsMatch(urlFileName))
            {
                return true;
            }
        }

        return UpdatePatternRegex.IsMatch(clean);
    }

    /// <summary>
    /// Extracts the clean game name before the update descriptor.
    /// Example: "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.rar" -> "The Blood of Dawnwalker"
    /// Example: "The Blood of Dawnwalker Updates" -> "The Blood of Dawnwalker"
    /// </summary>
    public static string ExtractGameName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var clean = WebUtility.HtmlDecode(text).Trim();

        // If input is a URL, isolate filename
        if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
            clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var urlFileName = LinkExtractor.ExtractFileNameFromUrl(clean);
            if (!string.IsNullOrWhiteSpace(urlFileName) && !LinkExtractor.IsPureNumericOrHash(urlFileName))
            {
                clean = urlFileName;
            }
        }

        // Strip file extension
        clean = Path.GetFileName(clean);
        clean = CommonArchiveExtRegex.Replace(clean, "");

        // Strip prefixes (e.g. "Filecrypt - ", "www.site.com_")
        clean = PrefixRegex.Replace(clean, "").Trim();

        // Check if update split regex matches
        var splitMatch = UpdateSplitRegex.Match(clean);
        string gamePart;
        if (splitMatch.Success && splitMatch.Index > 0)
        {
            gamePart = clean[..splitMatch.Index];
        }
        else if (splitMatch.Success && splitMatch.Index == 0 && clean.Length > splitMatch.Length)
        {
            gamePart = clean[splitMatch.Length..];
        }
        else
        {
            // Check if string ends with "- Updates" or "Updates"
            var trailingMatch = TrailingUpdatesRegex.Match(clean);
            if (trailingMatch.Success && trailingMatch.Index > 0)
            {
                gamePart = clean[..trailingMatch.Index];
            }
            else
            {
                gamePart = clean;
            }
        }

        // Replace separators (_ . + with space)
        gamePart = Regex.Replace(gamePart, @"[_\.\+]+", " ");

        // Clean up (special characters, surrounding brackets)
        gamePart = PackageGrouper.SanitizeCleanName(gamePart);

        // Trim remaining release tags or delimiters at edges
        gamePart = gamePart.Trim(' ', '-', '_', ':', '|');

        return gamePart;
    }

    /// <summary>
    /// Creates standardized package name for an update package: "[Game Title] - Updates".
    /// </summary>
    public static string GetUpdatePackageName(string? text)
    {
        var gameName = ExtractGameName(text);
        if (string.IsNullOrWhiteSpace(gameName) || PackageGrouper.IsGenericOrCrypticName(gameName))
        {
            return "Game - Updates";
        }

        if (gameName.EndsWith(" - Updates", StringComparison.OrdinalIgnoreCase))
        {
            return gameName;
        }

        if (gameName.EndsWith(" Updates", StringComparison.OrdinalIgnoreCase))
        {
            return $"{gameName[..^8].Trim()} - Updates";
        }

        return $"{gameName} - Updates";
    }

    /// <summary>
    /// Determines subfolder name for archive extraction ("Extract to...").
    /// Strips part suffixes (.part01.rar) and archive extensions.
    /// Example: "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release.part01.rar" 
    ///        -> "The_Blood_of_Dawnwalker_Update_from_v1.0.1_to_v1.0.2-Release"
    /// </summary>
    public static string GetExtractionSubfolder(string archivePathOrName)
    {
        if (string.IsNullOrWhiteSpace(archivePathOrName))
            return "Update_Files";

        var fileName = Path.GetFileName(archivePathOrName);

        // Remove .partXX.rar or .rar/.zip/.7z
        string prev;
        do
        {
            prev = fileName;
            fileName = CommonArchiveExtRegex.Replace(fileName, "");
            fileName = PartPatternRegex.Replace(fileName, "");
        } while (fileName != prev && fileName.Length > 0);

        var safeName = PackageGrouper.MakeSafeDirectoryName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || safeName == "Download_Paket")
        {
            return "Update_Files";
        }

        return safeName;
    }

    /// <summary>
    /// Checks whether a DownloadPackage is an update package.
    /// </summary>
    public static bool IsUpdatePackage(DownloadPackage? package)
    {
        if (package == null)
            return false;

        if (package.Name.EndsWith("- Updates", StringComparison.OrdinalIgnoreCase) ||
            package.Name.EndsWith("Updates", StringComparison.OrdinalIgnoreCase) ||
            IsUpdate(package.Name))
        {
            return true;
        }

        return package.Items.Any(i => IsUpdate(i.FileName) || IsUpdate(i.OriginalUrl));
    }
}
