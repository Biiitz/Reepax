using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reepax.Services.Update;
using Velopack.Locators;

namespace Reepax.Tests;

[Trait("Category", "Integration")]
public sealed class VelopackProcessTests
{
    [Theory]
    [InlineData("26.11.2")]
    [InlineData("26.11.1.1")]
    public async Task Update_UsesRealDeltaFullFallbackAndNativeApplyWithoutChangingUserData(string targetVersion)
    {
        string root = Directory.CreateTempSubdirectory("NativeUpdate_").FullName;
        string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string project = Path.Combine(projectRoot, "Reepax.Tests", "UpdateProbe", "UpdateProbe.csproj");
        string releases = Path.Combine(root, "releases");
        string payload = Path.Combine(root, "payload");
        var processes = new List<Process>();
        try
        {
            await Run("dotnet", projectRoot, "tool", "restore");
            byte[] expected = Enumerable.Range(0, 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
            foreach (string version in new[] { "26.11.1", targetVersion })
            {
                await Run("dotnet", projectRoot, "publish", project, "-c", "Release", "-r", "win-x64", "--self-contained", "false",
                    "--artifacts-path", Path.Combine(root, "build"), "-o", payload, "-p:Version=" + version, "--verbosity", "quiet");
                if (version == targetVersion) expected[512] ^= 1;
                File.WriteAllBytes(Path.Combine(payload, "data.bin"), expected);
                var files = Directory.GetFiles(payload, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(payload, file).Replace('\\', '/')).ToArray();
                File.WriteAllLines(Path.Combine(payload, "update-files.txt"), files);
                await Run("dotnet", projectRoot, "tool", "run", "vpk", "pack", "--packId", AppUpdateService.PackageId,
                    "--packTitle", "Reepax", "--packVersion", AppUpdateService.GetPackageVersion(version), "--packDir", payload, "--mainExe", "Reepax.exe",
                    "--runtime", "win-x64", "--outputDir", releases);
                if (version == "26.11.1")
                    File.Copy(Path.Combine(releases, AppUpdateService.PackageId + "-win-Portable.zip"), Path.Combine(root, "base.zip"));
            }
            var feed = JsonDocument.Parse(File.ReadAllText(Path.Combine(releases, AppUpdateService.PackageFeedName)));
            using (feed)
            {
                var targetAssets = feed.RootElement.GetProperty("Assets").EnumerateArray()
                    .Where(asset => asset.GetProperty("Version").GetString() == AppUpdateService.GetPackageVersion(targetVersion)).ToArray();
                Assert.Contains(targetAssets, asset => asset.GetProperty("Type").GetString() == "Delta");
                foreach (string scenario in new[] { "delta", "fallback", "full" })
                {
                    string installed = Path.Combine(root, scenario);
                    ZipFile.ExtractToDirectory(Path.Combine(root, "base.zip"), installed);
                    string cache = Directory.CreateDirectory(Path.Combine(installed, "packages")).FullName;
                    if (scenario != "full")
                        File.Copy(Path.Combine(releases, AppUpdateService.PackageId + "-26.11.1-full.nupkg"),
                            Path.Combine(cache, AppUpdateService.PackageId + "-26.11.1-full.nupkg"));
                    string userData = Path.Combine(installed, "user-data.bin");
                    File.WriteAllBytes(userData, new byte[] { 10, 0, 255 });
                    string foreign = Path.Combine(cache, "unrelated.nupkg");
                    File.WriteAllText(foreign, "preserve");
                    string report = Path.Combine(installed, "report.json");
                    var files = targetAssets.ToDictionary(asset => asset.GetProperty("FileName").GetString()!,
                        asset => File.ReadAllBytes(Path.Combine(releases, asset.GetProperty("FileName").GetString()!)));
                    files[AppUpdateService.PackageFeedName] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Assets = targetAssets }));
                    using (var zip = ZipFile.OpenRead(Path.Combine(releases, files.Keys.Single(name => name.EndsWith("-full.nupkg")))))
                    {
                        var contents = zip.Entries.ToDictionary(entry => entry.FullName, entry =>
                        {
                            using var input = entry.Open();
                            return new PackageFileDigest(entry.Length, Convert.ToHexString(SHA256.HashData(input)));
                        });
                        files[AppUpdateService.ContentManifestName(targetVersion)] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(contents));
                    }
                    string AssetUrl(string name) => UpdateReliabilityTests.AssetUrl(name).Replace("v26.10.5", "v" + targetVersion);
                    files["release"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        tag_name = "v" + targetVersion, assets = files.Select(file => new { name = file.Key, size = file.Value.Length,
                            browser_download_url = AssetUrl(file.Key), digest = "sha256:" + Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant() }).ToArray()
                    }));
                    if (scenario == "fallback") files.Single(file => file.Key.EndsWith("-delta.nupkg")).Value[^1] ^= 1;
                    await using var server = new PipelineHttpSource(files);
                    server.ReleaseArchive();
                    using var client = new HttpClient(new LocalReleaseHandler(server));
                    // The native fixture marker prevents registration in the real Windows account.
                    var locator = new NativeLocator(installed, cache, report, processes);
                    int shutdowns = 0;
                    var service = new AppUpdateService(client, client, locator) { ShutdownAfterPackageUpdate = () => shutdowns++ };
                    var info = await service.CheckForUpdatesAsync("26.11.1");
                    Assert.NotNull(info);
                    string package = await service.DownloadUpdateAsync(info!);
                    string fullName = files.Keys.Single(name => name.EndsWith("-full.nupkg"));
                    if (scenario != "delta") Assert.Equal(files[fullName], File.ReadAllBytes(package));
                    Assert.NotNull(info!.VerifiedSha256);
                    var paths = server.Requests.Keys.ToArray();
                    Assert.Equal(scenario != "full", paths.Any(path => path.EndsWith("-delta.nupkg")));
                    Assert.Equal(scenario != "delta", paths.Any(path => path.EndsWith("-full.nupkg")));
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var watcher = new FileSystemWatcher(installed, "report.json") { EnableRaisingEvents = true };
                    watcher.Created += (_, _) => ready.TrySetResult();
                    watcher.Renamed += (_, _) => ready.TrySetResult();
                    Assert.True(service.ApplyDownloadedUpdate(info!, package));
                    Assert.Equal(1, shutdowns);
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(45));
                    using var startup = JsonDocument.Parse(File.ReadAllText(report));
                    Assert.Equal(targetVersion, startup.RootElement.GetProperty("Version").GetString());
                    Assert.Contains(startup.RootElement.GetProperty("Arguments").EnumerateArray(), argument => argument.GetString() == "--restart");
                    foreach (string file in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(payload, file);
                        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(installed, "current", relative)));
                    }
                    Assert.Equal(expected, File.ReadAllBytes(Path.Combine(installed, "current", "data.bin")));
                    Assert.Equal(new byte[] { 10, 0, 255 }, File.ReadAllBytes(userData));
                    Assert.Equal("preserve", File.ReadAllText(foreign));
                    foreach (var process in processes) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task Run(string executable, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    private sealed class LocalReleaseHandler(PipelineHttpSource server) : HttpMessageHandler
    {
        private readonly HttpClient _local = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string name = request.RequestUri!.AbsolutePath.EndsWith("latest") ? "release" : Uri.UnescapeDataString(request.RequestUri.Segments[^1]);
            var response = await _local.GetAsync(server.Url(name), HttpCompletionOption.ResponseHeadersRead, token);
            response.RequestMessage = request;
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) _local.Dispose(); base.Dispose(disposing); }
    }

    private sealed class NativeLocator(string root, string cache, string report, List<Process> processes)
        : TestVelopackLocator(AppUpdateService.PackageId, "26.11.1", cache, Path.Combine(root, "current"), root,
            Path.Combine(root, "Update.exe"), "win")
    {
        private string ReportPath { get; } = report;
        private List<Process> StartedProcesses { get; } = processes;
        public override IProcessImpl Process => new NativeProcess(this);
        private sealed class NativeProcess(NativeLocator owner) : IProcessImpl
        {
            public uint GetCurrentProcessId() => 0;
            public string GetCurrentProcessPath() => Path.Combine(owner.AppContentDir!, "Reepax.exe");
            public void Exit(int exitCode) => throw new InvalidOperationException("Unexpected immediate process exit.");
            public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
            {
                var start = new ProcessStartInfo(exePath) { WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in args) start.ArgumentList.Add(argument);
                start.Environment["REEPAX_UPDATE_PROBE_REPORT"] = owner.ReportPath;
                owner.StartedProcesses.Add(System.Diagnostics.Process.Start(start)!);
            }
        }
    }
}
