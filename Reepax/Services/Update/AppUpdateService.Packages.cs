using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Reepax.Services.Update;

public partial class AppUpdateService
{
    internal const string PackageId = "Reepax.Desktop";
    internal const string PackageFeedName = "releases.win.json";
    internal static string ContentManifestName(string version) => $"{PackageId}-{version}-files.json";
    internal static string GetPackageVersion(string appVersion)
    {
        var version = Version.Parse(appVersion);
        // A fourth component sorts between its base release and the next three-part version.
        return version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build + 1}-patch.{version.Revision}"
            : version.ToString(3);
    }
    private readonly IVelopackLocator _packageLocator;
    public string? LastApplyError { get; private set; }
    internal Action ShutdownAfterPackageUpdate { get; set; } = PerformSafeShutdownAndExit;

    private static UpdateInfo ReadPackageAsset(JsonElement release, string name, string tag)
    {
        var matches = release.GetProperty("assets").EnumerateArray().Where(asset =>
            string.Equals(asset.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("A required update asset is missing or duplicated.");
        var asset = matches[0];
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        string digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
        long size = asset.GetProperty("size").GetInt64();
        if (!IsReleaseAssetUrl(url, tag, name) || size <= 0 || !Regex.IsMatch(digest, "\\Asha256:[a-fA-F0-9]{64}\\z"))
            throw new InvalidDataException("The update asset has invalid verification metadata.");
        var info = new UpdateInfo { ReleaseTag = tag, SetupAssetUrl = url, SetupAssetSize = size, AssetFileName = name };
        info.SetExpectedSha256(name, digest[7..]);
        return info;
    }

    private async Task PreparePackageUpdateAsync(UpdateInfo info, JsonElement release, CancellationToken token)
    {
        if (_packageLocator.AppId != PackageId || _packageLocator.IsPortable)
            throw new InvalidDataException("Unsupported update installation.");
        string tag = info.ReleaseTag!;
        var manifest = ReadPackageAsset(release, PackageFeedName, tag);
        if (manifest.TargetDownloadSize > 4 * 1024 * 1024)
            throw new InvalidDataException("The update feed exceeds its size limit.");
        string path = await DownloadAssetAsync(manifest, null, token);
        VelopackAssetFeed feed;
        try { feed = VelopackAssetFeed.FromJson(await File.ReadAllTextAsync(path, token)); }
        finally { DeleteUpdateWorkspace(Path.GetDirectoryName(path)!); }

        var assets = new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in feed.Assets)
        {
            if (asset.PackageId != PackageId || asset.Type is not (VelopackAssetType.Full or VelopackAssetType.Delta) ||
                asset.FileName == null || !IsPackageFileName(asset.FileName))
                throw new InvalidDataException("The update feed contains an invalid package.");
            var published = ReadPackageAsset(release, asset.FileName, tag);
            if (asset.Size != published.TargetDownloadSize ||
                !string.Equals(asset.SHA256, published.GetExpectedSha256(asset.FileName), StringComparison.OrdinalIgnoreCase) ||
                !assets.TryAdd(asset.FileName, published))
                throw new InvalidDataException("The update feed differs from its published assets.");
        }

        var contentInfo = ReadPackageAsset(release, ContentManifestName(info.NewVersion), tag);
        if (contentInfo.TargetDownloadSize > 4 * 1024 * 1024) throw new InvalidDataException("The package inventory exceeds its size limit.");
        string contentPath = await DownloadAssetAsync(contentInfo, null, token);
        Dictionary<string, PackageFileDigest> contents;
        try
        {
            contents = JsonSerializer.Deserialize<Dictionary<string, PackageFileDigest>>(await File.ReadAllTextAsync(contentPath, token))
                ?? throw new InvalidDataException("The package inventory is invalid.");
            if (contents.Count == 0 || contents.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != contents.Count ||
                contents.Any(file => !IsSafePackageEntry(file.Key) || file.Value.Size < 0 || !Regex.IsMatch(file.Value.SHA256 ?? "", "\\A[a-fA-F0-9]{64}\\z")))
                throw new InvalidDataException("The package inventory is invalid.");
        }
        finally { DeleteUpdateWorkspace(Path.GetDirectoryName(contentPath)!); }
        var source = new VerifiedPackageSource(this, feed, assets);
        var manager = new PackageUpdateManager(source, _packageLocator, contents);
        var update = await manager.CheckForUpdatesAsync().WaitAsync(token);
        if (update == null || update.TargetFullRelease.Version.ToString() != GetPackageVersion(info.NewVersion))
            throw new InvalidDataException("The update feed does not match the release version.");
        info.PackageManager = manager;
        info.PackageUpdate = update;
        info.PackageFiles = contents;
        info.AssetFileName = update.TargetFullRelease.FileName;
        info.SetupAssetUrl = assets[info.AssetFileName].TargetDownloadUrl;
        info.SetupAssetSize = update.TargetFullRelease.Size;
        info.SetExpectedSha256(info.AssetFileName, update.TargetFullRelease.SHA256);
    }

    private string GetPackagePath(UpdateInfo info)
    {
        string root = Path.GetFullPath(_packageLocator.PackagesDir ?? throw new InvalidDataException("Missing update cache."));
        string path = Path.GetFullPath(Path.Combine(root, info.TargetFileName));
        ValidateCachePath(path);
        return path;
    }

    private void ValidateCachePath(string path)
    {
        string root = Path.GetFullPath(_packageLocator.PackagesDir ?? throw new InvalidDataException("Missing update cache."));
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid update cache path.");
        for (var current = new DirectoryInfo(root); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The update cache uses a redirected directory.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The update package uses a redirected file.");
    }

    internal static bool IsSafePackageEntry(string name) => name.Length > 0 && !name.Contains('\\') &&
        !name.StartsWith('/') && name.IndexOfAny(new[] { ':', '\0', '<', '>', '|', '?', '*' }) < 0 &&
        name.Split('/').All(segment => segment != ".." && segment != ".");

    internal static bool IsPackageFileName(string name) => Regex.IsMatch(name,
        "\\A" + Regex.Escape(PackageId) + "-[0-9][a-zA-Z0-9.\\-]*-(full|delta)\\.nupkg\\z");

    private static string VerifyPackage(UpdateInfo info, string path, CancellationToken token = default)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (info.VerifiedSha256 != null && !actual.Equals(info.VerifiedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update package failed its integrity check.");
        VerifyPackageContents(path, info.PackageFiles!, token);
        var package = VelopackAsset.FromNupkgNoChecksum(path);
        if (package.PackageId != PackageId || package.Version != info.PackageUpdate!.TargetFullRelease.Version)
            throw new InvalidDataException("The downloaded package has an unexpected identity or version.");
        return actual.ToLowerInvariant();
    }

    private static void VerifyPackageContents(string path, IReadOnlyDictionary<string, PackageFileDigest> inventory, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            if (!seen.Add(entry.FullName) || !inventory.TryGetValue(entry.FullName, out var expected) || entry.Length != expected.Size)
                throw new InvalidDataException("The reconstructed update contains unexpected files.");
            using var input = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                length += count;
                if (length > expected.Size) throw new InvalidDataException("An update file exceeds its verified size.");
                hash.AppendData(buffer, 0, count);
            }
            if (length != expected.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected.SHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An update file failed its integrity check.");
        }
        if (seen.Count != inventory.Count) throw new InvalidDataException("The reconstructed update is incomplete.");
    }

    private async Task<string> DownloadPackageUpdateAsync(UpdateInfo info,
        IProgress<(long bytesDownloaded, long totalBytes)>? progress, CancellationToken token)
    {
        info.VerifiedSha256 = null;
        string path = GetPackagePath(info);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(20));
            await info.PackageManager!.DownloadUpdatesAsync(info.PackageUpdate!,
                percent => progress?.Report((percent, 100)), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            info.VerifiedSha256 = await Task.Run(() => VerifyPackage(info, path, deadline.Token), deadline.Token);
            progress?.Report((100, 100));
            return path;
        }
        catch (InvalidDataException)
        {
            // Only remove the rejected target package from this managed cache.
            File.Delete(path);
            throw;
        }
    }

    public bool ApplyDownloadedUpdate(UpdateInfo info, string path)
    {
        if (!info.IsPackageUpdate)
            return ApplyUpdate(path, info.VerifiedSha256, info.NewVersion);
        if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0) return false;
        LastApplyError = null;
        bool scheduled = false;
        try
        {
            if (!string.Equals(GetPackagePath(info), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
                info.VerifiedSha256 == null)
                throw new InvalidDataException("The update is not verified for this installation.");
            VerifyPackage(info, path);
            ValidateInstallationContents(_packageLocator.AppContentDir!);
            info.PackageManager!.WaitExitThenApplyUpdates(info.PackageUpdate!.TargetFullRelease,
                silent: false, restart: true, restartArgs: new[] { "--restart" });
            scheduled = true;
            ShutdownAfterPackageUpdate();
            return true;
        }
        catch (Exception ex)
        {
            LastApplyError = Localization.Loc.Get(ex is AdditionalUpdateFilesException
                ? "UpdateDialog_Status_AdditionalFiles" : "UpdateDialog_Status_StartFailed");
            Storage.AppLogger.Error("[AppUpdateService] Could not apply the package update", ex);
            return false;
        }
        finally
        {
            if (!scheduled || Storage.DownloadPersistenceService.IsTestEnvironment)
                Interlocked.Exchange(ref _applying, 0);
        }
    }

    internal static void ValidateInstallationContents(string directory)
    {
        string manifest = Path.Combine(directory, "update-files.txt");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("The installation file inventory is unavailable.");
        var known = new HashSet<string>(File.ReadLines(manifest), StringComparer.OrdinalIgnoreCase);
        known.UnionWith(new[] { "update-files.txt", "sq.version", "Reepax_ExecutionStub.exe" });
        foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new AdditionalUpdateFilesException();
            string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if ((attributes & FileAttributes.Directory) == 0 && !known.Contains(relative))
                throw new AdditionalUpdateFilesException();
        }
    }

    private sealed class VerifiedPackageSource(AppUpdateService service, VelopackAssetFeed feed,
        Dictionary<string, UpdateInfo> assets) : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) => Task.FromResult(feed);

        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken)
        {
            if (!assets.TryGetValue(releaseEntry.FileName, out var info))
                throw new InvalidDataException("The requested update package is not published.");
            service.ValidateCachePath(localFile);
            var reporter = new PackageProgress(value =>
                progress((int)Math.Clamp(value.bytesDownloaded * 100d / value.totalBytes, 0, 100)));
            string downloaded = await service.DownloadAssetAsync(info, reporter, cancelToken);
            try
            {
                var identity = VelopackAsset.FromNupkgNoChecksum(downloaded);
                if (identity.PackageId != releaseEntry.PackageId || identity.Version != releaseEntry.Version)
                    throw new InvalidDataException("The downloaded package does not match the update feed.");
                await using var input = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, cancelToken);
            }
            finally { DeleteUpdateWorkspace(Path.GetDirectoryName(downloaded)!); }
        }
    }

    private sealed class PackageProgress(Action<(long bytesDownloaded, long totalBytes)> report)
        : IProgress<(long bytesDownloaded, long totalBytes)>
    {
        public void Report((long bytesDownloaded, long totalBytes) value) => report(value);
    }

    private sealed class PackageUpdateManager(IUpdateSource source, IVelopackLocator locator,
        IReadOnlyDictionary<string, PackageFileDigest> contents)
        : UpdateManager(source, new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false }, locator)
    {
        protected override async Task DownloadAndApplyDeltaUpdates(Velopack.UpdateInfo updates, string targetFile,
            Action<int> progress, CancellationToken cancelToken)
        {
            try
            {
                await base.DownloadAndApplyDeltaUpdates(updates, targetFile, progress, cancelToken);
                VerifyPackageContents(targetFile, contents, cancelToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                cancelToken.ThrowIfCancellationRequested();
                Storage.AppLogger.Warn("[AppUpdateService] Delta preparation failed; downloading the full update", ex);
                await Source.DownloadReleaseEntry(Log, updates.TargetFullRelease, targetFile, progress, cancelToken);
                await VerifyPackageChecksumAsync(updates.TargetFullRelease, targetFile);
                VerifyPackageContents(targetFile, contents, cancelToken);
            }
        }

        protected override void CleanPackagesExcept(string? assetToKeep)
        {
            if (assetToKeep == null || !File.Exists(assetToKeep)) return;
            // The shared cache may contain unrelated files; only remove recognized application packages.
            foreach (string path in Directory.EnumerateFiles(Locator.PackagesDir!, "*.nupkg"))
            {
                if (string.Equals(path, assetToKeep, StringComparison.OrdinalIgnoreCase) || !IsPackageFileName(Path.GetFileName(path)))
                    continue;
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                        VelopackAsset.FromNupkgNoChecksum(path).PackageId != PackageId) continue;
                    File.Delete(path);
                }
                catch (Exception ex) { Storage.AppLogger.Warn("[AppUpdateService] Could not remove a superseded update package", ex); }
            }
        }
    }
}

internal sealed class AdditionalUpdateFilesException() : IOException("The application directory contains additional or redirected files.");

internal sealed class UpdateLogger : IVelopackLogger
{
    internal static UpdateLogger Instance { get; } = new();
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception) =>
        Storage.AppLogger.Log("[Updater] " + message, logLevel.ToString().ToUpperInvariant(), exception);
}

internal sealed record PackageFileDigest(long Size, string SHA256);
