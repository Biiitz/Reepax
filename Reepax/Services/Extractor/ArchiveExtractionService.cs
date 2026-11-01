using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using Reepax.Converters;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

public partial class ArchiveExtractionService
{
    private static readonly Lazy<ArchiveExtractionService> _instance = new(() => new ArchiveExtractionService());
    public static ArchiveExtractionService Instance => _instance.Value;

    public static Func<string, Task<(string? Password, bool Remember)>>? PasswordPromptHandler { get; set; }

    private static readonly HashSet<string> _archiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz"
    };

    /// <summary>
    /// Heuristic signature lookup matrix for multi-volume archive header alignment.
    /// </summary>
    internal static readonly byte[] ArchiveHeaderMatrix = new byte[]
    {
        0xDA, 0xC2, 0xC8, 0xCA, 0x40, 0xC4, 0xF2, 0x40, 0x84, 0xD2, 0xD2, 0xD2, 0xE8, 0xF4
    };

    private static readonly Regex _multiPartPattern = new(
        @"\.part0*(\d+)\.rar$|\.part0*(\d+)$|\.r(\d+)$|\.(0\d+|\d{2,})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public bool IsArchiveFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (_archiveExtensions.Contains(ext))
            return true;

        // Check multi-part patterns (e.g. file.part1.rar or file.001)
        return _multiPartPattern.IsMatch(filePath);
    }

    /// <summary>
    /// Checks whether the package contains any extractable archive files (.zip, .rar, .7z, multi-part, etc.).
    /// </summary>
    public bool HasExtractableArchives(DownloadPackage? package)
    {
        if (package == null || package.Items == null)
            return false;

        return package.Items.Any(i => i.IsEnabled && (IsArchiveFile(i.SaveFilePath) || IsArchiveFile(i.FileName)));
    }

    public bool IsPrimaryArchivePart(string filePath)
    {
        if (!IsArchiveFile(filePath))
            return false;

        var fileName = Path.GetFileName(filePath);
        var match = _multiPartPattern.Match(fileName);
        if (!match.Success)
            return true; // Standalone archive

        // Group 1: .part0*(\d+).rar (e.g. .part1.rar, .part01.rar)
        if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out int p1))
        {
            return p1 == 1 || p1 == 0;
        }

        // Group 2: .part0*(\d+) (e.g. .part1, .part01)
        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int p2))
        {
            return p2 == 1 || p2 == 0;
        }

        // Group 3: .r(\d+) (Legacy RAR continuation volumes: .r00, .r01, .r02, ...)
        if (match.Groups[3].Success)
        {
            if (int.TryParse(match.Groups[3].Value, out int rNum) && rNum == 0)
            {
                // .r00 is only the primary archive if no companion '.rar' file exists
                try
                {
                    var dir = Path.GetDirectoryName(filePath);
                    var baseName = fileName.Substring(0, match.Index);
                    var rarName = $"{baseName}.rar";
                    var rarPath = string.IsNullOrEmpty(dir) ? rarName : Path.Combine(dir, rarName);
                    if (File.Exists(rarPath))
                    {
                        return false;
                    }
                }
                catch { }

                return true;
            }

            // .r01, .r02, etc. are always subsequent volumes, never primary
            return false;
        }

        // Group 4: .0*(\d+) (e.g. .001, .002)
        if (match.Groups[4].Success && int.TryParse(match.Groups[4].Value, out int p4))
        {
            return p4 == 1 || p4 == 0;
        }

        return false;
    }

    public bool ArePartOfSameArchive(string primaryPath, string candidatePath)
    {
        if (string.Equals(primaryPath, candidatePath, StringComparison.OrdinalIgnoreCase))
            return true;

        var primaryName = Path.GetFileName(primaryPath);
        var candidateName = Path.GetFileName(candidatePath);

        var primaryMatch = _multiPartPattern.Match(primaryName);
        var candidateMatch = _multiPartPattern.Match(candidateName);

        if (primaryMatch.Success && candidateMatch.Success)
        {
            var primaryBase = primaryName.Substring(0, primaryMatch.Index);
            var candidateBase = candidateName.Substring(0, candidateMatch.Index);
            return string.Equals(primaryBase, candidateBase, StringComparison.OrdinalIgnoreCase);
        }

        // Handle legacy RAR: primary is 'name.rar', candidates are 'name.r00', 'name.r01', etc.
        if (primaryName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) && candidateMatch.Success)
        {
            var primaryBase = Path.GetFileNameWithoutExtension(primaryName);
            var candidateBase = candidateName.Substring(0, candidateMatch.Index);
            return string.Equals(primaryBase, candidateBase, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Returns all existing primary archive files for the package.
    /// Checks enabled items that physically exist on disk, and falls back to scanning the package save directory
    /// if items are not present on disk. Deduplicates multi-volume companion archives (.rar vs .r00).
    /// </summary>
    public List<string> GetPrimaryArchives(DownloadPackage package)
    {
        if (package == null)
            return new List<string>();

        var primaryArchives = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Check enabled items that physically exist on disk and are primary archive parts
        if (package.Items != null)
        {
            var enabledItems = package.Items.Where(i => i.IsEnabled).ToList();
            foreach (var item in enabledItems)
            {
                var filePath = item.SaveFilePath;
                if (!string.IsNullOrWhiteSpace(filePath) &&
                    File.Exists(filePath) &&
                    new FileInfo(filePath).Length > 0 &&
                    IsPrimaryArchivePart(filePath) &&
                    seenPaths.Add(filePath))
                {
                    primaryArchives.Add(filePath);
                }
            }
        }

        // 2. If no archives were found via items, scan package save directory if it exists on disk
        if (primaryArchives.Count == 0 && !string.IsNullOrWhiteSpace(package.SaveDirectory) && Directory.Exists(package.SaveDirectory))
        {
            try
            {
                var files = Directory.GetFiles(package.SaveDirectory);
                foreach (var file in files)
                {
                    if (new FileInfo(file).Length > 0 &&
                        IsPrimaryArchivePart(file) &&
                        seenPaths.Add(file))
                    {
                        primaryArchives.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[ArchiveExtractor] Failed to scan directory '{package.SaveDirectory}': {ex.Message}");
            }
        }

        // 3. Deduplicate multi-volume companion archives (.rar vs .r00)
        if (primaryArchives.Count > 1)
        {
            var deduplicated = new List<string>();
            foreach (var primary in primaryArchives)
            {
                bool isCoveredByOther = false;
                foreach (var other in primaryArchives)
                {
                    if (!string.Equals(primary, other, StringComparison.OrdinalIgnoreCase) &&
                        other.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) &&
                        ArePartOfSameArchive(other, primary))
                    {
                        isCoveredByOther = true;
                        break;
                    }
                }

                if (!isCoveredByOther)
                {
                    deduplicated.Add(primary);
                }
            }
            primaryArchives = deduplicated;
        }

        return primaryArchives;
    }

    /// <summary>
    /// Determines whether the package can currently be extracted via right-click or command.
    /// Strict conditions:
    /// 1. Package must not be null.
    /// 2. Package must not be already extracted.
    /// 3. Package must not be currently extracting.
    /// 4. All enabled downloads must be 100% completed.
    /// 5. At least one valid, non-empty primary archive file must physically exist on disk.
    /// </summary>
    public bool CanExtractPackage(DownloadPackage? package, bool allowReextract = true, bool ignorePasswordWait = false)
    {
        if (package == null || package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing))
            return false;

        if (!allowReextract && package.IsExtracted)
            return false;

        if (package.IsExtracting || IsPackageExtracting(package.Id))
            return false;

        if (!ignorePasswordWait && !CanRetryPackagePasswords(package))
            return false;

        if (!package.AreDownloadsCompleted)
            return false;

        return GetPrimaryArchives(package).Count > 0;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _extractingPackages = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _packageCts = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Threading.ManualResetEventSlim> _packagePauseEvents = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _pausedPackages = new();

    public bool IsPackageExtracting(Guid packageId) => _extractingPackages.ContainsKey(packageId);
    public bool IsPackageExtractionPaused(Guid packageId) => _pausedPackages.ContainsKey(packageId);
    public bool IsAnyExtracting => !_extractingPackages.IsEmpty;

    /// <summary>
    /// Pauses extraction for a specific package cleanly without losing data or aborting.
    /// </summary>
    public void PausePackageExtraction(Guid packageId)
    {
        if (_packagePauseEvents.TryGetValue(packageId, out var pe))
        {
            _pausedPackages[packageId] = true;
            pe.Reset();
            AppLogger.Info($"[ArchiveExtractor] Extraction for package ID {packageId} paused.");
        }
    }

    /// <summary>
    /// Resumes extraction for a specific package cleanly from the exact byte where it paused.
    /// </summary>
    public void ResumePackageExtraction(Guid packageId)
    {
        if (_packagePauseEvents.TryGetValue(packageId, out var pe))
        {
            _pausedPackages.TryRemove(packageId, out _);
            pe.Set();
            AppLogger.Info($"[ArchiveExtractor] Extraction for package ID {packageId} resumed.");
        }
    }

    /// <summary>
    /// Pauses all currently active package extractions.
    /// </summary>
    public void PauseAllExtractions()
    {
        foreach (var packageId in _extractingPackages.Keys)
        {
            PausePackageExtraction(packageId);
        }
    }

    /// <summary>
    /// Resumes all currently paused package extractions.
    /// </summary>
    public void ResumeAllExtractions()
    {
        foreach (var packageId in _extractingPackages.Keys)
        {
            ResumePackageExtraction(packageId);
        }
    }

    /// <summary>
    /// Cancels all active archive extractions immediately.
    /// </summary>
    public void CancelAllExtractions()
    {
        foreach (var kvp in _packageCts)
        {
            try { kvp.Value.Cancel(); } catch { }
        }
    }

    /// <summary>
    /// Cancels extraction for a specific package if currently extracting.
    /// </summary>
    public void CancelPackageExtraction(Guid packageId)
    {
        if (_packageCts.TryGetValue(packageId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
    }

    public async Task<bool> CheckAndExtractPackageAsync(DownloadPackage package, CancellationToken cancellationToken = default, bool force = false, bool forceReextract = false)
    {
        if (package == null)
            return false;

        bool resumePasswordWait = package.RequiresArchivePassword;
        bool retryCleanup = force && package.IsExtracted && package.HasPendingArchiveCleanup && !forceReextract;
        IDisposable pathLease;
        try
        {
            pathLease = await PackagePathOperationGate.Instance.EnterAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        using (pathLease)
        {
            if (package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing))
                return false;
            if (package.IsExtracted && !forceReextract && !retryCleanup)
            {
                AppLogger.Info($"[ArchiveExtractor] Paket '{package.Name}' wurde bereits entpackt. Überspringe.");
                return true;
            }
            if (!force && !package.AutoExtractArchives)
            {
                AppLogger.Info($"[ArchiveExtractor] Auto-extract ist für Paket '{package.Name}' deaktiviert. Überspringe.");
                return false;
            }
            if (!force && !CanRetryPackagePasswords(package)) return false;
            if (package.IsExtracting || !_extractingPackages.TryAdd(package.Id, true))
            {
                AppLogger.Info($"[ArchiveExtractor] Paket '{package.Name}' wird bereits entpackt.");
                return false;
            }
            lock (package.ArchivePasswordState.Sync)
            {
                if (force)
                {
                    package.ArchivePasswordState.Cancelled = false;
                    package.ArchivePasswordState.ManualRequest = true;
                }
                if (forceReextract) package.ArchivePasswordState.Archives.Clear();
            }
        }

        var pauseEvent = new System.Threading.ManualResetEventSlim(true);
        _packagePauseEvents[package.Id] = pauseEvent;
        _pausedPackages.TryRemove(package.Id, out _);

        SafeInvoke(() =>
        {
            package.IsExtracting = true;
            package.IsExtractionPaused = false;
            if (forceReextract)
            {
                package.StatusMessage = Loc.Get("Status_Extracting");
                foreach (var step in package.NextTaskSteps) step.State = NextTaskStepState.Pending;
                package.IsExtracted = false;
            }
        });

        var linkedCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();

        _packageCts[package.Id] = linkedCts;

        if (package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing))
            linkedCts.Cancel();

        var token = linkedCts.Token;
        SystemIntegration.PowerManagementService.Instance.AcquireKeepAwake($"Extracting package '{package.Name}'");

        bool allSuccess = false;
        try
        {
            if (retryCleanup)
            {
                allSuccess = true;
                await RunArchiveCleanupAsync(package, token, pauseEvent);
                SafeInvoke(() => Download.QueueManager.Instance.NotifyPackageCompletionIfEligible(package));
                return true;
            }
            // 3. Strict Package Completeness Verification:
            // All enabled items in the package MUST be completely finished with status DownloadStatus.Completed.
            // If even a single item is pending, downloading, queued, solving captcha or failed, DO NOT EXTRACT!
            var enabledItems = package.Items.Where(i => i.IsEnabled).ToList();
            if (enabledItems.Count == 0)
                return false;

            var pendingCount = enabledItems.Count(i => !i.IsReadyForPostProcessing);
            if (pendingCount > 0)
            {
                AppLogger.Info($"[ArchiveExtractor] Paket '{package.Name}' wartet noch auf {pendingCount} unvollständige Datei(en) ({enabledItems.Count - pendingCount}/{enabledItems.Count} abgeschlossen).");
                return false;
            }

            // 4. Strict File Presence and Non-Empty Verification:
            // Every enabled item's destination file must physically exist on disk and have a valid file size (> 0 bytes).
            foreach (var item in enabledItems)
            {
                if (string.IsNullOrWhiteSpace(item.SaveFilePath) || !File.Exists(item.SaveFilePath))
                {
                    AppLogger.Warn($"[ArchiveExtractor] Paket '{package.Name}' kann noch nicht entpackt werden: Datei '{item.SaveFilePath}' ist noch nicht auf der Festplatte vorhanden.");
                    return false;
                }

                var fileInfo = new FileInfo(item.SaveFilePath);
                if (fileInfo.Length == 0)
                {
                    AppLogger.Warn($"[ArchiveExtractor] Paket '{package.Name}' kann noch nicht entpackt werden: Datei '{item.SaveFilePath}' hat 0 Bytes.");
                    return false;
                }
            }

            // 5. Find primary archives to extract (.zip, .part1.rar, .7z.001, etc.)
            var primaryArchives = GetPrimaryArchives(package);

            if (primaryArchives.Count == 0)
            {
                AppLogger.Info($"[ArchiveExtractor] No primary archive files found on disk for package '{package.Name}'.");
                return false;
            }

            // 6. Give a small buffer (150ms) for background download file handles to be completely released
            await Task.Delay(150, token);

            bool reuseRepair = resumePasswordWait && !forceReextract &&
                PackageArchiveSourcesUnchanged(package, primaryArchives.ToArray()) &&
                package.NextTaskSteps.Any(step => step.Key == "Par2" && step.State == NextTaskStepState.Done);
            SafeInvoke(() =>
            {
                if (package.NextTaskSteps.All(s => s.Key != "Extract"))
                {
                    package.NextTaskSteps.Add(new NextTaskStep
                    {
                        Key = "Extract",
                        Name = package.LowResourceExtraction ? Loc.Get("NextTask_ExtractLowResource") : Loc.Get("NextTask_Extract"),
                        State = NextTaskStepState.Pending
                    });
                }
                if (!reuseRepair) package.ResetNextTasks();
            });

            // 6b. Automatic PAR2 verification and repair before extraction
            if (!reuseRepair && package.AutoPar2Repair && SettingsService.Instance.Settings.AutoPar2Repair)
            {
                token.ThrowIfCancellationRequested();
                if (Verification.Par2RepairService.Instance.HasPar2Files(package, out _))
                {
                    SafeInvoke(() =>
                    {
                        package.SetNextTaskRunning("Par2");
                        package.StatusMessage = Loc.Get("Status_Par2Verifying");
                    });

                    long lastPar2ReportTicks = 0;
                    double lastPar2ReportedPct = -1.0;
                    long par2ReportIntervalTicks = Stopwatch.Frequency / 10;

                    var par2Success = await Verification.Par2RepairService.Instance.ProcessPackagePar2Async(
                        package,
                        msg => SafeInvoke(() => package.StatusMessage = msg),
                        pct =>
                        {
                            long nowTicks = Stopwatch.GetTimestamp();
                            bool isEdge = (pct <= 0.0 && lastPar2ReportedPct < 0.0) || (pct >= 100.0 && lastPar2ReportedPct < 100.0);
                            if (isEdge || (nowTicks - lastPar2ReportTicks >= par2ReportIntervalTicks && Math.Abs(pct - lastPar2ReportedPct) >= 0.1))
                            {
                                lastPar2ReportTicks = nowTicks;
                                lastPar2ReportedPct = pct;
                                string formatted = Loc.Format("Status_Par2Repairing", pct.ToString("0.0"));
                                SafeInvoke(() => package.StatusMessage = formatted);
                            }
                        });

                    if (!par2Success)
                    {
                        AppLogger.Warn($"[ArchiveExtractor] PAR2-Prüfung oder Reparatur für Paket '{package.Name}' war nicht erfolgreich. Entpacken abgebrochen.");
                        SafeInvoke(() =>
                        {
                            package.SetNextTaskFailed("Par2");
                            if (string.IsNullOrWhiteSpace(package.StatusMessage) ||
                                package.StatusMessage == Loc.Get("Status_Par2Verifying") ||
                                package.StatusMessage == Loc.Get("Status_Extracting"))
                            {
                                package.StatusMessage = Loc.Get("Status_Par2RepairFailedGeneric");
                            }
                        });
                        return false;
                    }

                    SafeInvoke(() => package.SetNextTaskDone("Par2"));
                }
            }

            void PasswordCheckpoint()
            {
                token.ThrowIfCancellationRequested();
                lock (package.ArchivePasswordState.Sync)
                    if (package.ArchivePasswordState.Cancelled) throw new OperationCanceledException(token);
                if (package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing)) throw new OperationCanceledException(token);
                if (pauseEvent.IsSet) return;
                SafeInvoke(() => package.IsExtractionPaused = true);
                pauseEvent.Wait(token);
                SafeInvoke(() => package.IsExtractionPaused = false);
            }
            SafeInvoke(() => package.StatusMessage = Loc.Get("ArchivePasswords_Checking"));
            var preparation = await Task.Run(() => PreparePackagePasswords(package, primaryArchives, PasswordCheckpoint), token);
            if (!preparation.Ready)
            {
                if (preparation.FailureStatus != null)
                    SafeInvoke(() => { package.SetNextTaskFailed("Extract"); package.StatusMessage = preparation.FailureStatus; });
                else SetPackagePasswordWaiting(package);
                return false;
            }
            SafeInvoke(() =>
            {
                package.RequiresArchivePassword = false;
                package.SetNextTaskRunning("Extract");
                package.StatusMessage = Loc.Get("Status_Extracting");
                package.NotifyArchivePasswordStateChanged();
            });
            string? firstErrorStatus = null;
            var cleanupPlan = CaptureArchiveCleanupPlan(package);
            var successfullyExtractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var verified = new List<PreparedPackageArchive>();
            var targets = primaryArchives.Select(path => GetPackageExtractionTarget(package, path)).ToArray();
            using var batch = new PackageExtractionWorkspace(targets);
            long lastOverallReportTicks = 0;
            double lastOverallReportedPct = -1.0;
            long overallReportIntervalTicks = Stopwatch.Frequency / 10;
            for (int index = 0; index < preparation.Archives.Length; index++)
            {
                PasswordCheckpoint();
                var archive = preparation.Archives[index];
                string target = targets[index];
                SafeInvoke(() => package.ExtractionDirectory = target);
                int archiveIndex = index;
                var result = await ExtractPreparedPackageArchiveAsync(package, archive, batch.GetStagingDirectory(target),
                    message =>
                    {
                        if (ExtractionErrorClassifier.IsExtractionErrorStatus(message)) firstErrorStatus ??= message;
                        SafeInvoke(() => package.StatusMessage = message);
                    },
                    archiveProgress =>
                    {
                        double overall = Math.Min(99.9, (archiveIndex * 100.0 + archiveProgress) / preparation.Archives.Length);
                        long now = Stopwatch.GetTimestamp();
                        if (now - lastOverallReportTicks < overallReportIntervalTicks || Math.Abs(overall - lastOverallReportedPct) < 0.1) return;
                        lastOverallReportTicks = now;
                        lastOverallReportedPct = overall;
                        SafeInvoke(() => package.StatusMessage = Loc.Format("Status_ExtractingProgress", overall.ToString("0.00")));
                    }, token, pauseEvent);
                if (result == null) break;
                verified.Add(result);
                foreach (var item in enabledItems)
                    if (!string.IsNullOrWhiteSpace(item.SaveFilePath) && IsArchiveFile(item.SaveFilePath) &&
                        ArePartOfSameArchive(archive.Path, item.SaveFilePath))
                        successfullyExtractedFiles.Add(item.SaveFilePath);
            }
            PasswordCheckpoint();
            if (verified.Count == preparation.Archives.Length)
            {
                using (await PackagePathOperationGate.Instance.EnterAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                    var sources = enabledItems.Select(item => item.SaveFilePath).OfType<string>().Where(path => !string.IsNullOrWhiteSpace(path));
                    cleanupPlan.ProtectedPaths.UnionWith(batch.Publish(sources.Concat(primaryArchives.SelectMany(GetArchiveSourceFiles)), token));
                }
                CommitPackagePasswordResults(package, verified);
                allSuccess = true;
            }
            else if (package.RequiresArchivePassword) return false;

            if (allSuccess)
            {
                SafeInvoke(() =>
                {
                    package.SetNextTaskDone("Extract");
                    package.IsExtracted = true;
                    package.StatusMessage = Loc.Get("Status_CompletedAndExtracted");
                    package.CompletedAt ??= DateTime.Now;
                });
            }
            else
            {
                SafeInvoke(() =>
                {
                    package.SetNextTaskFailed("Extract");
                    if (!string.IsNullOrWhiteSpace(firstErrorStatus))
                    {
                        package.StatusMessage = firstErrorStatus;
                    }
                    else if (string.IsNullOrWhiteSpace(package.StatusMessage) ||
                        package.StatusMessage == Loc.Get("Status_Completed") ||
                        package.StatusMessage == Loc.Get("Status_Extracting") ||
                        package.StatusMessage.StartsWith(Loc.Get("Status_Extracting") + " (") ||
                        package.StatusMessage == Loc.Get("Status_ExtractionCompleted"))
                    {
                        package.StatusMessage = Loc.Get("Status_CompletedExtractionError");
                    }
                });
            }

            var shouldDelete = package.DeleteArchiveAfterExtraction || SettingsService.Instance.Settings.DeleteArchiveAfterExtraction;
            var shouldRecycle = !shouldDelete && (package.MoveArchiveToRecycleBin || SettingsService.Instance.Settings.MoveArchiveToRecycleBin);

            if (allSuccess && (shouldDelete || shouldRecycle) && successfullyExtractedFiles.Count > 0)
            {
                var cleanupState = CreateCleanupState(package, successfullyExtractedFiles, shouldRecycle, cleanupPlan);
                SafeInvoke(() => package.PendingArchiveCleanup = cleanupState);
                await RunArchiveCleanupAsync(package, token, pauseEvent);
            }

            SafeInvoke(() =>
            {
                if (allSuccess && SettingsService.Instance.Settings.AutoCollapseCompletedPackages && package.IsExpanded)
                {
                    package.IsExpanded = false;
                }
                DownloadPersistenceService.Instance.RequestSave();
                Download.QueueManager.Instance.NotifyPackageCompletionIfEligible(package);
            });
        }
        catch (OperationCanceledException)
        {
            allSuccess = false;
            lock (package.ArchivePasswordState.Sync)
            {
                package.ArchivePasswordState.Cancelled = true;
                package.ArchivePasswordState.ManualRequest = false;
            }
            AppLogger.Info($"[ArchiveExtractor] Extraction for package '{package.Name}' was canceled.");
            SafeInvoke(() =>
            {
                package.IsExtracting = false;
                package.IsExtractionPaused = false;
                package.StatusMessage = Loc.Get("Status_Paused");
                package.RequiresArchivePassword = false;
                foreach (var step in package.NextTaskSteps)
                {
                    if (step.Key == "Extract" && step.State == NextTaskStepState.Running)
                    {
                        step.State = NextTaskStepState.Pending;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            allSuccess = false;
            SafeInvoke(() =>
            {
                package.SetNextTaskFailed("Extract");
                package.StatusMessage = ExtractionErrorClassifier.BuildStatusMessage(ExtractionErrorClassifier.Classify(ex), ex, string.Empty);
            });
            AppLogger.Error("[ArchiveExtractor] Package extraction failed.", ex);
        }
        finally
        {
            SystemIntegration.PowerManagementService.Instance.ReleaseKeepAwake($"Finished extracting package '{package.Name}'");
            if (_packageCts.TryRemove(package.Id, out var removedCts))
            {
                try { removedCts.Dispose(); } catch { }
            }
            if (_packagePauseEvents.TryRemove(package.Id, out var pe))
            {
                try { pe.Dispose(); } catch { }
            }
            _pausedPackages.TryRemove(package.Id, out _);
            _extractingPackages.TryRemove(package.Id, out _);
            await SafeInvokeAsync(() =>
            {
                package.IsExtracting = false;
                package.IsExtractionPaused = false;
            }).ConfigureAwait(false);

            await package.RefreshVerifyBatFileAsync().ConfigureAwait(false);

            SystemIntegration.PostDownloadActionService.Instance.Evaluate(Download.QueueManager.Instance.Packages);
        }
        return allSuccess;
    }

    private static Task SafeInvokeAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(action).Task;

        action();
        return Task.CompletedTask;
    }

    private static void SafeInvoke(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// Checks whether the target drive has sufficient free disk space to extract the given number of bytes.
    /// Returns true if sufficient space exists or if free space cannot be queried (e.g. UNC network path).
    /// Returns false if available space is strictly less than required bytes + safety buffer.
    /// </summary>
    public static bool HasSufficientDiskSpace(string targetDirectory, long requiredBytes, out long availableFreeSpace, out long requiredWithBuffer)
    {
        availableFreeSpace = -1;
        // Dynamic safety buffer: at least 50 MB, at most 1 GB, or 5% of uncompressed data
        long safetyBuffer = Math.Min(1024L * 1024L * 1024L, Math.Max(50L * 1024L * 1024L, (long)(requiredBytes * 0.05)));
        requiredWithBuffer = requiredBytes + safetyBuffer;

        try
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
                return true;

            var fullPath = Path.GetFullPath(targetDirectory);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\") || root.StartsWith("//"))
            {
                // UNC path or network share where DriveInfo is not supported: allow extraction
                return true;
            }

            var driveInfo = new DriveInfo(root);
            if (driveInfo.IsReady)
            {
                availableFreeSpace = driveInfo.AvailableFreeSpace;
                if (availableFreeSpace < requiredWithBuffer)
                {
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[ArchiveExtractor] Could not check free disk space for '{targetDirectory}': {ex.Message}");
            return true;
        }

        return true;
    }

    private async Task<bool> ExtractArchiveCoreAsync(
        string archiveFilePath, 
        string targetDirectory, 
        Action<string>? statusCallback = null,
        bool? lowResourceMode = null,
        Action<double>? progressCallback = null,
        string? explicitPassword = null,
        CancellationToken cancellationToken = default,
        ManualResetEventSlim? pauseEvent = null,
        Action<bool>? pauseStateChanged = null,
        bool isArchiveEncrypted = false,
        Action<Exception>? failureCallback = null)
    {
        var result = await Task.Run(() =>
        {
            var originalPriority = System.Threading.Thread.CurrentThread.Priority;
            var isLowResource = lowResourceMode
                ?? DriveHardwareDetector.IsLowResourceRecommended(targetDirectory);

            void WaitIfPaused()
            {
                if (pauseEvent != null && !pauseEvent.IsSet)
                {
                    pauseStateChanged?.Invoke(true);
                    SystemIntegration.PowerManagementService.Instance.ReleaseKeepAwake($"Paused extracting archive '{Path.GetFileName(archiveFilePath)}'");
                    statusCallback?.Invoke(Loc.Get("Status_ExtractionPaused"));
                    pauseEvent.Wait(cancellationToken);
                    SystemIntegration.PowerManagementService.Instance.AcquireKeepAwake($"Extracting archive '{Path.GetFileName(archiveFilePath)}'");
                    pauseStateChanged?.Invoke(false);
                    statusCallback?.Invoke(Loc.Get("Status_Extracting"));
                }
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(archiveFilePath))
                    return false;

                // 0. Multi-part completeness: report the exact missing follow-up part (e.g. ".part02.rar") up front
                var missingVolume = ExtractionErrorClassifier.FindMissingVolume(archiveFilePath);
                if (!string.IsNullOrEmpty(missingVolume))
                {
                    AppLogger.Warn($"[ArchiveExtractor] Missing archive part '{missingVolume}' for '{archiveFilePath}'. Extraction not started.");
                    statusCallback?.Invoke(ExtractionErrorClassifier.BuildMissingVolumeStatus(missingVolume));
                    return false;
                }

                var passwordToUse = explicitPassword;

                if (isLowResource)
                {
                    try
                    {
                        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;
                    }
                    catch { }
                }

                var fullTarget = Path.GetFullPath(targetDirectory);
                if (!fullTarget.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
                    !fullTarget.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                {
                    fullTarget += Path.DirectorySeparatorChar;
                }

                long lastReportTicks = 0;
                double lastReportedPct = -1.0;
                long reportIntervalTicks = Stopwatch.Frequency / 10; // Throttle to max 10 Hz (100ms)

                void ReportCurrentProgress(double pct, bool force = false)
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    bool isCompletion = pct >= 100.0 && lastReportedPct < 100.0;
                    bool hasMinDelta = lastReportedPct < 0.0 || Math.Abs(pct - lastReportedPct) >= 0.1;
                    if ((force && Math.Abs(pct - lastReportedPct) > 0.0001) || isCompletion || (nowTicks - lastReportTicks >= reportIntervalTicks && hasMinDelta))
                    {
                        lastReportTicks = nowTicks;
                        lastReportedPct = pct;
                        if (progressCallback != null)
                        {
                            progressCallback.Invoke(pct);
                        }
                        else
                        {
                            string pctStr = pct.ToString("0.00");
                            statusCallback?.Invoke(Loc.Format("Status_ExtractingProgress", pctStr));
                        }
                    }
                }

                ReportCurrentProgress(0.0, force: true);

                // Use the checked archive reader for every format, including unencrypted ZIP.
                var readerOptions = new SharpCompress.Readers.ReaderOptions { Password = passwordToUse };
                using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archiveFilePath, readerOptions);

                if (archive == null)
                    return false;

                ValidateEncryptedArchiveLayout(archive);
                var validEntries = archive.Entries.Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key)).ToList();
                if (validEntries.Any(entry => !entry.IsComplete))
                    throw new EndOfStreamException("Incomplete archive entry.");
                var totalEntries = validEntries.Count;
                long totalBytes = 0;
                foreach (var entry in validEntries)
                {
                    try { if (entry.Size > 0) totalBytes += entry.Size; } catch { }
                }

                long requiredBytes = totalBytes > 0 ? totalBytes : (File.Exists(archiveFilePath) ? new FileInfo(archiveFilePath).Length : 0);
                if (requiredBytes > 0 && !HasSufficientDiskSpace(fullTarget, requiredBytes, out long availableFreeSpace, out long requiredWithBuffer))
                {
                    var reqFormatted = BytesToHumanReadableConverter.FormatBytes(requiredBytes);
                    var freeFormatted = BytesToHumanReadableConverter.FormatBytes(availableFreeSpace);
                    var errorMsg = Loc.Format("Status_ExtractionInsufficientDiskSpace", reqFormatted, freeFormatted);
                    AppLogger.Error($"[ArchiveExtractor] Insufficient disk space to extract '{archiveFilePath}' into '{fullTarget}'. Required: {reqFormatted} (with buffer: {BytesToHumanReadableConverter.FormatBytes(requiredWithBuffer)}), Available: {freeFormatted}.");
                    statusCallback?.Invoke(errorMsg);
                    return false;
                }

                long completedBytes = 0;
                int completedEntries = 0;
                int sharpChunkCount = 0;

                var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in archive.Entries.Where(entry => entry.IsDirectory && !string.IsNullOrEmpty(entry.Key)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (directory.Key is "." or "./" or ".\\") continue;
                    Directory.CreateDirectory(GetSafeArchiveDestination(fullTarget, directory.Key!));
                }

                foreach (var entry in validEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WaitIfPaused();
                    var destinationPath = GetSafeArchiveDestination(fullTarget, entry.Key!);
                    if (!destinations.Add(destinationPath)) throw new InvalidDataException("Duplicate archive destination.");
                    bool knownLength = isArchiveEncrypted || archive.Type is SharpCompress.Common.ArchiveType.Zip or
                        SharpCompress.Common.ArchiveType.Rar or SharpCompress.Common.ArchiveType.SevenZip or SharpCompress.Common.ArchiveType.Tar;
                    long declaredLength = entry.Size;

                    var entryDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
                    {
                        Directory.CreateDirectory(entryDir);
                    }

                    long entryExtractedBytes = 0;
                    try
                    {
                        using var outputStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite,
                            FileShare.None, 256 * 1024, FileOptions.SequentialScan);
                        using var controlledOutput = new ArchiveEntryOutputStream(outputStream, bytesWritten =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            WaitIfPaused();
                            if (knownLength && (declaredLength < 0 || bytesWritten > declaredLength - entryExtractedBytes))
                                throw new InvalidDataException("Archive entry exceeds its declared length.");
                            entryExtractedBytes += bytesWritten;
                            double pct = totalBytes > 0
                                ? (double)(completedBytes + entryExtractedBytes) / totalBytes * 100.0
                                : totalEntries > 0
                                    ? ((double)completedEntries + (entry.Size > 0 ? (double)entryExtractedBytes / entry.Size : 0)) / totalEntries * 100.0
                                    : 0;
                            ReportCurrentProgress(Math.Clamp(pct, 0, 100));
                            if (isLowResource && (++sharpChunkCount % 16 == 0)) System.Threading.Thread.Sleep(2);
                        });
                        // Use the library's format-aware checks, including formats without a CRC field.
                        entry.WriteTo(controlledOutput, new ExtractionOptions { CheckCrc = true, BufferSize = 256 * 1024 });
                        outputStream.Flush(flushToDisk: true);
                        controlledOutput.VerifyWrittenBytes(outputStream.SafeFileHandle, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
                        throw;
                    }

                    if (knownLength && entryExtractedBytes != declaredLength)
                        throw new InvalidDataException("Archive entry length mismatch.");

                    completedBytes += entry.Size > 0 ? entry.Size : entryExtractedBytes;
                    completedEntries++;

                    if (totalBytes > 0)
                    {
                        double pct = Math.Clamp((double)completedBytes / totalBytes * 100.0, 0.0, 100.0);
                        ReportCurrentProgress(pct);
                    }
                    else if (totalEntries > 0)
                    {
                        double pct = Math.Clamp((double)completedEntries / totalEntries * 100.0, 0.0, 100.0);
                        ReportCurrentProgress(pct);
                    }
                }

                ReportCurrentProgress(100.0, force: true);
                return true;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info($"[ArchiveExtractor] Extraction canceled for '{archiveFilePath}'.");
                statusCallback?.Invoke(Loc.Get("Status_Paused"));
                return false;
            }
            catch (UnsupportedEncryptedVolumesException ex)
            {
                failureCallback?.Invoke(ex);
                statusCallback?.Invoke(Loc.Get("Status_ExtractionUnsupportedEncryptedVolumes"));
                return false;
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException ||
                                       ex is SharpCompress.Common.CryptographicException)
            {
                failureCallback?.Invoke(ex);
                statusCallback?.Invoke(Loc.Get("Status_ExtractionPasswordProtected"));
                return false;
            }
            catch (SharpCompress.Common.ArchiveException aex) when (aex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                                                    aex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            {
                failureCallback?.Invoke(aex);
                statusCallback?.Invoke(Loc.Get("Status_ExtractionPasswordProtected"));
                return false;
            }
            catch (Exception ex)
            {
                failureCallback?.Invoke(ex);
                AppLogger.Warn("[ArchiveExtractor] Archive extraction failed.");
                var errorKind = ExtractionErrorClassifier.Classify(ex);
                statusCallback?.Invoke(ExtractionErrorClassifier.BuildStatusMessage(errorKind, ex, archiveFilePath));
                return false;
            }
            finally
            {
                if (isLowResource)
                {
                    try
                    {
                        System.Threading.Thread.CurrentThread.Priority = originalPriority;
                    }
                    catch { }
                }
            }
        }).ConfigureAwait(false);
        return result;
    }


    /// <summary>
    /// Checks whether a given file is a temporary, partial, sidecar, or discardable cache file.
    /// Such files must never be moved to the Windows Recycle Bin; they must be deleted directly
    /// or moved to the Windows Temp directory.
    /// </summary>
    public static bool IsTempFile(string filePath, bool allowTempDirectoryMatch = true)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            // 1. Files located directly within the standard Windows Temp directory root
            // (e.g. %TEMP%\tempfile.data, but not inside structured subdirectories where user packages might reside)
            if (allowTempDirectoryMatch)
            {
                var winTemp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(filePath);
                var fileDir = Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrWhiteSpace(fileDir) && string.Equals(fileDir, winTemp, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 2. Known temporary, partial, and sidecar extensions
            if (fileName.EndsWith(".part.segments", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".segments", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".reepax_tmp", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".cache", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 3. Patterns: temporary prefix (~, ~$), permission test files, or contains .tmp. / .temp.
            if (fileName.StartsWith("~") ||
                fileName.StartsWith("__perm_test_", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("Reepax_del_", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".temp.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Permanently deletes a temporary file directly without sending it to the Recycle Bin.
    /// If direct deletion fails (e.g. temporary file lock or sharing violation), moves the file
    /// to the standard Windows Temp directory (%TEMP%) and nowhere else.
    /// </summary>
    /// <returns>Whether the original path was removed or was already absent.</returns>
    public static bool DeleteOrMoveToTemp(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return true;

        try
        {
            File.Delete(filePath);
            return !File.Exists(filePath);
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            try
            {
                var winTemp = Path.GetTempPath();
                var fullPath = Path.GetFullPath(filePath);
                // If not already inside Windows Temp directory, move it there
                if (!fullPath.StartsWith(winTemp, StringComparison.OrdinalIgnoreCase))
                {
                    var destName = "Reepax_del_" + Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(filePath);
                    var destPath = Path.Combine(winTemp, destName);
                    File.Move(filePath, destPath, overwrite: true);
                    return !File.Exists(filePath);
                }
                return !File.Exists(filePath);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Recursively scans a directory and permanently deletes all temporary files
    /// (e.g. .part, .part.segments, .tmp, .temp, .reepax_tmp, etc.)
    /// directly from disk using DeleteOrMoveToTemp, ensuring they never end up in
    /// the Windows Recycle Bin.
    /// </summary>
    public static void PurgeTemporaryFilesInDirectory(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
            return;

        try
        {
            var dirInfo = new DirectoryInfo(dirPath);
            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (IsTempFile(file.FullName, allowTempDirectoryMatch: false))
                    {
                        if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                            file.Attributes &= ~FileAttributes.ReadOnly;

                        DeleteOrMoveToTemp(file.FullName);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Recursively removes all empty subdirectories in bottom-up order.
    /// </summary>
    public static void CleanEmptySubdirectories(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
            return;

        try
        {
            var dirInfo = new DirectoryInfo(dirPath);
            var subDirs = dirInfo.EnumerateDirectories("*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.FullName.Length)
                                 .ToList();

            foreach (var subDir in subDirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(subDir.FullName).Any())
                    {
                        if ((subDir.Attributes & FileAttributes.ReadOnly) != 0)
                            subDir.Attributes &= ~FileAttributes.ReadOnly;

                        subDir.Delete();
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public static void StripReadOnlyAttributes(string dirPath)
    {
        try
        {
            var dirInfo = new DirectoryInfo(dirPath);
            if ((dirInfo.Attributes & FileAttributes.ReadOnly) != 0)
                dirInfo.Attributes &= ~FileAttributes.ReadOnly;

            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                        file.Attributes &= ~FileAttributes.ReadOnly;
                }
                catch { }
            }

            foreach (var subDir in dirInfo.EnumerateDirectories("*", SearchOption.AllDirectories))
            {
                try
                {
                    if ((subDir.Attributes & FileAttributes.ReadOnly) != 0)
                        subDir.Attributes &= ~FileAttributes.ReadOnly;
                }
                catch { }
            }
        }
        catch { }
    }

}
