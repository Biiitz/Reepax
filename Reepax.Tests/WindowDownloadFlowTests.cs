using System.Diagnostics;
using System.IO;
using Xunit;

namespace Reepax.Tests;

public sealed class WindowDownloadFlowTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task MainWindow_AddStartBrowserDownloadAndExtract_PreservesOriginalBytes(string language)
    {
        string root = Directory.CreateTempSubdirectory("WindowFlow_").FullName;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string value in new[] { typeof(WindowDownloadFlowTests).Assembly.Location, "--window-flow-worker", root, language })
            start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Window worker did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100));
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Contains("Window download flow passed", await output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(output, error);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(root));
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, recursive: true); break; }
                catch (IOException) when (attempt < 40) { await Task.Delay(100); }
            }
        }
    }
}
