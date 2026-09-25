using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Reepax.Models;

public class HosterInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconKey { get; set; } = "Globe";

    /// <summary>
    /// Canonical domain of the hoster (e.g. "rapidgator.net").
    /// Used as filename for the favicon in the AppData icon cache ({Domain}.png).
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    public string DomainPattern { get; set; } = string.Empty;
    public string PrimaryColor { get; set; } = "#3B82F6";

    public static readonly List<HosterInfo> KnownHosters = new()
    {
        // Tier 1 Major Hosters
        new HosterInfo { Name = "rapidgator", DisplayName = "Rapidgator", IconKey = "Rapidgator", Domain = "rapidgator.net", DomainPattern = @"rapidgator\.net|rg\.to", PrimaryColor = "#FF6600" },
        new HosterInfo { Name = "ddownload", DisplayName = "DDownload", IconKey = "DDownload", Domain = "ddownload.com", DomainPattern = @"ddownload\.com|ddl\.to", PrimaryColor = "#0284C7" },
        new HosterInfo { Name = "1fichier", DisplayName = "1Fichier", IconKey = "OneFichier", Domain = "1fichier.com", DomainPattern = @"1fichier\.com|alterupload\.com", PrimaryColor = "#F59E0B" },
        new HosterInfo { Name = "katfile", DisplayName = "Katfile", IconKey = "Katfile", Domain = "katfile.com", DomainPattern = @"katfile\.com|katfile\.cloud", PrimaryColor = "#9333EA" },
        new HosterInfo { Name = "turbobit", DisplayName = "Turbobit", IconKey = "Turbobit", Domain = "turbobit.net", DomainPattern = @"turbobit\.net|turbo\.to", PrimaryColor = "#EF4444" },
        new HosterInfo { Name = "mega", DisplayName = "Mega", IconKey = "Mega", Domain = "mega.nz", DomainPattern = @"mega\.nz|mega\.co\.nz", PrimaryColor = "#DC2626" },
        new HosterInfo { Name = "mediafire", DisplayName = "MediaFire", IconKey = "Mediafire", Domain = "mediafire.com", DomainPattern = @"mediafire\.com", PrimaryColor = "#0070F3" },
        new HosterInfo { Name = "fasthost", DisplayName = "FastHost", IconKey = "FastHost", Domain = Reepax.Services.Extractor.FastHostResolver.CanonicalDomain, DomainPattern = Reepax.Services.Extractor.FastHostResolver.DomainRegexPattern, PrimaryColor = "#E11D48" },
        new HosterInfo { Name = "gofile", DisplayName = "Gofile", IconKey = "Gofile", Domain = "gofile.io", DomainPattern = @"gofile\.io", PrimaryColor = "#1E88E5" },
        new HosterInfo { Name = "pixeldrain", DisplayName = "Pixeldrain", IconKey = "Pixeldrain", Domain = "pixeldrain.com", DomainPattern = @"pixeldrain\.com", PrimaryColor = "#E91E63" },
        new HosterInfo { Name = "krakenfiles", DisplayName = "Krakenfiles", IconKey = "Krakenfiles", Domain = "krakenfiles.com", DomainPattern = @"krakenfiles\.com", PrimaryColor = "#00BCD4" },
        new HosterInfo { Name = "buzzheavier", DisplayName = "Buzzheavier", IconKey = "Buzzheavier", Domain = "buzzheavier.com", DomainPattern = @"buzzheavier\.com", PrimaryColor = "#F59E0B" },
        new HosterInfo { Name = "workupload", DisplayName = "Workupload", IconKey = "Workupload", Domain = "workupload.com", DomainPattern = @"workupload\.com", PrimaryColor = "#10B981" },
        new HosterInfo { Name = "fikper", DisplayName = "Fikper", IconKey = "Fikper", Domain = "fikper.com", DomainPattern = @"fikper\.com", PrimaryColor = "#059669" },
        new HosterInfo { Name = "nitroflare", DisplayName = "Nitroflare", IconKey = "Nitroflare", Domain = "nitroflare.com", DomainPattern = @"nitroflare\.com|nitro\.download", PrimaryColor = "#B91C1C" },
        new HosterInfo { Name = "gdrive", DisplayName = "Google Drive", IconKey = "GoogleDrive", Domain = "drive.google.com", DomainPattern = @"drive\.google\.com", PrimaryColor = "#16A34A" },
        new HosterInfo { Name = "sendcm", DisplayName = "Send.cm", IconKey = "Sendcm", Domain = "send.cm", DomainPattern = @"send\.cm", PrimaryColor = "#6366F1" },
        new HosterInfo { Name = "uploadhaven", DisplayName = "Uploadhaven", IconKey = "Uploadhaven", Domain = "uploadhaven.com", DomainPattern = @"uploadhaven\.com", PrimaryColor = "#0EA5E9" },
        new HosterInfo { Name = "hexupload", DisplayName = "Hexupload", IconKey = "Hexupload", Domain = "hexupload.net", DomainPattern = @"hexupload\.net", PrimaryColor = "#8B5CF6" },
        new HosterInfo { Name = "fastdrop", DisplayName = "Fastdrop", IconKey = "Fastdrop", Domain = "fastdrop.co", DomainPattern = @"fastdrop\.co", PrimaryColor = "#F43F5E" },
        new HosterInfo { Name = "terabox", DisplayName = "Terabox", IconKey = "Terabox", Domain = "terabox.com", DomainPattern = @"terabox\.com", PrimaryColor = "#00A3FF" },
        new HosterInfo { Name = "uploaded", DisplayName = "Uploaded", IconKey = "Uploaded", Domain = "uploaded.net", DomainPattern = @"uploaded\.net|uploaded\.to|ul\.to", PrimaryColor = "#0284C7" },
        new HosterInfo { Name = "keep2share", DisplayName = "Keep2Share", IconKey = "Keep2Share", Domain = "keep2share.cc", DomainPattern = @"keep2share\.cc|k2s\.cc", PrimaryColor = "#7C3AED" },
        new HosterInfo { Name = "filefactory", DisplayName = "FileFactory", IconKey = "FileFactory", Domain = "filefactory.com", DomainPattern = @"filefactory\.com", PrimaryColor = "#2563EB" },
        new HosterInfo { Name = "streamtape", DisplayName = "Streamtape", IconKey = "Streamtape", Domain = "streamtape.com", DomainPattern = @"streamtape\.com|strtape\.tech", PrimaryColor = "#2563EB" },
        new HosterInfo { Name = "voe", DisplayName = "VOE", IconKey = "VOE", Domain = "voe.sx", DomainPattern = @"voe\.sx|voe-unblock\.com", PrimaryColor = "#F97316" },
        new HosterInfo { Name = "doodstream", DisplayName = "Doodstream", IconKey = "Doodstream", Domain = "doodstream.com", DomainPattern = @"doodstream\.com|dood\.to|dood\.watch", PrimaryColor = "#06B6D4" },
        new HosterInfo { Name = "mixdrop", DisplayName = "Mixdrop", IconKey = "Mixdrop", Domain = "mixdrop.co", DomainPattern = @"mixdrop\.co|mixdrop\.to", PrimaryColor = "#A855F7" },
        new HosterInfo { Name = "dailyuploads", DisplayName = "Dailyuploads", IconKey = "Dailyuploads", Domain = "dailyuploads.net", DomainPattern = @"dailyuploads\.net", PrimaryColor = "#3B82F6" },
        new HosterInfo { Name = "userscloud", DisplayName = "Userscloud", IconKey = "Userscloud", Domain = "userscloud.com", DomainPattern = @"userscloud\.com", PrimaryColor = "#00ACC1" },
        new HosterInfo { Name = "dropapk", DisplayName = "Dropapk", IconKey = "Dropapk", Domain = "dropapk.to", DomainPattern = @"dropapk\.to", PrimaryColor = "#84CC16" },
        new HosterInfo { Name = "uptobox", DisplayName = "UpToBox", IconKey = "UpToBox", Domain = "uptobox.com", DomainPattern = @"uptobox\.com|uptostream\.com", PrimaryColor = "#FF6F00" }
    };

    public static bool IsKnownHoster(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        foreach (var hoster in KnownHosters)
        {
            if (Regex.IsMatch(url, hoster.DomainPattern, RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    public static HosterInfo DetectHoster(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new HosterInfo { Name = "generic", DisplayName = "Web", IconKey = "Globe", Domain = "generic", PrimaryColor = "#718096" };

        foreach (var hoster in KnownHosters)
        {
            if (Regex.IsMatch(url, hoster.DomainPattern, RegexOptions.IgnoreCase))
            {
                return hoster;
            }
        }

        try
        {
            var cleaned = Regex.Replace(url.Trim(), @"^(https?://)?[.\s]+", "$1");
            if (!cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = "http://" + cleaned;
            }

            string host = string.Empty;
            if (Uri.TryCreate(cleaned, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
            }

            // Also clean any internal leading dots if host was parsed directly
            host = host.Trim().Trim('.').ToLowerInvariant();
            if (host.StartsWith("www."))
            {
                host = host.Substring(4).TrimStart('.');
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                return new HosterInfo { Name = "unknown", DisplayName = "Link", IconKey = "Globe", Domain = "unknown", PrimaryColor = "#718096" };
            }

            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            string name = parts.Length > 0 ? parts[0] : host;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "link";
            }

            string displayName = name.Length > 1
                ? char.ToUpperInvariant(name[0]) + name.Substring(1)
                : name.ToUpperInvariant();

            return new HosterInfo
            {
                Name = host,
                DisplayName = displayName,
                IconKey = "Globe",
                Domain = host,
                PrimaryColor = "#3B82F6"
            };
        }
        catch
        {
            return new HosterInfo { Name = "unknown", DisplayName = "Link", IconKey = "Globe", Domain = "unknown", PrimaryColor = "#718096" };
        }
    }
}
