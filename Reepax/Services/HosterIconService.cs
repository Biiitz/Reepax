using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reepax.Models;
using Reepax.Services.Storage;

namespace Reepax.Services;

public sealed class HosterIconService
{
    private static readonly Lazy<HosterIconService> _default = new(() => new(SettingsService.IconsDirectory));
    private static readonly ImageSource _defaultGlobe = CreateGenericGlobeIcon();
    private static readonly char[] InvalidIconKeyCharacters = Path.GetInvalidFileNameChars();
    private readonly ConcurrentDictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _appDataIconsDir;

    internal HosterIconService(string iconDirectory)
    {
        _appDataIconsDir = iconDirectory;
        LoadAllLocalFavicons();
    }

    private bool CanReadIconCache()
    {
        try
        {
            var directory = new DirectoryInfo(_appDataIconsDir);
            return directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsIconKey(string key) =>
        key.Length > 0 && key.IndexOfAny(InvalidIconKeyCharacters) < 0;

    private void LoadAllLocalFavicons()
    {
        try
        {
            if (!CanReadIconCache()) return;

            foreach (var file in Directory.GetFiles(_appDataIconsDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".png" or ".ico" or ".jpg" or ".jpeg" or ".bmp"))
                    continue;

                var rawName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(rawName))
                    continue;

                var img = LoadImageFromFile(file);
                if (img == null)
                    continue;

                _iconCache[rawName] = img;

                // Also map canonical domain and known aliases into cache
                var canonical = GetDomain(rawName);
                if (!string.IsNullOrEmpty(canonical) && canonical != "unknown")
                {
                    _iconCache[canonical] = img;
                }

                foreach (var hoster in HosterInfo.KnownHosters)
                {
                    if (string.Equals(hoster.Domain, rawName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(hoster.Domain, canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(hoster.Name))
                            _iconCache[hoster.Name] = img;
                        if (!string.IsNullOrEmpty(hoster.DisplayName))
                            _iconCache[hoster.DisplayName.ToLowerInvariant()] = img;
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Returns a locally cached icon or a generated badge for a hoster.
    /// </summary>
    public static ImageSource GetIconForHoster(string? hosterOrUrl) => _default.Value.GetIcon(hosterOrUrl);

    internal ImageSource GetIcon(string? hosterOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hosterOrUrl))
            return _defaultGlobe;

        var trimmed = hosterOrUrl.Trim();
        if (trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("generic", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("web", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("archive", StringComparison.OrdinalIgnoreCase))
        {
            return _defaultGlobe;
        }

        string domain = GetDomain(trimmed);
        if (!IsIconKey(domain) || domain.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            domain.Equals("generic", StringComparison.OrdinalIgnoreCase))
        {
            return _defaultGlobe;
        }

        if (_iconCache.Count > 300)
            _iconCache.Clear();

        // 1. In-Memory Cache
        if (_iconCache.TryGetValue(domain, out var cached))
            return cached;

        // 2. AppData disk cache ({domain}.png)
        var img = FindIconOnDisk(domain);
        if (img != null)
        {
            _iconCache[domain] = img;
            return img;
        }

        var fallback = CreateGenericLetterBadge(domain);
        _iconCache[domain] = fallback;
        return fallback;
    }

    private ImageSource? FindIconOnDisk(string domain)
    {
        if (!IsIconKey(domain) || !CanReadIconCache())
            return null;

        // .png is the standard format; others for migrating older cache files
        string[] extensions = { ".png", ".ico", ".jpg", ".jpeg", ".bmp" };

        // 1. Direct match: {domain}.ext
        foreach (var ext in extensions)
        {
            var path = Path.Combine(_appDataIconsDir, $"{domain}{ext}");
            if (File.Exists(path))
            {
                var img = LoadImageFromFile(path);
                if (img != null)
                    return img;
            }
        }

        // 2. Base domain without TLD (e.g. domain "rapidgator.net" -> try "rapidgator.png")
        var dotIdx = domain.IndexOf('.');
        if (dotIdx > 0)
        {
            var nameWithoutTld = domain.Substring(0, dotIdx);
            foreach (var ext in extensions)
            {
                var path = Path.Combine(_appDataIconsDir, $"{nameWithoutTld}{ext}");
                if (File.Exists(path))
                {
                    var img = LoadImageFromFile(path);
                    if (img != null)
                        return img;
                }
            }
        }

        // 3. Known hoster alternate filenames (e.g. hoster Name, DisplayName, or Domain)
        foreach (var hoster in HosterInfo.KnownHosters)
        {
            if (string.Equals(hoster.Domain, domain, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hoster.Name, domain, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hoster.DisplayName, domain, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(hoster.DomainPattern) && Regex.IsMatch(domain, hoster.DomainPattern, RegexOptions.IgnoreCase)))
            {
                var candidates = new[] { hoster.Domain, hoster.Name, hoster.DisplayName.ToLowerInvariant() };
                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    foreach (var ext in extensions)
                    {
                        var path = Path.Combine(_appDataIconsDir, $"{candidate}{ext}");
                        if (File.Exists(path))
                        {
                            var img = LoadImageFromFile(path);
                            if (img != null)
                                return img;
                        }
                    }
                }
            }
        }

        // 4. Wildcard prefix match if domain has no dot (e.g. "rapidgator" -> matches "rapidgator.net.png")
        if (!domain.Contains('.'))
        {
            try
            {
                foreach (var file in Directory.GetFiles(_appDataIconsDir, $"{domain}.*"))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".png" or ".ico" or ".jpg" or ".jpeg" or ".bmp")
                    {
                        var img = LoadImageFromFile(file);
                        if (img != null)
                            return img;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Determines the canonical domain of a hoster from name or URL.
    /// Known hosters are resolved via <see cref="HosterInfo.KnownHosters"/>,
    /// unknown URLs via the host portion of the URL.
    /// </summary>
    public static string ExtractDomain(string input) => _default.Value.GetDomain(input);

    private string GetDomain(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "unknown";

        var lower = input.Trim().ToLowerInvariant();

        // Extract host part from URL (if present)
        string? host = null;
        if (lower.Contains("://"))
        {
            try { host = new Uri(lower).Host; } catch { }
        }

        // 1. Known hoster via URL host (including alias domains like rg.to, ddl.to)
        if (host != null)
        {
            foreach (var hoster in HosterInfo.KnownHosters)
            {
                if (host == hoster.Domain
                    || host.EndsWith("." + hoster.Domain, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(hoster.DomainPattern) && Regex.IsMatch(host, hoster.DomainPattern, RegexOptions.IgnoreCase)))
                {
                    return hoster.Domain;
                }
            }
        }

        // 2. Known hoster passed as Name/DisplayName/Domain/Pattern (e.g. "Rapidgator", "rg.to", legacy names)
        foreach (var hoster in HosterInfo.KnownHosters)
        {
            if (lower == hoster.Name
                || lower == hoster.DisplayName.ToLowerInvariant()
                || lower == hoster.Domain
                || (!string.IsNullOrEmpty(hoster.DomainPattern) && Regex.IsMatch(lower, hoster.DomainPattern, RegexOptions.IgnoreCase)))
            {
                return hoster.Domain;
            }
        }

        // 3. Generic URL -> Host or base domain
        if (host != null)
        {
            if (host.StartsWith("www.", StringComparison.Ordinal))
                host = host.Substring(4);

            var parts = host.Split('.');
            if (parts.Length > 2)
            {
                var baseDomain = string.Join(".", parts.Skip(parts.Length - 2));
                if (_iconCache.ContainsKey(baseDomain) || FindIconOnDisk(baseDomain) != null)
                    return baseDomain;
            }
            return host;
        }

        return lower.Replace(" ", "").Replace("www.", "");
    }

    private static ImageSource? LoadImageFromFile(string filePath)
    {
        try
        {
            if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault() ?? decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource CreateGenericGlobeIcon()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(37, 99, 235)), null, new Rect(0, 0, 24, 24));
            var geom = Geometry.Parse("F1 M 12,3 C 7.03,3 3,7.03 3,12 C 3,16.97 7.03,21 12,21 C 16.97,21 21,16.97 21,12 C 21,7.03 16.97,3 12,3 Z M 11,18.93 C 7.5,18.44 5,15.5 5,12 C 5,11.5 5.1,11 5.3,10.5 L 9,14.2 L 9,15 C 9,16.1 9.9,17 11,17 Z M 17.9,16.39 C 17.6,15.58 16.9,15 16,15 L 15,12 C 15,11.45 14.55,11 14,11 L 8,9 L 10,9 C 10.55,9 11,8.55 11,8 L 11,6 L 13,6 C 14.1,6 15,5.1 15,4 C 17.6,5.1 19.5,7.6 19.9,10.6 C 19.8,12.9 19,14.9 17.9,16.39 Z");
            dc.DrawGeometry(Brushes.White, null, geom);
        }
        var drawing = new DrawingImage(group);
        drawing.Freeze();
        return drawing;
    }

    private static ImageSource CreateGenericLetterBadge(string domain)
    {
        string letter = domain.Length > 0 ? char.ToUpperInvariant(domain[0]).ToString() : "H";
        if (domain.Contains(Extractor.FastHostResolver.CanonicalDomain) || domain.Contains("fasthost")) letter = "FH";
        else if (domain.Contains("rapidgator")) letter = "RG";
        else if (domain.Contains("ddownload")) letter = "DD";
        else if (domain.Contains("1fichier")) letter = "1F";

        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(59, 130, 246)), null, new Rect(0, 0, 24, 24));

            var ft = new FormattedText(
                letter,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Arial"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
                letter.Length > 2 ? 8.5 : 12,
                Brushes.White,
                96);

            double x = (24 - ft.Width) / 2;
            double y = (24 - ft.Height) / 2;
            dc.DrawText(ft, new Point(x, y));
        }

        var drawing = new DrawingImage(group);
        drawing.Freeze();
        return drawing;
    }
}
