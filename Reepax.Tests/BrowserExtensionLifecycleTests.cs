using System.IO;
using System.IO.Compression;
using Reepax.Services.Browser;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public sealed class BrowserExtensionLifecycleTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ReepaxExtension_");
    private readonly string? _previousAppData = SettingsService.AppDataDirectoryOverride;

    public BrowserExtensionLifecycleTests()
    {
        SettingsService.AppDataDirectoryOverride = _root.FullName;
        Directory.CreateDirectory(BrowserExtensionService.ExtensionsDirectory);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FailedInstallation_LeavesUnrelatedProfileExtensionAlone(bool similarName, bool staleId)
    {
        var host = new BrowserScriptTestHost(enableExtensions: true);
        try
        {
            await host.InitializeAsync();
            await host.OnUi(async () =>
            {
                var profile = host.Core.Profile;
                var unrelated = await profile.AddBrowserExtensionAsync(CreateFolder(_root.FullName, "existing", "Example Tool"));
                await unrelated.EnableAsync(false);
                var existingIds = (await profile.GetBrowserExtensionsAsync()).Select(ext => ext.Id).Order().ToArray();
                CreateFolder(BrowserExtensionService.ExtensionsDirectory, "invalid", similarName ? "Example Tool Plus" : "Other Tool", "invalid");
                if (staleId)
                    File.WriteAllText(Path.Combine(SettingsService.AppDataDirectory, "extensions.json"),
                        $$"""{"invalid":{"isEnabled":true,"extensionId":"{{unrelated.Id}}"} }""");
                var service = new BrowserExtensionService();

                await service.InstallIntoProfileAsync(profile);
                var failed = Assert.Single(service.Extensions);
                Assert.False(string.IsNullOrWhiteSpace(failed.InstallError));
                Assert.Null(failed.NativeExtension);
                Assert.Null(failed.InstalledExtensionId);
                Assert.Null(BrowserExtensionService.LoadSavedStates().GetValueOrDefault("invalid")?.ExtensionId);
                await service.SetEnabledAsync(failed, false);
                await service.RemoveAsync(failed);

                var retainedExtensions = await profile.GetBrowserExtensionsAsync();
                Assert.Equal(existingIds, retainedExtensions.Select(ext => ext.Id).Order());
                var retained = Assert.Single(retainedExtensions, ext => ext.Id == unrelated.Id);
                Assert.False(retained.IsEnabled);
            });
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ManagedInstallation_IsRecognizedFromItsProfileAfterServiceRestart()
    {
        var host = new BrowserScriptTestHost(enableExtensions: true);
        try
        {
            await host.InitializeAsync();
            await host.OnUi(async () =>
            {
                var profile = host.Core.Profile;
                CreateFolder(BrowserExtensionService.ExtensionsDirectory, "managed", "Managed Tool");
                var service = new BrowserExtensionService();
                await service.InstallIntoProfileAsync(profile);
                var original = Assert.Single(service.Extensions);
                string id = Assert.IsType<string>(original.InstalledExtensionId);
                await service.SetEnabledAsync(original, false);

                await host.ReopenControllerAsync();
                service = new BrowserExtensionService();
                await service.InstallIntoProfileAsync(host.Core.Profile);
                var rediscovered = Assert.Single(service.Extensions);
                Assert.Equal(id, rediscovered.InstalledExtensionId);
                Assert.False(rediscovered.IsEnabled);
                Assert.False(Assert.IsType<Microsoft.Web.WebView2.Core.CoreWebView2BrowserExtension>(rediscovered.NativeExtension).IsEnabled);
                await service.SetEnabledAsync(rediscovered, true);
                Assert.True(Assert.Single(await host.Core.Profile.GetBrowserExtensionsAsync(), ext => ext.Id == id).IsEnabled);

                var states = BrowserExtensionService.LoadSavedStates();
                states["managed"].RemovalPending = true;
                Assert.True(BrowserExtensionService.SaveSavedStates(states));
                service = new BrowserExtensionService();
                await service.InstallIntoProfileAsync(host.Core.Profile);
                Assert.Empty(service.Extensions);
                Assert.DoesNotContain(await host.Core.Profile.GetBrowserExtensionsAsync(), ext => ext.Id == id);
                Assert.False(Directory.Exists(Path.Combine(BrowserExtensionService.ExtensionsDirectory, "managed")));
            });
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(".zip")]
    [InlineData(".crx")]
    [InlineData(".crx2")]
    public async Task RemovedArchive_StaysRemovedAcrossScansAndCanBeReplaced(string extension)
    {
        var host = new BrowserScriptTestHost(enableExtensions: true);
        try
        {
            await host.InitializeAsync();
            string archive = Path.Combine(BrowserExtensionService.ExtensionsDirectory, "archived" + (extension == ".zip" ? ".zip" : ".crx"));
            int crxVersion = extension == ".crx2" ? 2 : 3;
            WriteArchive(archive, "1.0", crxVersion: crxVersion);
            byte[] original = File.ReadAllBytes(archive);
            CreateFolder(BrowserExtensionService.ExtensionsDirectory, "unrelated", "Another Tool");

            await host.OnUi(async () =>
            {
                var profile = host.Core.Profile;
                var service = new BrowserExtensionService();
                await service.InstallIntoProfileAsync(profile);
                var removed = Assert.Single(service.Extensions, ext => ext.FolderName == "archived");
                string id = Assert.IsType<string>(removed.InstalledExtensionId);
                var other = Assert.Single(service.Extensions, ext => ext.FolderName == "unrelated");
                string otherId = Assert.IsType<string>(other.InstalledExtensionId);
                await service.SetEnabledAsync(removed, false);
                await service.RemoveAsync(removed);
                Assert.Equal(original, File.ReadAllBytes(archive));

                service = new BrowserExtensionService();
                await service.InstallIntoProfileAsync(profile);
                Assert.DoesNotContain(service.Extensions, ext => ext.FolderName == "archived");
                Assert.DoesNotContain(await profile.GetBrowserExtensionsAsync(), ext => ext.Id == id);
                Assert.Equal(otherId, Assert.Single(service.Extensions).InstalledExtensionId);

                WriteArchive(archive, "2.0", crxVersion: crxVersion);
                await service.InstallIntoProfileAsync(profile);
                var replacement = Assert.Single(service.Extensions, ext => ext.FolderName == "archived");
                Assert.Equal("2.0", replacement.Version);
                Assert.True(replacement.IsEnabled);
                Assert.NotNull(replacement.NativeExtension);

                await service.RemoveAsync(replacement);
                File.Move(archive, Path.Combine(BrowserExtensionService.ExtensionsDirectory, "reinstalled" + Path.GetExtension(archive)));
                await service.InstallIntoProfileAsync(profile);
                Assert.Equal("2.0", Assert.Single(service.Extensions, ext => ext.FolderName == "reinstalled").Version);
            });
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public void PartialArchiveImport_CanRetryWithoutReplacingManualDirectory()
    {
        string directory = BrowserExtensionService.ExtensionsDirectory;
        string archive = Path.Combine(directory, "archived.zip");
        WriteArchive(archive, "1.0", partial: true);
        string manual = CreateFolder(directory, "manual", "Manual Tool");
        WriteArchive(Path.Combine(directory, "manual.zip"), "2.0");

        var service = new BrowserExtensionService();
        Assert.Single(service.Extensions, ext => ext.FolderName == "manual");
        Assert.False(Directory.Exists(Path.Combine(directory, "archived")));
        WriteArchive(archive, "2.0");

        service = new BrowserExtensionService();
        Assert.Equal("2.0", Assert.Single(service.Extensions, ext => ext.FolderName == "archived").Version);
        Assert.Equal("1.0", Assert.Single(service.Extensions, ext => ext.FolderName == "manual").Version);
        Assert.Equal(Manifest("Manual Tool", "1.0"), File.ReadAllText(Path.Combine(manual, "manifest.json")));
    }

    private static string CreateFolder(string parent, string folder, string name, string version = "1.0")
    {
        string path = Directory.CreateDirectory(Path.Combine(parent, folder)).FullName;
        File.WriteAllText(Path.Combine(path, "manifest.json"), Manifest(name, version));
        return path;
    }

    private static string Manifest(string name, string version)
        => $$"""{"manifest_version":3,"name":"{{name}}","version":"{{version}}","action":{"default_popup":"popup.html"} }""";

    private static void WriteArchive(string path, string version, bool partial = false, int crxVersion = 3)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("popup.html").Open()))
                writer.Write("<p>Example</p>");
            using (var writer = new StreamWriter(archive.CreateEntry(partial ? "popup.html/manifest.json" : "manifest.json").Open()))
                writer.Write(Manifest("Archived Tool", version));
        }
        using var file = File.Create(path);
        if (path.EndsWith(".crx", StringComparison.Ordinal))
        {
            if (crxVersion == 2) file.Write("Cr24\x02\0\0\0\x01\0\0\0\x01\0\0\0\x12\x34"u8);
            else file.Write("Cr24\x03\0\0\0\x02\0\0\0\x12\x34"u8);
        }
        stream.WriteTo(file);
    }

    public void Dispose()
    {
        SettingsService.AppDataDirectoryOverride = _previousAppData;
        _root.Delete(recursive: true);
    }
}
