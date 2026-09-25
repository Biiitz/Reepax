using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Reepax.Models;
using Reepax.Services.Localization;

namespace Reepax.Services.Extractor;

public static class PackageGrouper
{
    private static readonly Regex PartPatternRegex = new(
        @"(?i)(?:[\._\-\s]+(?:part|cd|disk|vol|volume|pt)[\._\-\s]*\d+|\.z\d{2,}|\.r\d{2,}|\.(?:00[1-9]|0[1-9]\d|[1-9]\d{2})(?!\d))",
        RegexOptions.Compiled);

    private static readonly Regex HashPatternRegex = new(
        @"(?i)(?:[_\-\.][0-9a-f]{8,32}|\[[0-9a-f]{8}\])$",
        RegexOptions.Compiled);

    private static readonly Regex CommonExtensionsRegex = new(
        @"(?i)\.(rar|zip|7z|tar|gz|iso|mkv|mp4|avi|bin|exe|pkg|mov|flv|wmv|mp3|flac|wav|pdf|epub)(?:\.html|\.htm|\.php)?$",
        RegexOptions.Compiled);

    private static readonly Regex SpecialCharsRegex = new(
        @"[_\.\+]+",
        RegexOptions.Compiled);

    private static readonly Regex DomainPrefixRegex = new(
        @"(?i)^www[\._\-][a-z0-9\-]+[\._\-](?:com|net|org|to|cc|co)[\._\-]",
        RegexOptions.Compiled);

    private static readonly Regex GenericPackageRegex = new(
        @"(?i)(?:package|paket|download)\s*\(\d+\s*(?:files|dateien)\)|^(?:download(?:_|\s+)?(?:package|paket)|package|paket|download|file)\b|^filecrypt\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Groups extracted links into packages. By default, links from the same drop/paste action 
    /// are grouped into a SINGLE cohesive package unless explicitly separated by distinct section headers.
    /// </summary>
    public static List<DownloadPackage> GroupLinksIntoPackages(
        IEnumerable<ExtractedLink> extractedLinks, 
        string baseDownloadFolder, 
        string? customPackageName = null,
        bool autoExtractArchives = false,
        bool? lowResourceExtraction = null,
        bool deleteArchiveAfterExtraction = false,
        bool moveArchiveToRecycleBin = false,
        bool autoResolveHostLinks = false)
    {
        var packages = new List<DownloadPackage>();
        var linksList = extractedLinks.ToList();

        if (linksList.Count == 0)
            return packages;

        // If user specified a custom package name (from dialog), put all into 1 package
        if (!string.IsNullOrWhiteSpace(customPackageName))
        {
            packages.Add(CreatePackage(customPackageName, linksList, baseDownloadFolder, autoExtractArchives, lowResourceExtraction, deleteArchiveAfterExtraction, moveArchiveToRecycleBin, autoResolveHostLinks));
            return packages;
        }

        // Check if links have multiple distinct, non-empty ContextTitles
        var distinctContextTitles = linksList
            .Select(l => l.ContextTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctContextTitles.Count > 1)
        {
            // Group by ContextTitle
            var contextGroups = linksList.GroupBy(l => l.ContextTitle ?? "Downloads", StringComparer.OrdinalIgnoreCase);
            foreach (var grp in contextGroups)
            {
                var pkgName = DetermineBestPackageName(grp.ToList());
                packages.Add(CreatePackage(pkgName, grp.ToList(), baseDownloadFolder, autoExtractArchives, lowResourceExtraction, deleteArchiveAfterExtraction, moveArchiveToRecycleBin, autoResolveHostLinks));
            }
            return packages;
        }

        // Default behavior: Put all links from this drop/paste into ONE package
        var bestName = DetermineBestPackageName(linksList);
        packages.Add(CreatePackage(bestName, linksList, baseDownloadFolder, autoExtractArchives, lowResourceExtraction, deleteArchiveAfterExtraction, moveArchiveToRecycleBin, autoResolveHostLinks));
        return packages;
    }

    public static string DetermineBestPackageName(List<ExtractedLink> links)
    {
        if (links == null || links.Count == 0)
            return Loc.Get("Package_DefaultName");

        // 0. Smart Game Update detection: Format as "[Game Name] - Updates"
        var updateSource = links
            .Select(l => l.ContextTitle)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && UpdateDetector.IsUpdate(t));

        if (string.IsNullOrWhiteSpace(updateSource))
        {
            updateSource = links
                .Select(l => l.RawFileName)
                .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && UpdateDetector.IsUpdate(f));
        }

        if (string.IsNullOrWhiteSpace(updateSource))
        {
            updateSource = links
                .Select(l => l.Url)
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u) && UpdateDetector.IsUpdate(u));
        }

        if (!string.IsNullOrWhiteSpace(updateSource))
        {
            var updatePkgName = UpdateDetector.GetUpdatePackageName(updateSource);
            if (!string.IsNullOrWhiteSpace(updatePkgName) && updatePkgName != "Game - Updates")
            {
                return updatePkgName;
            }
        }

        // 1. Check ContextTitle if available
        var contextTitle = links.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.ContextTitle))?.ContextTitle;
        if (!string.IsNullOrWhiteSpace(contextTitle) && !LinkExtractor.IsPureNumericOrHash(contextTitle))
        {
            var cleanContext = SanitizeCleanName(contextTitle);
            if (!string.IsNullOrWhiteSpace(cleanContext) && cleanContext.Length >= 2)
            {
                return cleanContext;
            }
        }

        // 2. Check if any link has a clean, meaningful filename
        var cleanCandidates = links
            .Select(l => CleanPackageBaseName(l.RawFileName, l.Url, l.Hoster, null))
            .Where(name => !string.IsNullOrWhiteSpace(name) && !IsGenericOrCrypticName(name))
            .ToList();

        if (cleanCandidates.Count > 0)
        {
            // Find most frequent or longest common candidate
            var mostCommon = cleanCandidates
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .First().Key;

            return mostCommon;
        }

        // 3. Fallback: Clean hoster-based package name (NEVER random numbers or hashes!)
        var primaryHoster = links.First().Hoster.DisplayName;
        if (links.Count > 1)
        {
            return Loc.Format("Package_HosterMultiFilesName", primaryHoster, links.Count);
        }

        return Loc.Format("Package_HosterSingleDownloadName", primaryHoster, DateTime.Now.ToString("yyyy-MM-dd HHmm"));
    }

    public static DownloadPackage CreatePackage(
        string packageName, 
        List<ExtractedLink> links, 
        string baseDownloadFolder,
        bool autoExtractArchives = false,
        bool? lowResourceExtraction = null,
        bool deleteArchiveAfterExtraction = false,
        bool moveArchiveToRecycleBin = false,
        bool autoResolveHostLinks = false)
    {
        // Updates require host auto-resolver to be disabled and proper naming
        bool isUpdate = UpdateDetector.IsUpdate(packageName) || 
                        packageName.EndsWith("- Updates", StringComparison.OrdinalIgnoreCase) ||
                        links.Any(l => UpdateDetector.IsUpdate(l.RawFileName) || UpdateDetector.IsUpdate(l.ContextTitle) || UpdateDetector.IsUpdate(l.Url));

        if (isUpdate)
        {
            autoResolveHostLinks = false;
            if (!packageName.EndsWith("- Updates", StringComparison.OrdinalIgnoreCase))
            {
                var formatted = UpdateDetector.GetUpdatePackageName(packageName);
                if (!string.IsNullOrWhiteSpace(formatted) && formatted != "Game - Updates")
                {
                    packageName = formatted;
                }
            }
        }

        var safePackageName = MakeSafeDirectoryName(packageName);
        if (string.IsNullOrWhiteSpace(safePackageName) || 
            LinkExtractor.IsPureNumericOrHash(safePackageName))
        {
            var primaryHoster = links.FirstOrDefault()?.Hoster.DisplayName ?? "Downloads";
            if (links.Count > 1)
            {
                safePackageName = Loc.Format("Package_HosterMultiFilesName", primaryHoster, links.Count);
            }
            else
            {
                safePackageName = Loc.Format("Package_HosterSingleDownloadName", primaryHoster, DateTime.Now.ToString("yyyy-MM-dd HHmm"));
            }
        }

        var saveDir = Path.Combine(baseDownloadFolder, safePackageName);

        var package = new DownloadPackage
        {
            Name = safePackageName,
            SaveDirectory = saveDir,
            AutoExtractArchives = autoExtractArchives,
            LowResourceExtraction = lowResourceExtraction ?? false,
            DeleteArchiveAfterExtraction = deleteArchiveAfterExtraction,
            MoveArchiveToRecycleBin = moveArchiveToRecycleBin,
            AutoResolveHostLinks = autoResolveHostLinks,
            PackageIconKey = links.Any(l => IsArchiveName(l.RawFileName)) ? "Archive" : "Folder",
            Status = DownloadStatus.Queued
        };

        for (int i = 0; i < links.Count; i++)
        {
            var link = links[i];
            var cleanFileName = MakeSafeFileName(link.RawFileName);

            // If filename was pure cryptic numbers, hash or placeholder, generate a clean filename
            if (string.IsNullOrWhiteSpace(cleanFileName) || 
                cleanFileName == "download_file" || 
                LinkExtractor.IsPureNumericOrHash(cleanFileName))
            {
                if (links.Count > 1)
                {
                    cleanFileName = $"{safePackageName}.part{i + 1:D2}.rar";
                }
                else
                {
                    cleanFileName = $"{safePackageName}.rar";
                }
            }

            var item = new DownloadItem
            {
                // OriginalUrl always remains the original page URL, so expired
                // direct links can be re-resolved later
                OriginalUrl = link.Url,
                DirectDownloadUrl = link.DirectDownloadUrl,
                FileName = cleanFileName,
                HosterName = link.Hoster.DisplayName,
                HosterIconKey = link.Hoster.IconKey,
                SaveFilePath = Path.Combine(saveDir, cleanFileName),
                Status = DownloadStatus.Paused,
                StatusMessage = Loc.Get("Status_Paused")
            };

            package.Items.Add(item);
        }

        package.RecalculateAggregates();
        return package;
    }

    public static string CleanPackageBaseName(string rawFileName, string url, HosterInfo hoster, string? contextTitle = null)
    {
        if (string.IsNullOrWhiteSpace(rawFileName) || rawFileName.Equals("download_file", StringComparison.OrdinalIgnoreCase))
        {
            rawFileName = LinkExtractor.ExtractFileNameFromUrl(url);
        }

        // If rawFileName is cryptic/numeric/download_file, use contextTitle if available
        if (string.IsNullOrWhiteSpace(rawFileName) || 
            rawFileName.Equals("download_file", StringComparison.OrdinalIgnoreCase) ||
            LinkExtractor.IsPureNumericOrHash(rawFileName))
        {
            if (!string.IsNullOrWhiteSpace(contextTitle) && !LinkExtractor.IsPureNumericOrHash(contextTitle))
            {
                var cleanContext = SanitizeCleanName(contextTitle);
                if (!string.IsNullOrWhiteSpace(cleanContext) && cleanContext.Length >= 2)
                {
                    return cleanContext;
                }
            }

            return Loc.Format("Package_HosterSingleDownloadName", hoster.DisplayName, DateTime.Now.ToString("yyyy-MM-dd HHmm"));
        }

        string cleaned = rawFileName;

        // Strip domain prefixes e.g. www_mysite_com_
        cleaned = DomainPrefixRegex.Replace(cleaned, "");

        // Iteratively remove parts, extensions, and trailing hashes (e.g. .7z.001 -> .7z -> base)
        string prev;
        do
        {
            prev = cleaned;
            cleaned = CommonExtensionsRegex.Replace(cleaned, "");
            cleaned = PartPatternRegex.Replace(cleaned, "");
            cleaned = HashPatternRegex.Replace(cleaned, "");
        } while (cleaned != prev && cleaned.Length > 0);

        // Clean up separators: replace dots/underscores/plus with spaces
        cleaned = SpecialCharsRegex.Replace(cleaned, " ");

        // Trim leftover whitespace and symbols
        cleaned = SanitizeCleanName(cleaned);

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 2 || IsGenericOrCrypticName(cleaned))
        {
            return Loc.Format("Package_HosterSingleDownloadName", hoster.DisplayName, DateTime.Now.ToString("yyyy-MM-dd HHmm"));
        }

        return cleaned;
    }

    public static bool IsGenericOrCrypticName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        if (LinkExtractor.IsPureNumericOrHash(name))
            return true;

        var lower = name.ToLowerInvariant().Trim();
        if (lower.StartsWith("package_") || 
            lower.StartsWith("download_") || 
            lower.StartsWith("download paket") ||
            lower.StartsWith("download package") ||
            lower.StartsWith("download_paket") ||
            lower.StartsWith("download_package") ||
            lower.StartsWith("filecrypt") ||
            lower.Contains(" package (") ||
            lower.Contains(" paket (") ||
            lower.Contains(" download (") ||
            lower.Contains(" - single download") ||
            lower.Contains(" - einzeldownload") ||
            lower.Contains(" files)") ||
            lower.Contains(" dateien)") ||
            lower == "download" || 
            lower == "file" || 
            lower == "package" ||
            lower == "paket" ||
            lower == "download_file")
        {
            return true;
        }

        if (GenericPackageRegex.IsMatch(name))
        {
            return true;
        }

        // Hoster fallback pattern: e.g. "FastHost - Paket (5 Dateien)" or "Rapidgator - Download 2026-09-11"
        if (Regex.IsMatch(lower, @"^[a-z0-9\.\-_]+\s*-\s*(?:paket|package|download|einzeldownload)\b"))
        {
            return true;
        }

        return false;
    }

    public static string SanitizeCleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        name = name.Trim(' ', '-', '_', '.', ',', ':', ';', '|', '/', '\\');

        // Clean unclosed/orphan brackets at edges
        if (name.EndsWith(")") && !name.Contains("(")) name = name[..^1].Trim();
        if (name.EndsWith("]") && !name.Contains("[")) name = name[..^1].Trim();
        if (name.StartsWith("(") && !name.Contains(")")) name = name[1..].Trim();
        if (name.StartsWith("[") && !name.Contains("]")) name = name[1..].Trim();

        // Capitalize first letter of words if all lowercase
        if (name.All(c => !char.IsLetter(c) || char.IsLower(c)))
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            name = string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w.Substring(1) : "")));
        }

        return name.Trim();
    }

    private static bool IsArchiveName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        return Regex.IsMatch(fileName, @"(?i)\.(rar|zip|7z|tar|gz|z\d{2,}|\d{3,}|r\d{2,})$");
    }

    private static readonly HashSet<string> ReservedDosNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string MakeSafeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Download_Paket";

        var invalid = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct().ToArray();
        var safe = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');

        while (safe.Contains(".."))
        {
            safe = safe.Replace("..", "_");
        }
        safe = safe.Trim('.', '_', ' ');

        if (string.IsNullOrWhiteSpace(safe) || safe.All(c => c == '.'))
            return "Download_Paket";

        var baseName = safe.Split('.')[0];
        if (ReservedDosNames.Contains(safe) || ReservedDosNames.Contains(baseName))
            return "Download_Paket";

        return safe;
    }

    public static string MakeSafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "file.bin";

        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');

        while (safe.Contains(".."))
        {
            safe = safe.Replace("..", "_");
        }
        safe = safe.Trim('.', '_', ' ');

        if (string.IsNullOrWhiteSpace(safe) || safe.All(c => c == '.'))
            return "file.bin";

        var dotIndex = safe.IndexOf('.');
        var baseName = dotIndex >= 0 ? safe[..dotIndex] : safe;

        if (ReservedDosNames.Contains(safe) || ReservedDosNames.Contains(baseName))
        {
            return $"_{safe}";
        }

        return safe;
    }
}
