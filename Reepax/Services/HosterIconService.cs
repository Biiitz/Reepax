using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reepax.Helpers;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax.Services;

/// <summary>
/// Manages official filehoster favicons.
/// Icons are loaded lazily: when a hoster appears in the app, its favicon
/// is downloaded and permanently cached under %LocalAppData%\Reepax\Icons\{domain}.png.
/// If the icon already exists in disk cache, the local file is always used.
/// </summary>
public static class HosterIconService
{
    private static readonly ConcurrentDictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> _downloadsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string _appDataIconsDir;
    private static readonly HttpClient _httpClient;
    private static readonly ImageSource _defaultGlobe;

    /// <summary>
    /// Raised on the UI thread when a favicon finishes downloading.
    /// Parameter is the hoster domain.
    /// </summary>
    public static event Action<string>? IconUpdated;

    static HosterIconService()
    {
        _appDataIconsDir = Path.Combine(SettingsService.AppDataDirectory, "Icons");
        Directory.CreateDirectory(_appDataIconsDir);

        _httpClient = HttpUserAgentService.CreateHttpClient(TimeSpan.FromSeconds(8), HttpContentType.Image);

        HttpUserAgentService.UserAgentChanged += _ =>
        {
            HttpUserAgentService.ApplyDefaultBrowserHeaders(_httpClient.DefaultRequestHeaders, null, HttpContentType.Image);
        };

        _defaultGlobe = CreateGenericGlobeIcon();

        // Only load previously needed (and thus existing) icons from disk.
        // No new icons are downloaded at startup.
        LoadAllLocalFavicons();
    }

    private static void LoadAllLocalFavicons()
    {
        if (!Directory.Exists(_appDataIconsDir))
            return;

        try
        {
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
                var canonical = ExtractDomain(rawName);
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
    /// Returns the official favicon for a hoster name, domain, or URL.
    /// If the icon is not yet in AppData cache, it will be downloaded in the background
    /// and reported upon completion via <see cref="IconUpdated"/>.
    /// </summary>
    public static ImageSource GetIconForHoster(string? hosterOrUrl)
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

        string domain = ExtractDomain(trimmed);
        if (domain.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
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

        // 3. Not yet downloaded -> queue download in background
        if (domain.Contains('.') && _downloadsInFlight.TryAdd(domain, 0))
            _ = FetchAndCacheIconAsync(domain);

        // 4. Fallback badge until download finishes
        var fallback = CreateGenericLetterBadge(domain);
        _iconCache[domain] = fallback;
        return fallback;
    }

    private static ImageSource? FindIconOnDisk(string domain)
    {
        if (!Directory.Exists(_appDataIconsDir))
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
    public static string ExtractDomain(string input)
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

    private static async Task FetchAndCacheIconAsync(string domain)
    {
        if (!domain.Contains('.'))
            return;
        try
        {
            // Multi-source fallback chain (DuckDuckGo -> Google -> IconHorse -> Direct)
            string[] sources = {
                $"https://icons.duckduckgo.com/ip3/{domain}.ico",
                $"https://www.google.com/s2/favicons?domain={domain}&sz=64",
                $"https://icon.horse/icon/{domain}",
                $"https://{domain}/favicon.ico"
            };

            foreach (var url in sources)
            {
                try
                {
                    await HttpJitterHelper.DelayJitterAsync(50, 150);

                    var bytes = await _httpClient.GetByteArrayAsync(url);
                    if (bytes == null || bytes.Length < 100)
                        continue;

                    // Decoding validates that it is a valid image (not an HTML error page)
                    var icon = DecodeBestFrame(bytes);
                    if (icon == null)
                        continue;

                    // Always save as authentic PNG under the domain name -> {domain}.png
                    var pngBytes = EncodeAsPng(icon);
                    await File.WriteAllBytesAsync(Path.Combine(_appDataIconsDir, $"{domain}.png"), pngBytes);

                    _iconCache[domain] = icon;

                    if (App.Current != null)
                        await App.Current.Dispatcher.InvokeAsync(() => IconUpdated?.Invoke(domain));
                    return;
                }
                catch { }
            }
        }
        finally
        {
            _downloadsInFlight.TryRemove(domain, out _);
        }
    }

    private static BitmapSource? DecodeBestFrame(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames == null || decoder.Frames.Count == 0)
                return null;

            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault() ?? decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncodeAsPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static ImageSource? LoadImageFromFile(string filePath)
    {
        try
        {
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
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    private static ImageSource CreateGenericGlobeIcon()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(37, 99, 235)), null, new Rect(0, 0, 24, 24), 5, 5);
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
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(59, 130, 246)), null, new Rect(0, 0, 24, 24), 5, 5);

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
