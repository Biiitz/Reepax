using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

[Trait("Category", "Integration")]
public sealed class WindowDownloadFlowTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task MainWindow_AddStartBrowserDownloadAndExtract_PreservesOriginalBytes(string language)
        => await RunWindowWorkerAsync(language, "--window-flow-worker", "Window download flow passed");

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task MainWindow_ColumnMenuSwitchesFromHeaderToGearWithoutMovingOutsideWindow(string language)
        => await RunWindowWorkerAsync(language, "--column-menu-worker", "Column menu placement passed");

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task MainWindow_ClipboardRetriesRejectedLinksAndAppendsOnlyNewLinks(string language)
        => await RunWindowWorkerAsync(language, "--clipboard-window-worker", "Clipboard handoff passed");

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task MainWindow_RenameAndDeleteFollowActiveKeyboardShortcuts(string language)
        => await RunWindowWorkerAsync(language, "--shortcut-window-worker", "Window shortcuts passed");

    [Theory]
    [InlineData("keep", ".repx")]
    [InlineData("remove", ".sdlr")]
    [InlineData("move", ".repx")]
    public async Task MainWindow_RestartDoesNotReplayPackageOpenedAtStartup(string scenario, string extension)
    {
        var root = Directory.CreateTempSubdirectory("WindowFlow_");
        try
        {
            string source = Path.Combine(root.FullName, "example package" + extension);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ReepaxPackageDto
            {
                Format = extension[1..], Name = "Example package", AutoResolveHostLinks = false,
                Items = [new() { FileName = "example.bin", OriginalUrl = "https://example.test/example.bin", TotalBytes = 16 }]
            });
            File.WriteAllBytes(source, bytes);
            await RunWindowWorkerAsync(root.FullName, "en", "--package-restart-window-worker", "Package state saved", scenario, source);
            string[] restartArgs = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(Path.Combine(root.FullName, "restart.json")))!;
            await RunWindowWorkerAsync(root.FullName, "en", "--package-restart-window-worker", "Restarted package state verified", ["verify-" + scenario, ..restartArgs]);
            Assert.Equal(bytes, File.ReadAllBytes(source));
        }
        finally { await DeleteWorkerDirectoryAsync(root.FullName); }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task Browser_FilterListButtonUpdatesRealRequestsAndPreservesRulesOnFailure(string language)
        => await RunWindowWorkerAsync(language, "--filter-update-worker",
            Environment.GetEnvironmentVariable("REEPAX_LIVE_FILTER_CHECK") == "1" && language == "en"
                ? "Live HTTPS filter update and unchanged-list detection passed"
                : "Browser filter updates passed");

    private static async Task RunWindowWorkerAsync(string language, string worker, string success)
    {
        string root = Directory.CreateTempSubdirectory("WindowFlow_").FullName;
        try { await RunWindowWorkerAsync(root, language, worker, success); }
        finally { await DeleteWorkerDirectoryAsync(root); }
    }

    private static async Task RunWindowWorkerAsync(string root, string language, string worker, string success, params string[] extraArguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string value in new[] { typeof(WindowDownloadFlowTests).Assembly.Location, worker, root, language })
            start.ArgumentList.Add(value);
        foreach (string value in extraArguments)
            start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Window worker did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100));
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains(success, await output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(output, error);
        }
    }

    private static async Task DeleteWorkerDirectoryAsync(string root)
    {
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(root));
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(root, recursive: true); break; }
            catch (IOException) when (attempt < 40) { await Task.Delay(100); }
        }
    }
}
