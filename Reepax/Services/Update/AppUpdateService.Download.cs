using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Reepax.Services.Download;
using Reepax.Services.Storage;

namespace Reepax.Services.Update;

public enum UpdateCheckStatus { Checking, Failed, UpToDate, Available, Incomplete }

public partial class AppUpdateService
{
    public UpdateCheckStatus LastCheckStatus { get; private set; }
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    internal static bool IsReleaseAssetUrl(string value, string tag, string name)
    {
        var repository = new Uri(GitHubApiLatestReleaseUrl).AbsolutePath.Split('/');
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsolutePath == $"/{repository[2]}/{repository[3]}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
    }

    private static bool IsAllowedRedirect(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" ||
        uri.Host == "objects.githubusercontent.com");

    private static void AddChecksum(Dictionary<string, string> hashes, string name, string hash)
    {
        hash = hash.ToLowerInvariant();
        if (hashes.TryGetValue(name, out var previous) && previous != hash)
            throw new InvalidDataException("Conflicting update checksums.");
        hashes[name] = hash;
    }

    internal static HttpClient CreateUpdateClient(TimeSpan timeout) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = timeout };

    private static async Task<HttpResponseMessage> GetUpdateResponseAsync(HttpClient client, string url,
        CancellationToken token)
    {
        var uri = new Uri(url);
        for (int i = 0; i < 6; i++)
        {
            if (!IsAllowedRedirect(uri)) throw new InvalidDataException("Untrusted update destination.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Reepax-AppUpdater");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsAllowedRedirect(finalUri))
            {
                response.Dispose();
                throw new InvalidDataException("Untrusted update redirect.");
            }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new InvalidDataException("Missing update redirect.");
                uri = new Uri(uri, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); }
            catch { response.Dispose(); throw; }
            return response;
        }
        throw new InvalidDataException("Too many update redirects.");
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo updateInfo,
        IProgress<(long bytesDownloaded, long totalBytes)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await DownloadGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("An update download is already running.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        cancellationToken = deadline.Token;
        string? updateDir = null;
        updateInfo.VerifiedSha256 = null;
        try
        {
            string fileName = updateInfo.TargetFileName;
            var url = updateInfo.TargetDownloadUrl;
            string tag = updateInfo.ReleaseTag ?? "v" + updateInfo.NewVersion;
            if (url == null || !IsReleaseAssetUrl(url, tag, fileName) || updateInfo.TargetDownloadSize <= 0)
                throw new InvalidDataException("The release does not contain a valid update for this installation.");

            if (updateInfo.Sha256ChecksumAssetUrl is { } checksumUrl)
            {
                if (!IsReleaseAssetUrl(checksumUrl, tag, "SHA256SUMS.txt"))
                    throw new InvalidDataException("Invalid checksum destination.");
                using var checksumResponse = await GetUpdateResponseAsync(_httpClient, checksumUrl, cancellationToken);
                var content = await ReadLimitedAsync(checksumResponse.Content, 65536, cancellationToken);
                var published = ParseSha256Checksums(System.Text.Encoding.UTF8.GetString(content));
                if (!published.ContainsKey(fileName)) throw new InvalidDataException("The checksum file does not cover this update.");
                foreach (var pair in published)
                    AddChecksum(updateInfo.ExpectedSha256Hashes, pair.Key, pair.Value);
            }
            var expected = updateInfo.GetExpectedSha256(fileName);
            if (expected == null || !Regex.IsMatch(expected, "\\A[0-9a-fA-F]{64}\\z"))
                throw new InvalidDataException("A published SHA-256 checksum is required.");

            updateDir = Path.Combine(Path.GetTempPath(), "Reepax", "Update", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(updateDir);
            RestrictDirectoryAccessToCurrentUser(updateDir);
            string path = Path.Combine(updateDir, fileName);
            using var response = await GetUpdateResponseAsync(_downloadHttpClient, url, cancellationToken);
            if (response.Content.Headers.ContentLength is { } length && length != updateInfo.TargetDownloadSize)
                throw new InvalidDataException("Update file size mismatch.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    read += count;
                    if (read > updateInfo.TargetDownloadSize) throw new InvalidDataException("Update exceeds its published size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report((read, updateInfo.TargetDownloadSize));
                }
                if (read != updateInfo.TargetDownloadSize) throw new InvalidDataException("Update file size mismatch.");
            }
            await using var verify = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 mismatch. Update rejected.");
            if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var signature = VerifyAuthenticodeSignature(path);
                if ((!signature.IsValid && !signature.IsUnsigned) || signature.IsTampered)
                    throw new InvalidDataException("The update signature is invalid or untrusted.");
            }
            updateInfo.VerifiedSha256 = actual;
            return path;
        }
        catch
        {
            if (updateDir != null) DeleteUpdateWorkspace(updateDir);
            throw;
        }
        finally { DownloadGate.Release(); }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Update metadata is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static void DeleteUpdateWorkspace(string path)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Reepax", "Update"));
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) return;
        for (var current = new DirectoryInfo(full); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return;
        try { Directory.Delete(full, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<string> PreparePortableUpdateAsync(string zipFilePath, CancellationToken cancellationToken = default)
    {
        var extractDir = Path.Combine(Path.GetDirectoryName(zipFilePath)!, "extracted");
        if (Directory.Exists(extractDir)) throw new IOException("The update has already been extracted.");
        Directory.CreateDirectory(extractDir);
        try
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipFilePath);
                long expandedSize = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = entry.FullName.Replace('/', '\\');
                    if (name.EndsWith('\\')) continue;
                    ValidateRelativeUpdatePath(name);
                    if (!names.Add(name)) throw new InvalidDataException("Duplicate archive entry.");
                    expandedSize = checked(expandedSize + entry.Length);
                    if (expandedSize > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Update archive is too large.");
                }
                foreach (var required in RequiredPortableFiles)
                    if (!names.Contains(required)) throw new InvalidDataException("The update archive is incomplete.");
                if (new DriveInfo(Path.GetPathRoot(extractDir)!).AvailableFreeSpace < expandedSize * 2 + 104857600)
                    throw new IOException("Insufficient space to prepare the update.");
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = entry.FullName.Replace('/', '\\');
                    if (name.EndsWith('\\') || IsPreservedPath(name)) continue;
                    string target = Path.Combine(extractDir, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open();
                    using (var output = new FileStream(target, FileMode.CreateNew))
                    {
                        var buffer = new byte[81920];
                        int count;
                        while ((count = input.Read(buffer)) != 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, count);
                        }
                    }
                    using var file = File.OpenRead(target);
                    hashes.Add(name, Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant());
                }
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(extractDir)!, "manifest.json"), JsonSerializer.Serialize(hashes));
            }, cancellationToken);
            return extractDir;
        }
        catch
        {
            Directory.Delete(extractDir, true);
            throw;
        }
    }

    internal static readonly string[] RequiredPortableFiles =
        ["Reepax.exe", "Reepax.dll", "Reepax.deps.json", "Reepax.runtimeconfig.json", "runtimes\\win-x64\\native\\WebView2Loader.dll",
         "runtimes\\win-x64\\native\\reepax_adblock.dll", "runtimes\\win-x64\\native\\par2.exe"];

    internal static bool IsPreservedPath(string name) => name.Split('\\')[0].Equals("Data", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("portable.txt", StringComparison.OrdinalIgnoreCase);

    internal static void ValidateRelativeUpdatePath(string name)
    {
        if (Path.IsPathRooted(name) || name.Split('\\').Any(p => p is ".." or "." or "" ||
            p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Regex.IsMatch(p, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)) ||
            name.Split('\\')[0].Equals(".reepax-update", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe update archive path.");
    }
}
