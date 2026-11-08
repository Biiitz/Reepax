using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class DownloadUrlIdentityTests
{
    public static IEnumerable<object[]> InputCases()
    {
        foreach (string route in new[] { "text", "html", "drop-text", "drop-html", "clipboard" })
        {
            yield return new object[] { route, "/Payload.bin", "/payload.bin" };
            yield return new object[] { route, "/payload.bin?token=Ab1", "/payload.bin?token=aB1" };
            yield return new object[] { route, "/payload.bin#KeyAb1", "/payload.bin#KeyaB1" };
        }
    }

    [Theory]
    [MemberData(nameof(InputCases))]
    public void InputRoutes_PreserveDistinctResourcesAndRemoveOnlyDuplicates(string route, string first, string second)
    {
        string a = "https://files.example.invalid" + first;
        string b = "https://files.example.invalid" + second;
        string duplicate = "HTTPS://FILES.EXAMPLE.INVALID" + first;

        Assert.Equal(new[] { a, b }, Extract(route, new[] { a, b, duplicate, a }));
    }

    private static string[] Extract(string route, string[] urls)
    {
        string text = string.Join(Environment.NewLine, urls);
        string html = string.Join("", urls.Select(url => $"<a href=\"{url}\">Download</a>"));
        if (route == "text" || route == "html")
            return LinkExtractor.ExtractLinks(route == "text" ? text : html).Select(link => link.Url).ToArray();
        if (route == "clipboard")
            return ClipboardMonitorService.ExtractLinksFromData(html, text).ToArray();
        var data = new DataObject();
        data.SetData(route == "drop-html" ? DataFormats.Html : DataFormats.UnicodeText,
            route == "drop-html" ? html : text);
        return LinkExtractor.ExtractUrlsFromDataObject(data).ToArray();
    }

    [Theory]
    [InlineData("HTTPS://FILES.EXAMPLE.INVALID/payload.bin?token=Ab1", "https://files.example.invalid/payload.bin?token=Ab1", true)]
    [InlineData("https://files.example.invalid", "https://FILES.EXAMPLE.INVALID/", true)]
    [InlineData("ftp://FILES.EXAMPLE.INVALID/payload.bin", "FTP://files.example.invalid/payload.bin", true)]
    [InlineData("https://files.example.invalid/payload.bin?sig=%2BAb%2F", "https://files.example.invalid/payload.bin?sig=%2BaB%2F", false)]
    [InlineData("https://files.example.invalid/payload.bin?sig=%2BAb%2F", "https://files.example.invalid/payload.bin?sig=%2BAb%2f", false)]
    [InlineData("https://test:Sample@files.example.invalid/payload.bin", "https://test:sample@files.example.invalid/payload.bin", false)]
    [InlineData("https://files.example.invalid:8080/payload.bin", "https://files.example.invalid:8081/payload.bin", false)]
    [InlineData("https://files.example.invalid/payload.bin", "http://files.example.invalid/payload.bin", false)]
    [InlineData("https://files.example.invalid/payload", "https://files.example.invalid/payload/", false)]
    public void UrlComparison_PreservesResourceAndAuthorityBoundaries(string first, string second, bool equivalent)
    {
        var comparer = DownloadUrlComparer.Instance;
        Assert.Equal(equivalent, comparer.Equals(first, second));
        Assert.Equal(equivalent ? 1 : 2, new HashSet<string>(new[] { first, second }, comparer).Count);
        if (equivalent) Assert.Equal(comparer.GetHashCode(first), comparer.GetHashCode(second));
    }

    [Fact]
    public void ClipboardDispatch_DoesNotSuppressAChangedCaseSensitiveResource()
    {
        using var monitor = (ClipboardMonitorService)Activator.CreateInstance(typeof(ClipboardMonitorService), nonPublic: true)!;
        var detected = new List<string[]>();
        monitor.Start(new IntPtr(1), links => { detected.Add(links.ToArray()); return true; });
        string first = "https://files.example.invalid/payload.bin?token=Ab1";
        string second = "https://files.example.invalid/payload.bin?token=aB1";

        monitor.DispatchDetectedLinks(new List<string> { first });
        monitor.DispatchDetectedLinks(new List<string> { "HTTPS://FILES.EXAMPLE.INVALID/payload.bin?token=Ab1" });
        monitor.DispatchDetectedLinks(new List<string> { second });
        monitor.DispatchDetectedLinks(new List<string> { second });

        Assert.Equal(2, detected.Count);
        Assert.Equal(new[] { first }, detected[0]);
        Assert.Equal(new[] { second }, detected[1]);
    }

    [Fact]
    public async Task ExtractedLocalLinks_RetrieveEachCaseSensitiveResourceByteForByte()
    {
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Payload.bin"] = new byte[] { 2, 3, 5 },
            ["payload.bin"] = new byte[] { 7, 11, 13 },
            ["payload.bin?token=Ab1"] = new byte[] { 17, 19, 23 },
            ["payload.bin?token=aB1"] = new byte[] { 29, 31, 37 }
        };
        await using var source = new PipelineHttpSource(originals);
        using var client = new HttpClient();
        var links = LinkExtractor.ExtractLinks(string.Join("\n", originals.Keys.Select(source.Url)));

        Assert.Equal(originals.Count, links.Count);
        foreach (var pair in originals)
        {
            var link = Assert.Single(links.Where(link => link.Url == source.Url(pair.Key)));
            Assert.Equal(pair.Value, await client.GetByteArrayAsync(link.Url));
            Assert.Equal(1, source.Requests[pair.Key]);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebPageExtraction_PreservesDistinctLocalLinks(bool anchors)
    {
        var files = new Dictionary<string, byte[]>();
        await using var source = new PipelineHttpSource(files);
        string[] urls = new[] { source.Url("Payload.bin"), source.Url("payload.bin"),
            source.Url("payload.bin?token=Ab1"), source.Url("payload.bin?token=aB1") };
        files["page"] = Encoding.UTF8.GetBytes("<title>Example package</title>" + string.Join("\n",
            urls.Concat(new[] { urls[0] }).Select(url => anchors ? $"<a href=\"{url}\">payload.bin</a>" : url)));
        var localHost = new HosterInfo { Name = "local-fixture", DomainPattern = @"^http://127\.0\.0\.1:" };
        HosterInfo.KnownHosters.Add(localHost);
        try
        {
            var result = await WebPageLinkExtractor.ExtractAsync(source.Url("page"));
            Assert.Equal(urls, result.Links.Select(link => link.Url));
            Assert.Equal(1, source.Requests["page"]);
        }
        finally { HosterInfo.KnownHosters.Remove(localHost); }
    }

    [Fact]
    public async Task AddLinksToPackage_PreservesExistingDataAndDistinctUrlsDespiteFilenameCollisions()
    {
        string root = Directory.CreateTempSubdirectory("UrlIdentity_").FullName;
        var model = new MainViewModel();
        var previous = model.Packages.ToArray();
        var queue = QueueManager.Instance;
        bool queueWasRunning = queue.IsRunning;
        model.Packages.Clear();
        queue.StopQueue();
        Assert.False(queue.IsRunning);
        var files = new Dictionary<string, byte[]>
        {
            ["Payload.bin?token=Ab1"] = new byte[] { 2, 3, 5 },
            ["Payload.bin?token=aB1"] = new byte[] { 7, 11, 13 },
            ["payload.bin?token=Ab1"] = new byte[] { 17, 19, 23 }
        };
        await using var source = new PipelineHttpSource(files);
        using var client = new HttpClient();
        model.MetadataResolver = new LinkMetadataResolverService(client);
        var package = new DownloadPackage { Name = "Example", SaveDirectory = root, AutoPar2Repair = false };
        var existing = new DownloadItem { OriginalUrl = source.Url(files.Keys.First()), FileName = "Payload.bin",
            SaveFilePath = Path.Combine(root, "Payload.bin"), Status = DownloadStatus.Completed,
            DownloadedBytes = 3, TotalBytes = 3, IsMetadataResolved = true,
            ExpectedChecksum = Convert.ToHexString(SHA256.HashData(files.Values.First())),
            CalculatedChecksum = Convert.ToHexString(SHA256.HashData(files.Values.First())) };
        package.Items.Add(existing);
        model.Packages.Add(package);
        File.WriteAllBytes(existing.SaveFilePath, files.Values.First());
        byte[] foreign = new byte[] { 41, 43 };
        File.WriteAllBytes(Path.Combine(root, "Payload_1.bin"), foreign);
        try
        {
            model.AddLinksToPackage(package, string.Join("\n", files.Keys.Select(source.Url).Append(existing.OriginalUrl)));
            await model.ResolvePackageSizesAsync(new[] { package });

            Assert.Equal(files.Count, package.Items.Count);
            Assert.Same(existing, package.Items[0]);
            Assert.Equal(DownloadStatus.Completed, existing.Status);
            Assert.Equal(existing.ExpectedChecksum, existing.CalculatedChecksum);
            Assert.Equal(files.Values.First(), File.ReadAllBytes(existing.SaveFilePath));
            Assert.Equal(foreign, File.ReadAllBytes(Path.Combine(root, "Payload_1.bin")));
            Assert.Equal(files.Count, package.Items.Select(item => item.SaveFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var item in package.Items.Skip(1))
            {
                Assert.Equal(DownloadStatus.Paused, item.Status);
                Assert.Equal(3, item.TotalBytes);
                Assert.False(File.Exists(item.SaveFilePath));
                Assert.Equal(files[new Uri(item.OriginalUrl).PathAndQuery.TrimStart('/')],
                    await client.GetByteArrayAsync(item.OriginalUrl));
            }
            using var storage = new DownloadPersistenceService(Path.Combine(root, "downloads.json"));
            storage.SaveDownloads(new[] { package }, sync: true);
            Assert.Equal(package.Items.Select(item => item.OriginalUrl), Assert.Single(storage.LoadDownloads()).Items.Select(item => item.OriginalUrl));
        }
        finally
        {
            queue.StopQueue();
            model.Packages.Clear();
            if (queueWasRunning) queue.StartQueue();
            foreach (var saved in previous) model.Packages.Add(saved);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/Payload.bin", "/payload.bin", false)]
    [InlineData("/payload.bin?token=Ab1", "/payload.bin?token=aB1", false)]
    [InlineData("/payload.bin#KeyAb1", "/payload.bin#KeyaB1", false)]
    [InlineData("/payload.bin?token=Ab1", "/payload.bin?token=aB1", true)]
    [InlineData("/payload", "/payload/", false)]
    [InlineData("/payload.bin?token=Ab1", "/payload.bin?token=Ab1/", false)]
    public async Task EditPackage_ReplacesDistinctResourceWithoutChangingOriginalOrUnrelatedFiles(string first, string second, bool direct)
    {
        await CheckEditAsync(first, second, direct, preserve: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditPackage_EquivalentSchemeAndHostPreserveState(bool direct)
    {
        await CheckEditAsync("/payload.bin?token=Ab1", "/payload.bin?token=Ab1", direct, preserve: true);
    }

    private static async Task CheckEditAsync(string first, string second, bool direct, bool preserve)
    {
        string root = Directory.CreateTempSubdirectory("UrlEdit_").FullName;
        var model = new MainViewModel();
        var previous = model.Packages.ToArray();
        var queue = QueueManager.Instance;
        bool queueWasRunning = queue.IsRunning;
        model.Packages.Clear();
        queue.StopQueue();
        Assert.False(queue.IsRunning);
        using var handler = new StaticMetadataHandler();
        using var client = new HttpClient(handler);
        model.MetadataResolver = new LinkMetadataResolverService(client);
        var package = new DownloadPackage { Name = "Example", SaveDirectory = root, AutoPar2Repair = false };
        byte[] original = new byte[] { 2, 3, 5 };
        string hash = Convert.ToHexString(SHA256.HashData(original));
        string oldUrl = "https://files.example.invalid" + first;
        var item = new DownloadItem { OriginalUrl = direct ? "https://files.example.invalid/page" : oldUrl,
            DirectDownloadUrl = direct ? oldUrl : null, FileName = "payload.bin", SaveFilePath = Path.Combine(root, "payload.bin"),
            Status = DownloadStatus.Completed, TotalBytes = 3, DownloadedBytes = 3, IsMetadataResolved = true,
            ExpectedChecksum = hash, CalculatedChecksum = hash };
        var other = new DownloadItem { OriginalUrl = "https://files.example.invalid/other.bin", FileName = "other.bin",
            SaveFilePath = Path.Combine(root, "other.bin"), Status = DownloadStatus.Paused, DownloadedBytes = 2,
            TotalBytes = 10, IsMetadataResolved = true };
        package.AddItems(new[] { item, other });
        model.Packages.Add(package);
        File.WriteAllBytes(item.SaveFilePath, original);
        byte[] partial = new byte[] { 7, 11 };
        File.WriteAllBytes(other.SaveFilePath + ".part", partial);
        File.WriteAllText(other.SaveFilePath + ".part.segments", "checkpoint-fixture");
        var localHost = new HosterInfo { Name = "fixture", DomainPattern = @"^https?://files\.example\.invalid/" };
        HosterInfo.KnownHosters.Add(localHost);
        try
        {
            string edited = (preserve ? "HTTPS://FILES.EXAMPLE.INVALID" : "https://files.example.invalid") + second;
            await model.EditPackageAsync(package, edited + "\n" + other.OriginalUrl, package.Name, root, false, false, false, false);
            await model.ResolvePackageSizesAsync(new[] { package });

            Assert.Equal(2, package.Items.Count);
            Assert.Contains(other, package.Items);
            Assert.Equal(DownloadStatus.Paused, other.Status);
            Assert.Equal(2, other.DownloadedBytes);
            Assert.Equal(partial, File.ReadAllBytes(other.SaveFilePath + ".part"));
            Assert.Equal("checkpoint-fixture", File.ReadAllText(other.SaveFilePath + ".part.segments"));
            Assert.Equal(original, File.ReadAllBytes(item.SaveFilePath));
            if (preserve)
            {
                Assert.Same(item, package.Items[0]);
                Assert.Equal(DownloadStatus.Completed, item.Status);
                Assert.Equal(hash, item.ExpectedChecksum);
                Assert.Equal(hash, item.CalculatedChecksum);
            }
            else
            {
                var added = Assert.Single(package.Items.Where(candidate => candidate != other));
                Assert.DoesNotContain(item, package.Items);
                Assert.Equal(edited, added.OriginalUrl);
                Assert.NotEqual(item.SaveFilePath, added.SaveFilePath, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(DownloadStatus.Paused, added.Status);
                Assert.Equal(0, added.DownloadedBytes);
                Assert.True(string.IsNullOrEmpty(added.ExpectedChecksum));
                Assert.True(string.IsNullOrEmpty(added.CalculatedChecksum));
            }
        }
        finally
        {
            HosterInfo.KnownHosters.Remove(localHost);
            queue.StopQueue();
            model.Packages.Clear();
            if (queueWasRunning) queue.StartQueue();
            foreach (var saved in previous) model.Packages.Add(saved);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticMetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new ByteArrayContent(new byte[] { 2, 3, 5 }) });
    }
}
