using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reepax.Services.Update;
using Velopack;
using Velopack.Locators;
using Velopack.NuGet;

namespace Reepax.Tests;

[Trait("Category", "Integration")]
public sealed class VelopackUpdateTests : IDisposable
{
    private const string NextVersion = "26.11.2";
    private readonly string _root = Directory.CreateTempSubdirectory("PackageUpdate_").FullName;
    private readonly Dictionary<string, byte[]> _assets = new();
    private readonly Dictionary<string, int> _requests = new();
    private readonly HttpClient _client;
    private readonly AppUpdateService _service;
    private readonly RecordingLocator _locator;
    private int _shutdowns;
    private string PackageName => AppUpdateService.PackageId + "-" + NextVersion + "-full.nupkg";

    public VelopackUpdateTests()
    {
        string cache = Directory.CreateDirectory(Path.Combine(_root, "packages")).FullName;
        string current = Directory.CreateDirectory(Path.Combine(_root, "current")).FullName;
        File.WriteAllText(Path.Combine(current, "update-files.txt"), "");
        string updater = Path.Combine(_root, "Update.exe");
        File.WriteAllText(updater, "updater-sentinel");
        _locator = new RecordingLocator(cache, current, _root, updater);
        _client = new HttpClient(new Handler(async (request, token) =>
        {
            string name = request.RequestUri!.AbsolutePath.EndsWith("latest") ? "release" : Uri.UnescapeDataString(request.RequestUri.Segments[^1]);
            _requests[name] = _requests.GetValueOrDefault(name) + 1;
            if (BeforeResponse != null) await BeforeResponse(name, token);
            return new HttpResponseMessage(_assets.TryGetValue(name, out var bytes) ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent(bytes ?? Array.Empty<byte>()), RequestMessage = request
            };
        }));
        _service = new AppUpdateService(_client, _client, _locator) { ShutdownAfterPackageUpdate = () => _shutdowns++ };
        PublishPackage();
    }

    private Func<string, CancellationToken, Task>? BeforeResponse { get; set; }

    [Fact]
    public async Task Update_DownloadsVerifiedPackageAndSchedulesOneRestartBeforeShutdown()
    {
        byte[] unrelated = { 7, 3, 1 };
        string foreign = Path.Combine(_locator.PackagesDir!, "unrelated.nupkg");
        File.WriteAllBytes(foreign, unrelated);
        var info = await Check();
        Assert.True(info.IsPackageUpdate);
        Assert.Equal("Example release notes", info.Changelog);
        string downloaded = await _service.DownloadUpdateAsync(info);

        Assert.Equal(_assets[PackageName], File.ReadAllBytes(downloaded));
        Assert.Equal(Sha256(_assets[PackageName]).ToLowerInvariant(), info.VerifiedSha256);
        Assert.Equal(1, _requests[PackageName]);
        Assert.Equal(unrelated, File.ReadAllBytes(foreign));
        Assert.Empty(_locator.Launches);
        Assert.Equal(0, _shutdowns);
        Assert.True(_service.ApplyDownloadedUpdate(info, downloaded));
        var launch = Assert.Single(_locator.Launches);
        Assert.Contains("apply", launch);
        Assert.Contains("--restart", launch);
        Assert.Equal(1, _shutdowns);
        Assert.Equal(unrelated, File.ReadAllBytes(foreign));
    }

    [Theory]
    [InlineData("feed-hash")]
    [InlineData("package-hash")]
    [InlineData("duplicate")]
    [InlineData("path")]
    [InlineData("version")]
    [InlineData("identity")]
    public async Task Check_RejectsInconsistentOrUnsafeFeedBeforeDownloadingPackages(string failure)
    {
        var asset = Asset(_assets[PackageName]);
        switch (failure)
        {
            case "package-hash": asset.SHA256 = new string('0', 64); break;
            case "path": asset.FileName = "../" + asset.FileName; break;
            case "version": asset.Version = SemanticVersion.Parse("26.11.3"); break;
            case "identity": asset.PackageId = "Other.Application"; break;
        }
        PublishFeed(failure == "duplicate" ? new[] { asset, asset } : new[] { asset });
        if (failure == "feed-hash") _assets[AppUpdateService.PackageFeedName][0] ^= 1;

        Assert.Null(await _service.CheckForUpdatesAsync("26.11.1"));
        Assert.Equal(UpdateCheckStatus.Failed, _service.LastCheckStatus);
        Assert.False(_requests.ContainsKey(PackageName));
        Assert.Empty(_locator.Launches);
        Assert.Equal(0, _shutdowns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_RejectsCorruptionAndRecoversOnRetry(bool cached)
    {
        var info = await Check();
        byte[] valid = _assets[PackageName].ToArray();
        string target = Path.Combine(_locator.PackagesDir!, PackageName);
        byte[] invalid = valid.ToArray();
        invalid[^1] ^= 1;
        if (cached)
        {
            File.WriteAllBytes(target, valid);
            using var archive = ZipFile.Open(target, ZipArchiveMode.Update);
            using var content = archive.GetEntry("lib/app/example.bin")!.Open();
            content.Write(new byte[] { 9, 8, 7, 6 });
        }
        else _assets[PackageName] = invalid;

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.DownloadUpdateAsync(info));
        Assert.Null(info.VerifiedSha256);
        Assert.Empty(_locator.Launches);
        Assert.Equal(0, _shutdowns);
        _assets[PackageName] = valid;
        string downloaded = await _service.DownloadUpdateAsync(info);
        Assert.Equal(valid, File.ReadAllBytes(downloaded));
        Assert.NotNull(info.VerifiedSha256);
    }

    [Fact]
    public async Task Download_RejectsWrongPackageIdentityWithoutReplacingUpdater()
    {
        _assets[PackageName] = CreatePackage("Other.Application", NextVersion);
        PublishFeed(new[] { Asset(_assets[PackageName]) });
        var info = await Check();

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.DownloadUpdateAsync(info));

        Assert.Equal("updater-sentinel", File.ReadAllText(_locator.UpdateExePath!));
        Assert.Null(info.VerifiedSha256);
        Assert.Empty(_locator.Launches);
    }

    [Fact]
    public async Task Download_CancelsAndRejectsParallelAttemptThenAllowsRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeforeResponse = async (name, token) =>
        {
            if (name != PackageName) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var info = await Check();
        using var cancel = new CancellationTokenSource();
        var downloading = _service.DownloadUpdateAsync(info, cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DownloadUpdateAsync(info));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloading);
        Assert.Null(info.VerifiedSha256);
        Assert.Empty(_locator.Launches);
        BeforeResponse = null;
        Assert.Equal(_assets[PackageName], File.ReadAllBytes(await _service.DownloadUpdateAsync(info)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_RejectsChangedOrUnverifiedPackagesAndDoesNotShutDown(bool tamper)
    {
        var info = await Check();
        string path = await _service.DownloadUpdateAsync(info);
        if (tamper) File.AppendAllText(path, "changed");
        else info.VerifiedSha256 = null;

        Assert.False(_service.ApplyDownloadedUpdate(info, path));
        Assert.Empty(_locator.Launches);
        Assert.Equal(0, _shutdowns);
    }

    [Fact]
    public async Task Apply_HelperLaunchFailureLeavesApplicationRunningAndAllowsRetry()
    {
        var info = await Check();
        string path = await _service.DownloadUpdateAsync(info);
        _locator.FailLaunch = true;
        Assert.False(_service.ApplyDownloadedUpdate(info, path));
        Assert.Equal(0, _shutdowns);
        Assert.Equal(_assets[PackageName], File.ReadAllBytes(path));
        _locator.FailLaunch = false;
        Assert.True(_service.ApplyDownloadedUpdate(info, path));
        Assert.Equal(1, _shutdowns);
    }

    [Fact]
    public async Task Apply_PreservesAdditionalUserFilesAndAllowsRetryAfterTheyAreMoved()
    {
        var info = await Check();
        string downloaded = await _service.DownloadUpdateAsync(info);
        string userFile = Path.Combine(_locator.AppContentDir!, "user-notes.txt");
        File.WriteAllText(userFile, "preserve");
        Assert.False(_service.ApplyDownloadedUpdate(info, downloaded));
        Assert.Equal("preserve", File.ReadAllText(userFile));
        Assert.Empty(_locator.Launches);
        Assert.Equal(0, _shutdowns);
        Assert.Equal(Reepax.Services.Localization.Loc.Get("UpdateDialog_Status_AdditionalFiles"), _service.LastApplyError);
        File.Move(userFile, Path.Combine(_root, "user-notes.txt"));
        Assert.True(_service.ApplyDownloadedUpdate(info, downloaded));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(_root, "user-notes.txt")));
    }

    private async Task<Reepax.Services.Update.UpdateInfo> Check()
    {
        var info = await _service.CheckForUpdatesAsync("26.11.1");
        Assert.NotNull(info);
        Assert.Equal(UpdateCheckStatus.Available, _service.LastCheckStatus);
        return info!;
    }

    private void PublishPackage()
    {
        _assets[PackageName] = CreatePackage(AppUpdateService.PackageId, NextVersion);
        PublishFeed(new[] { Asset(_assets[PackageName]) });
    }

    private void PublishFeed(VelopackAsset[] assets)
    {
        using (var zip = new ZipArchive(new MemoryStream(_assets[PackageName])))
        {
            var contents = zip.Entries.ToDictionary(entry => entry.FullName, entry =>
            {
                using var input = entry.Open();
                return new PackageFileDigest(entry.Length, Convert.ToHexString(SHA256.HashData(input)));
            });
            _assets[AppUpdateService.ContentManifestName(NextVersion)] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(contents));
        }
        _assets[AppUpdateService.PackageFeedName] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Assets = assets.Select(asset => new { asset.PackageId, Version = asset.Version.ToString(), Type = asset.Type.ToString(),
                asset.FileName, asset.Size, asset.SHA1, asset.SHA256 })
        }));
        var published = _assets.Where(entry => entry.Key != "release").Select(entry => new
        {
            name = entry.Key, browser_download_url = AssetUrl(entry.Key), size = entry.Value.Length, digest = "sha256:" + Sha256(entry.Value).ToLowerInvariant()
        }).ToArray();
        _assets["release"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            tag_name = "v" + NextVersion, name = "Example update", body = "Example release notes", assets = published
        }));
    }

    private VelopackAsset Asset(byte[] bytes) => new()
    {
        PackageId = AppUpdateService.PackageId, FileName = PackageName, Version = SemanticVersion.Parse(NextVersion),
        Type = VelopackAssetType.Full, Size = bytes.Length, SHA1 = Convert.ToHexString(SHA1.HashData(bytes)), SHA256 = Sha256(bytes)
    };

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string AssetUrl(string name) => UpdateReliabilityTests.AssetUrl(name).Replace("v26.10.5", "v" + NextVersion);

    private static byte[] CreatePackage(string id, string version)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("package.nuspec").Open()))
                writer.Write($"<package><metadata><id>{id}</id><version>{version}</version><authors>Example</authors><description>Update fixture</description><mainExe>Reepax.exe</mainExe><channel>win</channel></metadata></package>");
            using var file = archive.CreateEntry("lib/app/example.bin").Open();
            file.Write(new byte[] { 0, 1, 127, 255 });
        }
        return output.ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class RecordingLocator(string cache, string current, string root, string updater)
        : TestVelopackLocator(AppUpdateService.PackageId, "26.11.1", cache, current, root, updater, "win")
    {
        public List<string[]> Launches { get; } = new();
        public bool FailLaunch { get; set; }
        public override IProcessImpl Process => new RecordingProcess(this);

        private sealed class RecordingProcess(RecordingLocator owner) : IProcessImpl
        {
            public uint GetCurrentProcessId() => 0;
            public string GetCurrentProcessPath() => Path.Combine(owner.AppContentDir!, "Reepax.exe");
            public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
            {
                if (owner.FailLaunch) throw new IOException("Controlled helper launch failure.");
                owner.Launches.Add(args.ToArray());
            }
            public void Exit(int exitCode) => throw new InvalidOperationException("An immediate exit would bypass safe shutdown.");
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
