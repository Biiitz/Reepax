using System.IO;
using System.Text.Json;
using System.Windows;
using Reepax.Services.Storage;

namespace Reepax.Tests;

internal sealed record ConcurrentLoadReport(int Packages, int CompletedDownloads, int ExtractedPackages,
    int CompletionNotifications, int MovedPackages, int ResumedRanges, int MaximumActiveDownloads,
    int UiTicks, int MaximumRealizedRows, double MaximumUiGapMs, double ElapsedMs, double CpuMs,
    long PeakWorkingSet, long VerifiedBytes, Dictionary<string, double> MaximumOperationMs);

internal static class ConcurrentLoadWorker
{
    internal static int Run(string root, int packages, int rounds)
    {
        int exit = 1;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    root = Path.GetFullPath(root);
                    string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                    if (!Directory.Exists(root) || !Path.GetFileName(root).StartsWith("ConcurrentLoad_", StringComparison.Ordinal) ||
                        !string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase) ||
                        packages is < 12 or > 36 || rounds is < 1 or > 3)
                        throw new InvalidOperationException("Unexpected load worker configuration.");
                    // A fresh worker must never migrate existing user data into its test directory.
                    if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Reepax")))
                        throw new InvalidOperationException("This isolated load worker requires a profile without legacy application data.");
                    SettingsService.AppDataDirectoryOverride = Path.Combine(root, "appdata");
                    AppLogger.LogsDirectory = Path.Combine(root, "logs");
                    AppLogger.IsLoggingEnabled = false;
                    DownloadPersistenceService.IsTestEnvironment = false;
                    await using var workload = new ConcurrentPackageWorkload(root, packages, rounds);
                    var report = await workload.RunAsync().WaitAsync(TimeSpan.FromMinutes(2));
                    Console.WriteLine(JsonSerializer.Serialize(report));
                    exit = 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(DiagnosticPrivacy.Sanitize(ex.ToString()));
                }
                finally { app.Shutdown(); }
            }));
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }
}
