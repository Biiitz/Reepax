using System.Diagnostics;
using System.IO;
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
    private readonly string? _data = SettingsService.AppDataDirectoryOverride;

    public UpdateReliabilityTests()
    {
        Directory.CreateDirectory(_root);
        SettingsService.AppDataDirectoryOverride = Path.Combine(_root, "Data");
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
    public async Task Check_SelectsOnlyTheInstallerAndRequiresItsChecksumFile(bool includeInstaller)
    {
        var assets = new List<object>
        {
            new { name = "archive.zip", browser_download_url = AssetUrl("archive.zip"), size = 20, digest = "unsupported" },
            new { name = "SHA256SUMS.txt", browser_download_url = AssetUrl("SHA256SUMS.txt"), size = 100, digest = (string?)null }
        };
        if (includeInstaller)
            assets.Add(new { name = "setup.exe", browser_download_url = AssetUrl("setup.exe"), size = 10, digest = "sha256:" + new string('a', 64) });
        string release = JsonSerializer.Serialize(new { tag_name = "v26.10.5", assets });
        using var client = new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release) })));
        var service = new AppUpdateService(client, client);

        var info = await service.CheckForUpdatesAsync("26.10.4");

        Assert.NotNull(info);
        Assert.Equal("setup.exe", info.TargetFileName);
        Assert.Equal(includeInstaller, info.HasDirectAsset);
        Assert.Equal(includeInstaller ? AssetUrl("setup.exe") : null, info.TargetDownloadUrl);
        Assert.Equal(includeInstaller ? 10 : 0, info.TargetDownloadSize);
        Assert.Equal(includeInstaller ? UpdateCheckStatus.Available : UpdateCheckStatus.Incomplete, service.LastCheckStatus);
        Assert.Equal(includeInstaller ? 1 : 0, info.ExpectedSha256Hashes.Count);
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
                return [pscustomobject]@{ ExitCode = INSTALLER_EXIT; HasExited = $true } |
                    Add-Member ScriptMethod WaitForExit {} -PassThru | Add-Member ScriptMethod Dispose {} -PassThru
            }
            $setup = $source
            """.Replace("INSTALLER_EXIT", installerExit.ToString()));
        Assert.Equal(expectedExit, await RunScript(script));
        string[] launches = File.ReadAllLines(Path.Combine(_root, "launches.txt"));
        Assert.Contains("/NORESTART", launches[0]);
        Assert.Contains("/DIR=\"" + Path.Combine(_root, "Application") + "\"", launches[0]);
        Assert.Equal(installerExit == 0 ? 2 : 1, launches.Length);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(SettingsService.AppDataDirectory, "update-result.json")));
        Assert.Equal(installerExit, result.RootElement.GetProperty("InstallerExitCode").GetInt32());
        Assert.Equal(installerExit == 0 ? "cleanup" : "installation", result.RootElement.GetProperty("Stage").GetString());
    }

    [Fact]
    public async Task InstallerScript_CompletesWhileTheInstallersStartedApplicationRemainsRunning()
    {
        string eventId = Guid.NewGuid().ToString("N");
        string readyName = "Local\\UpdateChildReady_" + eventId;
        string stopName = "Local\\UpdateChildStop_" + eventId;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        File.WriteAllText(Path.Combine(_root, "child.ps1"), $$"""
            $ready = [Threading.EventWaitHandle]::OpenExisting('{{readyName}}')
            $stop = [Threading.EventWaitHandle]::OpenExisting('{{stopName}}')
            try {
                $PID | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'child.pid')
                $null = $ready.Set(); $null = $stop.WaitOne()
            }
            finally { $ready.Dispose(); $stop.Dispose() }
            """);
        File.WriteAllText(Path.Combine(_root, "installer.ps1"), $$"""
            $child = Start-Process -FilePath '{{shell.Replace("'", "''")}}' -ArgumentList "-NoProfile -NonInteractive -File `"$PSScriptRoot\child.ps1`"" -PassThru -WindowStyle Hidden
            $ready = [Threading.EventWaitHandle]::OpenExisting('{{readyName}}')
            try { if (-not $ready.WaitOne(15000)) { exit 2 } }
            finally { $ready.Dispose() }
            exit 0
            """);
        string setup = Path.Combine(_root, "setup.exe");
        File.WriteAllText(setup, "installer fixture");
        File.WriteAllText(Path.Combine(_root, "helper.approved"), "approved");
        string installed = Directory.CreateDirectory(Path.Combine(_root, "Application")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Reepax.exe"), Path.Combine(installed, "Reepax.exe"));
        File.Copy(typeof(AppUpdateService).Assembly.Location, Path.Combine(installed, "Reepax.dll"));
        string script = AppUpdateService.GenerateInstallerUpdateScript(0, setup, Path.Combine(installed, "Reepax.exe"), Hash(File.ReadAllBytes(setup)));
        script = script.Replace("$setup = $source", $$"""
            function Get-AuthenticodeSignature { param($LiteralPath) return [pscustomobject]@{ Status = 'NotSigned' } }
            function Start-Process { param($FilePath, $ArgumentList, $WorkingDirectory, [switch]$PassThru, [switch]$Wait)
                if ($FilePath -eq $source) {
                    return Microsoft.PowerShell.Management\Start-Process -FilePath '{{shell.Replace("'", "''")}}' -ArgumentList "-NoProfile -NonInteractive -File `"$sessionDir\installer.ps1`"" -PassThru -Wait:$Wait -WindowStyle Hidden
                }
                Set-Content -LiteralPath $readyPath -Value $updateSession
                return [pscustomobject]@{ HasExited = $true }
            }
            $setup = $source
            """);
        Process? child = null;
        Task<int> running = RunScript(script);
        try
        {
            Assert.True(ready.WaitOne(TimeSpan.FromSeconds(15)), "The installer's application did not start.");
            child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(_root, "child.pid"))));
            _ = child.SafeHandle;
            Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.False(child.HasExited);
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(SettingsService.AppDataDirectory, "update-result.json")));
            Assert.Equal("success", result.RootElement.GetProperty("Status").GetString());
            Assert.False(File.Exists(setup));
        }
        finally
        {
            stop.Set();
            try { await running; }
            finally
            {
                if (child != null)
                {
                    try
                    {
                        if (!child.WaitForExit(10000)) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                    }
                    finally { child.Dispose(); }
                }
            }
        }
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
                return [pscustomobject]@{ ExitCode = 0; HasExited = $true } |
                    Add-Member ScriptMethod WaitForExit {} -PassThru | Add-Member ScriptMethod Dispose {} -PassThru
            }
            $setup = $source
            """);
        Assert.Equal(1, await RunScript(script));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(SettingsService.AppDataDirectory, "update-result.json")));
        Assert.Equal("failed", result.RootElement.GetProperty("Status").GetString());
        Assert.Equal(wrongVersion ? "installed-files" : "startup-confirmation", result.RootElement.GetProperty("Stage").GetString());
        Assert.False(result.RootElement.TryGetProperty("Message", out _));
        Assert.DoesNotContain(_root, result.RootElement.GetRawText());
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
    [InlineData("valid", true)]
    [InlineData("helper", false)]
    [InlineData("version", false)]
    [InlineData("path", false)]
    [InlineData("manifest", false)]
    [InlineData("argument", false)]
    public void InstallerTransition_RequiresManagedLayoutAndTheLiveLegacyHelper(string scenario, bool expected)
    {
        string current = Directory.CreateDirectory(Path.Combine(_root, "current")).FullName;
        File.WriteAllText(Path.Combine(_root, "Update.exe"), "updater");
        if (scenario != "manifest") File.WriteAllText(Path.Combine(current, "sq.version"), "manifest");
        Directory.CreateDirectory(SettingsService.AppDataDirectory);
        using var process = Process.GetCurrentProcess();
        string session = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(SettingsService.AppDataDirectory, "update-startup.json"), JsonSerializer.Serialize(new
        {
            Session = session, Version = scenario == "version" ? "0.0.1" : AppUpdateService.AppCurrentVersion,
            Executable = Path.Combine(_root, scenario == "path" ? "other.exe" : "Reepax.exe"),
            HelperId = scenario == "helper" ? int.MaxValue : process.Id,
            HelperStart = process.StartTime.ToUniversalTime().Ticks.ToString()
        }));

        AppUpdateService.ConfirmUpdateStartup(current, scenario == "argument" ? new[] { "--restart" } : new[] { "--legacy-update-bootstrap" });

        string ready = Path.Combine(SettingsService.AppDataDirectory, "update-startup.ready");
        Assert.Equal(expected, File.Exists(ready));
        if (expected) Assert.Equal(session, File.ReadAllText(ready));
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
        SettingsService.AppDataDirectoryOverride = _data;
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(_root, true);
    }
}
