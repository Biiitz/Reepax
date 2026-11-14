using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Update;
using Reepax.Services.Verification;

namespace Reepax.Tests;

internal sealed record RecoveryProcessReport(int ProcessId, string Boundary, bool Completed, bool Extracted,
    bool Notified, long Downloaded, string? Checksum, long ResumedFrom = 0);

internal static class RecoveryProcessWorker
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) return 0;
        if (args.Length == 2 && args[0] == "--ipc-contention-worker")
            return await SingleInstanceIpcConcurrencyWorker.RunAsync(int.Parse(args[1]));
        if (args.Length == 3 && args[0] == "--window-flow-worker")
            return WindowDownloadFlowWorker.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--column-menu-worker")
            return WindowDownloadFlowWorker.Run(args[1], args[2], columnsOnly: true);
        if (args.Length == 3 && args[0] == "--clipboard-window-worker")
            return WindowDownloadFlowWorker.Run(args[1], args[2], clipboardOnly: true);
        if (args.Length == 3 && args[0] == "--filter-update-worker")
            return WindowDownloadFlowWorker.Run(args[1], args[2], filtersOnly: true);
        if (args.Length == 3 && args[0] == "--shortcut-window-worker")
            return WindowDownloadFlowWorker.Run(args[1], args[2], shortcutsOnly: true);
        if (args.Length >= 4 && args[0] == "--package-restart-window-worker")
            return WindowDownloadFlowWorker.Run(args[1], args[2], packageRestartStage: args[3]);
        if (args.Length == 4 && args[0] == "--load-worker")
            return ConcurrentLoadWorker.Run(args[1], int.Parse(args[2]), int.Parse(args[3]));
        if ((args.Length == 6 || (args.Length == 7 && args[6] == "--released-helper")) && args[0] == "--run-update-helper")
        {
            SettingsService.AppDataDirectoryOverride = args[1];
            string hash;
            using (var setup = File.OpenRead(args[2]))
                hash = Convert.ToHexString(SHA256.HashData(setup));
            string script = AppUpdateService.GenerateInstallerUpdateScript(0, args[2], args[3], hash, args[4]);
            if (args.Length == 7)
            {
                using var resource = typeof(RecoveryProcessWorker).Assembly.GetManifestResourceStream(
                    "Reepax.Tests.Fixtures.Updates.InstallerUpdate.26.11.1.ps1")!;
                using var reader = new StreamReader(resource);
                script = script[..script.IndexOf("$setup = $source", StringComparison.Ordinal)] + reader.ReadToEnd();
            }
            string installerLog = args[5] + ".installer.log";
            script = "$installerLogPath = '" + installerLog.Replace("'", "''") + "'\n" +
                script.Replace(" /UPDATEHELPER=1", " /UPDATEHELPER=1 /LOG=`\"$installerLogPath`\"");
            File.WriteAllText(args[5], script, new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[5])!, "helper.approved"), "approved");
            using var helper = AppUpdateService.StartUpdateHelper(args[5]) ?? throw new IOException("Could not start the update helper.");
            try
            {
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
                if (helper.ExitCode != 0 && File.Exists(installerLog))
                    foreach (string line in File.ReadLines(installerLog).TakeLast(40))
                        Console.Error.WriteLine(DiagnosticPrivacy.Sanitize(line));
                return helper.ExitCode;
            }
            finally
            {
                if (!helper.HasExited) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
            }
        }
        if (args.Length != 7 || args[0] != "--recovery-worker") return 2;
        try
        {
            string root = Path.GetFullPath(args[1]);
            if (!Directory.Exists(root) || !Path.GetFileName(root).StartsWith("ProcessRecovery_", StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected recovery test directory.");
            DownloadPersistenceService.IsTestEnvironment = true;
            SettingsService.AppDataDirectoryOverride = Path.Combine(root, "appdata");
            AppLogger.LogsDirectory = Path.Combine(root, "logs");
            AppLogger.IsLoggingEnabled = false;
            var settings = SettingsService.Instance.Settings;
            settings.EnableChecksumVerification = true;
            settings.EnableCompletionNotifications = settings.EnableCompletionSound = settings.EnableErrorSound = false;
            settings.AutoPar2Repair = settings.DeleteArchiveAfterExtraction = settings.MoveArchiveToRecycleBin = false;
            settings.PostDownloadAction = PostDownloadAction.None;
            settings.MaxAutoRetryOnCorruption = 0;
            using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(10000);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            void Report(RecoveryProcessReport report) => writer.WriteLine(JsonSerializer.Serialize(report));
            void Stop(RecoveryProcessReport report)
            {
                Report(report);
                // The parent keeps this pipe open until it terminates this process.
                reader.ReadLine();
                throw new IOException("The parent disconnected without terminating the recovery worker.");
            }
            int connections = int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture);
            if (args[3] == "browser-save") await BrowserRestartWorker.SaveAsync(root, args[5], Report);
            else if (args[3] == "browser-recover") await DownloadAsync(root, args[5], connections, false, Report, browserResume: true);
            else if (args[3] == "download-crash") await DownloadAsync(root, args[5], connections, true, Stop);
            else if (args[3] == "download-recover") await DownloadAsync(root, args[5], connections, false, Report);
            else if (args[3] == "archive-crash") await ExtractAsync(root, args[5], args[4], Stop);
            else if (args[3] == "archive-recover") RecoverArchive(root, Report);
            else throw new InvalidOperationException("Unknown recovery worker operation.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + DiagnosticPrivacy.Sanitize(ex.Message));
            return 1;
        }
    }

    private static DownloadPackage CreatePackage(string root, string url, string name, bool extract)
    {
        string destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        var package = new DownloadPackage
        {
            Name = "Recovery sample", IsCustomName = true, SaveDirectory = destination,
            AutoExtractArchives = extract, AutoPar2Repair = false, AutoResolveHostLinks = false
        };
        package.Items.Add(new DownloadItem
        {
            FileName = name, SaveFilePath = Path.Combine(destination, name), OriginalUrl = url, DirectDownloadUrl = url,
            IsCustomName = true, ExpectedChecksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))),
            Status = DownloadStatus.Queued
        });
        package.EnsureNextTaskSteps();
        return package;
    }

    private static RecoveryProcessReport State(DownloadPackage package, string boundary, long resumedFrom = 0)
    {
        var item = package.Items.Single();
        return new(Environment.ProcessId, boundary, package.CheckIsFullyCompleted(), package.IsExtracted,
            package.HasCompletedNotified, item.DownloadedBytes, item.CalculatedChecksum, resumedFrom);
    }

    private static async Task DownloadAsync(string root, string url, int connections, bool interrupt, Action<RecoveryProcessReport> report, bool browserResume = false)
    {
        using var storage = new DownloadPersistenceService(Path.Combine(root, "downloads.json"));
        var package = interrupt ? CreatePackage(root, url, "download.bin", false) : storage.LoadDownloads().Single();
        var item = package.Items.Single();
        long resumedFrom = item.DownloadedBytes;
        if (!interrupt && item.Status != DownloadStatus.Completed &&
            (item.Status != DownloadStatus.Paused || item.IsReadyForPostProcessing || resumedFrom <= 0))
            throw new InvalidDataException("An interrupted download was not restored as resumable work.");
        using var client = HttpUserAgentService.CreateHttpClient(TimeSpan.FromHours(6), HttpContentType.BinaryOrAny,
            automaticDecompression: false, scopedCookies: true);
        var engine = browserResume ? DownloadEngine.Instance : new DownloadEngine(client);
        engine.MaxConnectionsPerDownload = connections;
        var queue = browserResume ? QueueManager.Instance : new QueueManager(subscribeToDownloadEvents: false);
        var forbiddenBrowser = new BrowserRestartWorker.NoBrowserHost();
        if (browserResume) queue.BrowserHost = forbiddenBrowser;
        queue.Packages.Clear();
        queue.Packages.Add(package);
        Task completion = Task.CompletedTask;
        if (!browserResume)
        {
            engine.DownloadCompleted += download => completion = queue.HandleDownloadCompleted(download);
            engine.DownloadFailed += queue.OnBackgroundDownloadFailed;
        }
        if (interrupt)
        {
            storage.SaveDownloads(new[] { package }, sync: true);
            engine.SetSpeedLimit(64 * 1024);
            int signaled = 0;
            DownloadEngine.CheckpointPublished = path =>
            {
                if (path != item.SaveFilePath + ".part.segments" || DownloadPersistenceService.GetSegmentedDownloadedBytes(item.SaveFilePath) is not > 0 ||
                    Interlocked.Exchange(ref signaled, 1) != 0) return;
                storage.SaveDownloads(new[] { package }, sync: true);
                report(State(package, "checkpoint"));
            };
        }
        if (item.Status != DownloadStatus.Completed)
        {
            if (browserResume)
            {
                queue.ResumeItem(item);
                var transfer = engine.GetDownloadTask(item.Id);
                if (transfer != null) await transfer;
                else if (item.Status != DownloadStatus.Completed) throw new InvalidOperationException("Resume did not start the saved download.");
                await BrowserHandoffFixture.UntilAsync(() => item.Status == DownloadStatus.Failed ||
                    (item.Status == DownloadStatus.Completed && !item.IsVerifyingIntegrity && package.HasCompletedNotified));
            }
            else await engine.ResumeDownloadAsync(item);
            await completion;
        }
        if (interrupt) throw new InvalidOperationException("Download finished before the requested crash boundary.");
        var verification = await ChecksumVerificationService.Instance.VerifyFileIntegrityAsync(item);
        if (!verification.IsValid || item.Status != DownloadStatus.Completed || !item.IsReadyForPostProcessing)
            throw new InvalidDataException($"Recovered download did not pass its completion checks: {item.Status}, {item.FailureDetails?.Kind}, {item.DownloadedBytes}/{item.TotalBytes}, ready={item.IsReadyForPostProcessing}, valid={verification.IsValid}.");
        if (browserResume && forbiddenBrowser.Requested) throw new InvalidOperationException("Resume tried to open a browser window.");
        storage.SaveDownloads(new[] { package }, sync: true);
        report(State(package, "recovered", resumedFrom));
        queue.StopQueue();
    }

    private static async Task ExtractAsync(string root, string url, string boundary, Action<RecoveryProcessReport> report)
    {
        using var storage = new DownloadPersistenceService(Path.Combine(root, "downloads.json"));
        var package = CreatePackage(root, url, "source.zip", true);
        var item = package.Items.Single();
        storage.SaveDownloads(new[] { package }, sync: true);
        var requested = Enum.Parse<ExtractionPublicationBoundary>(boundary);
        ExtractionRecoveryJournal.PublicationCheckpoint = (destination, phase) =>
        {
            string expected = phase is ExtractionPublicationBoundary.CommitDecided or ExtractionPublicationBoundary.ReplacementMoved
                ? package.SaveDirectory : Path.Combine(package.SaveDirectory, "payload.bin");
            if (phase == ExtractionPublicationBoundary.ReplacementMoved) destination = Path.GetDirectoryName(destination)!;
            if (phase != requested || !string.Equals(destination, expected, StringComparison.OrdinalIgnoreCase)) return;
            if (phase == ExtractionPublicationBoundary.ReplacementMoved && Directory.GetDirectories(package.SaveDirectory, ".archive-work-*")
                .Any(workspace => Directory.EnumerateFiles(Path.Combine(workspace, "output"), "*", SearchOption.AllDirectories).Any())) return;
            storage.SaveDownloads(new[] { package }, sync: true);
            report(State(package, boundary));
        };
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var engine = new DownloadEngine(client);
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        queue.Packages.Clear();
        queue.Packages.Add(package);
        Task completion = Task.CompletedTask;
        Task<bool> extraction = Task.FromResult(false);
        queue.ExtractPackageAsync = value => extraction = ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(value);
        engine.DownloadCompleted += download => completion = queue.HandleDownloadCompleted(download);
        engine.DownloadFailed += queue.OnBackgroundDownloadFailed;
        await engine.ResumeDownloadAsync(item);
        await completion;
        await extraction;
        throw new InvalidOperationException("Extraction finished before the requested crash boundary.");
    }

    private static void RecoverArchive(string root, Action<RecoveryProcessReport> report)
    {
        using var storage = new DownloadPersistenceService(Path.Combine(root, "downloads.json"));
        var package = storage.LoadDownloads().Single();
        // This constructor is the recovery entry used when a new extraction starts.
        using (var workspace = new ArchiveExtractionWorkspace(package.SaveDirectory)) { }
        if (package.IsExtracted || package.HasCompletedNotified || package.CheckIsFullyCompleted())
            throw new InvalidDataException("Interrupted extraction was incorrectly restored as completed.");
        storage.SaveDownloads(new[] { package }, sync: true);
        report(State(package, "recovered"));
    }
}
