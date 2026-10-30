using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reepax.Services.Storage;
using Reepax.Services.Update;

namespace Reepax.Tests;

public sealed class UpdateReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UpdateCase_" + Guid.NewGuid().ToString("N"));
    private readonly bool _portable = SettingsService.IsPortableMode;
    private readonly string? _data = SettingsService.AppDataDirectoryOverride;

    public UpdateReliabilityTests()
    {
        Directory.CreateDirectory(_root);
        SettingsService.AppDataDirectoryOverride = Path.Combine(_root, "Data");
        SettingsService.IsPortableMode = false;
    }

    internal static string AssetUrl(string name) => "https://github.com/" +
        string.Join('/', new Uri(AppUpdateService.GitHubApiLatestReleaseUrl).AbsolutePath.Split('/').Skip(2).Take(2)) +
        "/releases/download/v26.10.5/" + name;

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static UpdateInfo Info(byte[] data, bool hash = true)
    {
        var info = new UpdateInfo { NewVersion = "26.10.5", SetupAssetUrl = AssetUrl("setup.exe"), SetupAssetSize = data.Length };
        if (hash) info.SetExpectedSha256("setup.exe", Hash(data));
        return info;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    [Fact]
    public void CurrentVersion_ComesFromBuiltAssembly()
    {
        string version = typeof(AppUpdateService).Assembly.GetCustomAttributes(false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
        Assert.Equal(version, AppUpdateService.AppCurrentVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssetSelection_NeverSubstitutesTheOtherInstallationType(bool portable)
    {
        SettingsService.IsPortableMode = portable;
        var info = portable ? new UpdateInfo { SetupAssetUrl = AssetUrl("setup.exe"), SetupAssetSize = 10 }
            : new UpdateInfo { PortableZipAssetUrl = AssetUrl("portable.zip"), PortableZipAssetSize = 20 };
        Assert.Null(info.TargetDownloadUrl);
        Assert.Equal(0, info.TargetDownloadSize);
        Assert.False(info.HasDirectAsset);
    }

    [Theory]
    [InlineData("26.10.4", "26.10.5", true)]
    [InlineData("26.10.4", "26.10.4.1", true)]
    [InlineData("9.926", "26.10.1", true)]
    [InlineData("26.10.4", "26.10.3", false)]
    public void VersionComparison_PreservesSupportedFormats(string current, string next, bool newer) =>
        Assert.Equal(newer, AppUpdateService.IsNewerVersion(current, next));

    [Fact]
    public void Checksums_RejectConflictingEntries()
    {
        Assert.Throws<InvalidDataException>(() => AppUpdateService.ParseSha256Checksums(
            new string('a', 64) + "  setup.exe\nsetup.exe: " + new string('b', 64)));
    }

    [Fact]
    public async Task Download_RequiresPublishedHashBeforeRequestingPayload()
    {
        int requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var info = Info([1, 2], false);
        info.VerifiedSha256 = "stale";
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateService(client, client).DownloadUpdateAsync(info));
        Assert.Equal(0, requests);
        Assert.Null(info.VerifiedSha256);
    }

    [Theory]
    [InlineData("http://github.com/invalid/setup.exe")]
    [InlineData("https://unrelated.example.test/setup.exe")]
    public async Task Download_RejectsUntrustedInitialDestinations(string url)
    {
        var info = Info([1, 2]);
        info.SetupAssetUrl = url;
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateService().DownloadUpdateAsync(info));
    }

    [Fact]
    public async Task Download_RejectsRedirectBeforeContactingUntrustedHost()
    {
        int requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://unrelated.example.test/payload");
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateService(client, client).DownloadUpdateAsync(Info([1])));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Download_CancellationAndParallelAttemptsDoNotPublishVerifiedState()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }));
        var service = new AppUpdateService(client, client);
        using var cancel = new CancellationTokenSource();
        var info = Info([1]);
        Task<string> first = service.DownloadUpdateAsync(info, cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadUpdateAsync(Info([1])));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Null(info.VerifiedSha256);
    }

    [Fact]
    public async Task Download_ChecksumFetchFailureDoesNotFallBackToUnverifiedPayload()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var info = Info([1], false);
        info.Sha256ChecksumAssetUrl = AssetUrl("SHA256SUMS.txt");
        await Assert.ThrowsAsync<HttpRequestException>(() => new AppUpdateService(client, client).DownloadUpdateAsync(info));
        Assert.Null(info.VerifiedSha256);
    }

    [Fact]
    public async Task Check_FallbackSkipsDraftsAndPrereleases()
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("latest") ? new HttpResponseMessage(HttpStatusCode.NotFound) :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"draft\":true,\"tag_name\":\"v26.12.1\"},{\"prerelease\":true,\"tag_name\":\"v26.11.1\"},{\"tag_name\":\"v26.10.5\",\"assets\":[]}]") })));
        var service = new AppUpdateService(client, client);
        var info = await service.CheckForUpdatesAsync("26.10.4");
        Assert.Equal("26.10.5", info!.NewVersion);
        Assert.Equal(UpdateCheckStatus.Incomplete, service.LastCheckStatus);
    }

    [Fact]
    public async Task Check_NetworkFailureDiffersFromUpToDate()
    {
        using var failed = new HttpClient(new Handler((_, _) => throw new HttpRequestException()));
        var service = new AppUpdateService(failed, failed);
        Assert.Null(await service.CheckForUpdatesAsync());
        Assert.Equal(UpdateCheckStatus.Failed, service.LastCheckStatus);
        using var current = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"tag_name\":\"v26.10.4\"}") })));
        service = new AppUpdateService(current, current);
        Assert.Null(await service.CheckForUpdatesAsync("26.10.4"));
        Assert.Equal(UpdateCheckStatus.UpToDate, service.LastCheckStatus);
    }

    private string CreateArchive(string? extra = null, bool complete = true)
    {
        string path = Path.Combine(_root, "portable.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string name in complete ? AppUpdateService.RequiredPortableFiles : ["Reepax.exe"])
        {
            using var writer = new StreamWriter(archive.CreateEntry(name.Replace('\\', '/')).Open());
            writer.Write("payload");
        }
        if (extra != null)
        {
            using var writer = new StreamWriter(archive.CreateEntry(extra).Open());
            writer.Write("untrusted");
        }
        return path;
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("payload.dll:stream")]
    [InlineData("folder./payload.dll")]
    [InlineData(".reepax-update/journal.json")]
    public async Task Prepare_RejectsUnsafeArchivePaths(string name)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateService().PreparePortableUpdateAsync(CreateArchive(name)));
        Assert.False(Directory.Exists(Path.Combine(_root, "extracted")));
    }

    [Fact]
    public async Task Prepare_RejectsIncompleteArchive() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateService().PreparePortableUpdateAsync(CreateArchive(complete: false)));

    [Fact]
    public async Task Prepare_PreservesUserDataAndBuildsFileManifest()
    {
        string extracted = await new AppUpdateService().PreparePortableUpdateAsync(CreateArchive("Data/settings.json"));
        Assert.False(Directory.Exists(Path.Combine(extracted, "Data")));
        Assert.True(File.Exists(Path.Combine(_root, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(extracted, "Reepax.dll")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Startup_ConfirmsOnlyTheExpectedVersionAndSession(bool matching)
    {
        string directory = Path.Combine(_root, ".reepax-update");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "journal.json"), JsonSerializer.Serialize(new
        {
            Phase = "awaiting", Session = "test-session", Version = matching ? AppUpdateService.AppCurrentVersion : "0.0.0"
        }));
        AppUpdateService.ConfirmUpdateStartup(_root, ["--update-session=test-session"]);
        Assert.Equal(matching, File.Exists(Path.Combine(directory, "startup.ready")));
        if (matching) Assert.False(AppUpdateService.HandleInterruptedUpdate(_root, ["--update-session=test-session"]));
    }

    [Fact]
    public async Task PortableScript_RejectsTamperedStagingBeforeModifyingApplication()
    {
        var (source, target) = CreatePayload();
        File.WriteAllText(Path.Combine(source, "module.dll"), "changed after verification");
        string script = AppUpdateService.GeneratePortableUpdateScript(0, source, target, Path.Combine(target, "Reepax.exe"));
        script = script.Replace("$owned = $false", FakeLaunch(true) + "$owned = $false");
        Assert.Equal(1, await RunScript(script));
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "module.dll")));
        Assert.False(File.Exists(Path.Combine(target, "Reepax.exe")));
    }

    private (string Source, string Target) CreatePayload()
    {
        string source = Path.Combine(_root, "stage's [payload]");
        string target = Path.Combine(_root, "application's [directory]");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Reepax.exe"), Path.Combine(source, "Reepax.exe"));
        File.WriteAllText(Path.Combine(source, "module.dll"), "new");
        File.WriteAllText(Path.Combine(source, "added.dll"), "added");
        File.WriteAllText(Path.Combine(target, "module.dll"), "original");
        Directory.CreateDirectory(Path.Combine(target, "Data"));
        File.WriteAllText(Path.Combine(target, "Data", "settings.json"), "private");
        var hashes = Directory.GetFiles(source).ToDictionary(p => Path.GetFileName(p)!, p => Hash(File.ReadAllBytes(p)));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), JsonSerializer.Serialize(hashes));
        File.WriteAllText(Path.Combine(_root, "helper.approved"), "approved");
        return (source, target);
    }

    private static string FakeLaunch(bool confirm) => "\nfunction Start-Process { param($FilePath, $WorkingDirectory, $ArgumentList, [switch]$PassThru)\n" +
        (confirm ? "if ($state.Phase -eq 'awaiting') { Set-Content -LiteralPath (Join-Path $stateDir 'startup.ready') -Value $state.Session }\n" : "") +
        "return [pscustomobject]@{ HasExited = $true; ExitCode = 0 }\n}\n";

    private async Task<int> RunScript(string script, params string[] args)
    {
        string path = Path.Combine(_root, "run.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(true));
        using var process = AppUpdateService.StartUpdateHelper(path, args)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task UpdateHelper_UsesNativeModulesInsteadOfInheritedOverrides()
    {
        string moduleRoot = Path.Combine(_root, "modules");
        string module = Path.Combine(moduleRoot, "Microsoft.PowerShell.Utility");
        Directory.CreateDirectory(module);
        File.WriteAllText(Path.Combine(module, "Microsoft.PowerShell.Utility.psd1"),
            "@{ RootModule = 'override.psm1'; ModuleVersion = '99.0.0'; FunctionsToExport = @('Get-FileHash') }");
        File.WriteAllText(Path.Combine(module, "override.psm1"),
            "function Get-FileHash { throw 'An inherited module was loaded.' }; Export-ModuleMember -Function Get-FileHash");
        string payload = Path.Combine(_root, "payload.bin");
        File.WriteAllBytes(payload, [1, 2, 3]);
        string result = Path.Combine(_root, "hash.txt");
        string? previous = Environment.GetEnvironmentVariable("PSModulePath");
        try
        {
            Environment.SetEnvironmentVariable("PSModulePath", moduleRoot);
            string script = "$ErrorActionPreference = 'Stop'\n" +
                "$hash = (Get-FileHash -LiteralPath '" + payload.Replace("'", "''") + "' -Algorithm SHA256).Hash\n" +
                "[IO.File]::WriteAllText('" + result.Replace("'", "''") + "', $hash)\n";

            Assert.Equal(0, await RunScript(script));
            Assert.Equal(Hash([1, 2, 3]), File.ReadAllText(result).ToLowerInvariant());
            Assert.Equal(moduleRoot, Environment.GetEnvironmentVariable("PSModulePath"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", previous);
        }
    }

    [Fact]
    public async Task PortableScript_ActuallyCopiesVerifiesAndConfirmsStartup()
    {
        var (source, target) = CreatePayload();
        string script = AppUpdateService.GeneratePortableUpdateScript(0, source, target, Path.Combine(target, "Reepax.exe"));
        script = script.Replace("$owned = $false", FakeLaunch(true) + "$owned = $false");
        Assert.Equal(0, await RunScript(script));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "module.dll")));
        Assert.Equal("private", File.ReadAllText(Path.Combine(target, "Data", "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(target, ".reepax-update")));
    }

    [Fact]
    public async Task PortableScript_LockedFileRollsBackAndKeepsUserData()
    {
        var (source, target) = CreatePayload();
        using var locked = new FileStream(Path.Combine(target, "module.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        string script = AppUpdateService.GeneratePortableUpdateScript(0, source, target, Path.Combine(target, "Reepax.exe"));
        script = script.Replace("$owned = $false", FakeLaunch(true) + "$owned = $false");
        Assert.Equal(1, await RunScript(script));
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "module.dll")));
        Assert.False(File.Exists(Path.Combine(target, "Reepax.exe")));
        Assert.False(File.Exists(Path.Combine(target, "added.dll")));
        Assert.Equal("private", File.ReadAllText(Path.Combine(target, "Data", "settings.json")));
    }

    [Fact]
    public async Task PortableScript_FailedStartupRestoresOriginalFiles()
    {
        var (source, target) = CreatePayload();
        string script = AppUpdateService.GeneratePortableUpdateScript(0, source, target, Path.Combine(target, "Reepax.exe"));
        script = script.Replace("$owned = $false", FakeLaunch(false) + "$owned = $false");
        Assert.Equal(1, await RunScript(script));
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "module.dll")));
        Assert.False(File.Exists(Path.Combine(target, "added.dll")));
    }

    [Fact]
    public async Task PortableScript_InterruptedExchangeCanBeRecoveredFromJournal()
    {
        var (source, target) = CreatePayload();
        string script = AppUpdateService.GeneratePortableUpdateScript(0, source, target, Path.Combine(target, "Reepax.exe"));
        script = script.Replace("$owned = $false", FakeLaunch(false) + "$owned = $false");
        script = script.Replace("Install-File $path $target $file.Hash", "Install-File $path $target $file.Hash\nStop-Process -Id $PID -Force");
        Assert.NotEqual(0, await RunScript(script));
        string recovery = File.ReadAllText(Path.Combine(target, ".reepax-update", "recover.ps1"));
        Assert.Equal(0, await RunScript(recovery, "-Recover", "-RecoveryProcessId", "0"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "module.dll")));
        Assert.False(File.Exists(Path.Combine(target, "Reepax.exe")));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 1)]
    [InlineData(3010, 1)]
    public async Task InstallerScript_RespectsExitCodeDestinationAndSingleRestart(int installerExit, int expectedExit)
    {
        string setup = Path.Combine(_root, "setup.exe");
        File.WriteAllText(setup, "installer");
        File.WriteAllText(Path.Combine(_root, "helper.approved"), "approved");
        string installed = Path.Combine(_root, "Application");
        Directory.CreateDirectory(installed);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Reepax.exe"), Path.Combine(installed, "Reepax.exe"));
        File.Copy(typeof(AppUpdateService).Assembly.Location, Path.Combine(installed, "Reepax.dll"));
        string script = AppUpdateService.GenerateInstallerUpdateScript(0, setup, Path.Combine(installed, "Reepax.exe"), Hash(File.ReadAllBytes(setup)));
        script = script.Replace("$setup = $source", """
            function Get-AuthenticodeSignature { param($LiteralPath) return [pscustomobject]@{ Status = 'NotSigned' } }
            function Start-Process { param($FilePath, $ArgumentList, $WorkingDirectory, [switch]$PassThru, [switch]$Wait)
                Add-Content -LiteralPath (Join-Path $sessionDir 'launches.txt') -Value $ArgumentList
                if ($FilePath -eq $exe) { Set-Content -LiteralPath $readyPath -Value $updateSession }
                return [pscustomobject]@{ ExitCode = INSTALLER_EXIT; HasExited = $true }
            }
            $setup = $source
            """.Replace("INSTALLER_EXIT", installerExit.ToString()));
        Assert.Equal(expectedExit, await RunScript(script));
        string[] launches = File.ReadAllLines(Path.Combine(_root, "launches.txt"));
        Assert.Contains("/NORESTART", launches[0]);
        Assert.Contains("/DIR=\"" + Path.Combine(_root, "Application") + "\"", launches[0]);
        Assert.Equal(installerExit == 0 ? 2 : 1, launches.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallerScript_RejectsWrongVersionOrMissingStartupConfirmation(bool wrongVersion)
    {
        string setup = Path.Combine(_root, "setup.exe");
        File.WriteAllText(setup, "installer");
        File.WriteAllText(Path.Combine(_root, "helper.approved"), "approved");
        string installed = Path.Combine(_root, "Application");
        Directory.CreateDirectory(installed);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Reepax.exe"), Path.Combine(installed, "Reepax.exe"));
        File.Copy(typeof(AppUpdateService).Assembly.Location, Path.Combine(installed, "Reepax.dll"));
        string script = AppUpdateService.GenerateInstallerUpdateScript(0, setup, Path.Combine(installed, "Reepax.exe"),
            Hash(File.ReadAllBytes(setup)), wrongVersion ? "0.0.1" : AppUpdateService.AppCurrentVersion);
        script = script.Replace("$setup = $source", """
            function Get-AuthenticodeSignature { param($LiteralPath) return [pscustomobject]@{ Status = 'NotSigned' } }
            function Start-Process { param($FilePath, $ArgumentList, $WorkingDirectory, [switch]$PassThru, [switch]$Wait)
                return [pscustomobject]@{ ExitCode = 0; HasExited = $true }
            }
            $setup = $source
            """);
        Assert.Equal(1, await RunScript(script));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(SettingsService.AppDataDirectory, "update-result.json")));
        Assert.Equal("failed", result.RootElement.GetProperty("Status").GetString());
        Assert.False(File.Exists(Path.Combine(SettingsService.AppDataDirectory, "update-startup.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallerStartup_RequiresMatchingSessionVersionAndExecutable(bool matching)
    {
        Directory.CreateDirectory(SettingsService.AppDataDirectory);
        string session = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(SettingsService.AppDataDirectory, "update-startup.json"), JsonSerializer.Serialize(new
        {
            Session = session, Version = AppUpdateService.AppCurrentVersion,
            Executable = Path.Combine(_root, matching ? "Reepax.exe" : "different.exe")
        }));
        AppUpdateService.ConfirmUpdateStartup(_root, ["--update-session=" + session]);
        Assert.Equal(matching, File.Exists(Path.Combine(SettingsService.AppDataDirectory, "update-startup.ready")));
    }

    [Fact]
    public void InstallerStartup_ReportsInterruptedHelperWithoutBlockingApplication()
    {
        Directory.CreateDirectory(SettingsService.AppDataDirectory);
        string request = Path.Combine(SettingsService.AppDataDirectory, "update-startup.json");
        File.WriteAllText(request, JsonSerializer.Serialize(new
        {
            Session = Guid.NewGuid().ToString("N"), Version = AppUpdateService.AppCurrentVersion,
            Executable = Path.Combine(_root, "Reepax.exe"), HelperId = int.MaxValue, HelperStart = "0"
        }));
        Assert.False(AppUpdateService.HandleInterruptedUpdate(_root, []));
        Assert.False(File.Exists(request));
        Assert.True(File.Exists(Path.Combine(SettingsService.AppDataDirectory, "update-result.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_ValidatesTheReleaseDigestAgainstItsOwnChecksumFile(bool mismatch)
    {
        byte[] bytes = [1, 2, 3];
        string expected = Hash(bytes);
        string release = JsonSerializer.Serialize(new
        {
            tag_name = "v26.10.5", body = "", assets = new[]
            {
                new { name = "setup.exe", browser_download_url = AssetUrl("setup.exe"), size = bytes.Length, digest = (string?)("sha256:" + expected) },
                new { name = "SHA256SUMS.txt", browser_download_url = AssetUrl("SHA256SUMS.txt"), size = 100, digest = (string?)null }
            }
        });
        int payloadRequests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            HttpContent content;
            if (request.RequestUri!.AbsolutePath.EndsWith("latest")) content = new StringContent(release);
            else if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt"))
                content = new StringContent((mismatch ? new string('0', 64) : expected) + "  setup.exe");
            else { payloadRequests++; content = new ByteArrayContent(bytes); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        var service = new AppUpdateService(client, client);
        var info = await service.CheckForUpdatesAsync("26.10.4");
        Assert.NotNull(info);
        if (mismatch)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadUpdateAsync(info));
            Assert.Equal(0, payloadRequests);
            Assert.Null(info.VerifiedSha256);
        }
        else
        {
            string path = await service.DownloadUpdateAsync(info);
            try { Assert.Equal(expected, info.VerifiedSha256); }
            finally { AppUpdateService.DeleteUpdateWorkspace(Path.GetDirectoryName(path)!); }
        }
    }

    [Fact]
    public async Task PortableScript_RollbackRestoresOtherFilesDespiteOneLockedDestination()
    {
        var (source, target) = CreatePayload();
        string script = AppUpdateService.GeneratePortableUpdateScript(0, source, target, Path.Combine(target, "Reepax.exe"));
        script = script.Replace("$owned = $false", FakeLaunch(false) + "$owned = $false");
        script = script.Replace("$state.Phase = 'awaiting'", """
            $lockedPath = Join-Path $dest 'module.dll'
            $lock = [IO.File]::Open($lockedPath, 'Open', 'Read', 'Read')
            $state.Phase = 'awaiting'
            """);
        Assert.Equal(1, await RunScript(script));
        Assert.False(File.Exists(Path.Combine(target, "Reepax.exe")));
        Assert.False(File.Exists(Path.Combine(target, "added.dll")));
        Assert.True(Directory.Exists(Path.Combine(target, ".reepax-update", "backup")));
        string recovery = File.ReadAllText(Path.Combine(target, ".reepax-update", "recover.ps1"));
        Assert.Equal(0, await RunScript(recovery, "-Recover", "-RecoveryProcessId", "0"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "module.dll")));
    }

    [Fact]
    public async Task PortableScript_WriteFailureDoesNotLeavePartiallyOverwrittenFiles()
    {
        var (source, target) = CreatePayload();
        string script = AppUpdateService.GeneratePortableUpdateScript(0, source, target, Path.Combine(target, "Reepax.exe"));
        script = script.Replace("$owned = $false", FakeLaunch(true) + "$owned = $false");
        script = script.Replace("[IO.File]::Copy($path, $pending, $false)", """
            if ($path.StartsWith($source) -and $path.EndsWith('module.dll')) {
                [IO.File]::WriteAllText($pending, 'incomplete')
                throw [IO.IOException]::new('Simulated storage write failure.')
            }
            [IO.File]::Copy($path, $pending, $false)
            """);
        Assert.Equal(1, await RunScript(script));
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "module.dll")));
        Assert.False(File.Exists(Path.Combine(target, "Reepax.exe")));
        Assert.False(File.Exists(Path.Combine(target, ".reepax-update", "pending.bin")));
    }

    [Fact]
    public async Task PortableScript_ReadOnlyDestinationPreservesTheOriginalPayload()
    {
        var (source, target) = CreatePayload();
        string protectedFile = Path.Combine(target, "module.dll");
        File.SetAttributes(protectedFile, FileAttributes.ReadOnly);
        try
        {
            string script = AppUpdateService.GeneratePortableUpdateScript(0, source, target, Path.Combine(target, "Reepax.exe"));
            script = script.Replace("$owned = $false", FakeLaunch(true) + "$owned = $false");
            Assert.Equal(1, await RunScript(script));
            Assert.Equal("original", File.ReadAllText(protectedFile));
            Assert.False(File.Exists(Path.Combine(target, "added.dll")));
        }
        finally { File.SetAttributes(protectedFile, FileAttributes.Normal); }
    }

    [Fact]
    public async Task WorkspaceCleanup_RemovesOnlyTheOwnedUpdateSession()
    {
        string owned = Path.Combine(Path.GetTempPath(), "Reepax", "Update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "partial.bin"), "partial");
        try
        {
            string script = AppUpdateService.GenerateInstallerUpdateScript(0, "unused", "unused");
            script = script[..script.IndexOf("$expectedHash =", StringComparison.Ordinal)];
            script += "\nRemove-CompletedWorkspace '" + owned.Replace("'", "''") + "'\n";
            script += "Remove-CompletedWorkspace '" + _root.Replace("'", "''") + "'\n";
            Assert.Equal(0, await RunScript(script));
            Assert.False(Directory.Exists(owned));
            Assert.True(Directory.Exists(_root));
        }
        finally { AppUpdateService.DeleteUpdateWorkspace(owned); }
    }

    public void Dispose()
    {
        SettingsService.IsPortableMode = _portable;
        SettingsService.AppDataDirectoryOverride = _data;
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(_root, true);
    }
}
