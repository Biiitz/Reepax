using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Reepax.Tests;

public sealed class ConcurrentLoadTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(12, 3)]
    [InlineData(36, 1)]
    public async Task MixedWorkload_PreservesExactResultsAcrossPausesMovesAndExtraction(int packages, int rounds)
    {
        string root = Directory.CreateTempSubdirectory("ConcurrentLoad_").FullName;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { typeof(ConcurrentLoadTests).Assembly.Location, "--load-worker", root,
            packages.ToString(System.Globalization.CultureInfo.InvariantCulture), rounds.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        Process? process = null;
        Task<string>? stdout = null, stderr = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Load worker did not start.");
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            string result = await stdout;
            Assert.True(process.ExitCode == 0, "Load worker failed: " + await stderr + result);
            var report = JsonSerializer.Deserialize<ConcurrentLoadReport>(result.Trim());
            Assert.NotNull(report);
            Assert.Equal(packages, report.Packages);
            Assert.Equal(packages * 3, report.CompletedDownloads);
            Assert.Equal(packages, report.ExtractedPackages);
            Assert.Equal(packages, report.CompletionNotifications);
            Assert.Equal(rounds * 2, report.MovedPackages);
            Assert.Equal(rounds * 2, report.ResumedRanges);
            Assert.InRange(report.MaximumActiveDownloads, 4, 6);
            Assert.True(report.UiTicks > 0);
            Assert.InRange(report.MaximumRealizedRows, 1, 128);
            Assert.True(report.VerifiedBytes > 0);
            output.WriteLine(result.Trim());
            string diagnostics = await stderr;
            if (!string.IsNullOrWhiteSpace(diagnostics)) output.WriteLine(diagnostics);
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    if (stdout != null && stderr != null) await Task.WhenAll(stdout, stderr);
                }
                finally { process.Dispose(); }
            }
            string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("ConcurrentLoad_", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected load test directory.");
            Directory.Delete(root, recursive: true);
        }
    }
}
