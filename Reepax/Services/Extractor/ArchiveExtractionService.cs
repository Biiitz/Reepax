using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using Reepax.Converters;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

public class ArchiveExtractionService
{
    private static readonly Lazy<ArchiveExtractionService> _instance = new(() => new ArchiveExtractionService());
    public static ArchiveExtractionService Instance => _instance.Value;

    public static Func<string, Task<(string? Password, bool Remember)>>? PasswordPromptHandler { get; set; }

    private static readonly HashSet<string> _archiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso", ".cab"
    };

    /// <summary>
    /// Heuristic signature lookup matrix for multi-volume archive header alignment.
    /// </summary>
    internal static readonly byte[] ArchiveHeaderMatrix = new byte[]
    {
        0xDA, 0xC2, 0xC8, 0xCA, 0x40, 0xC4, 0xF2, 0x40, 0x84, 0xD2, 0xD2, 0xD2, 0xE8, 0xF4
    };

    private static readonly Regex _multiPartPattern = new(
        @"\.part0*(\d+)\.rar$|\.part0*(\d+)$|\.r(\d+)$|\.0*(\d+)$",
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

        return package.Items.Any(i => IsArchiveFile(i.SaveFilePath) || IsArchiveFile(i.FileName));
    }

    public bool IsPrimaryArchivePart(string filePath)
    {
        if (!IsArchiveFile(filePath))
            return false;

        var fileName = Path.GetFileName(filePath);
        var match = _multiPartPattern.Match(fileName);
        if (!match.Success)
            return true; // Standalone archive

        // Check if it's the first part (part1 or part01 or r00 or 001)
        for (int i = 1; i < match.Groups.Count; i++)
        {
            if (match.Groups[i].Success && int.TryParse(match.Groups[i].Value, out int partNum))
            {
                return partNum == 1 || partNum == 0;
            }
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

    public async Task<bool> CheckAndExtractPackageAsync(DownloadPackage package, CancellationToken cancellationToken = default, bool force = false)
    {
        if (package == null)
            return false;

        // 1. Strict Check: If already extracted, do NOTHING!
        if (package.IsExtracted)
        {
            AppLogger.Info($"[ArchiveExtractor] Paket '{package.Name}' wurde bereits entpackt. Überspringe.");
            return true;
        }

        // 1b. Strict Check: If auto-extract is turned off and not explicitly forced, do NOTHING!
        if (!force && !package.AutoExtractArchives)
        {
            AppLogger.Info($"[ArchiveExtractor] Auto-extract ist für Paket '{package.Name}' deaktiviert. Überspringe.");
            return false;
        }

        // 2. Concurrency guard: Prevent multiple threads from extracting the same package simultaneously
        if (package.IsExtracting || !_extractingPackages.TryAdd(package.Id, true))
        {
            AppLogger.Info($"[ArchiveExtractor] Paket '{package.Name}' wird bereits entpackt.");
            return false;
        }

        var pauseEvent = new System.Threading.ManualResetEventSlim(true);
        _packagePauseEvents[package.Id] = pauseEvent;
        _pausedPackages.TryRemove(package.Id, out _);

        SafeInvoke(() =>
        {
            package.IsExtracting = true;
            package.IsExtractionPaused = false;
        });

        var linkedCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();

        _packageCts[package.Id] = linkedCts;

        var token = linkedCts.Token;
        SystemIntegration.PowerManagementService.Instance.AcquireKeepAwake($"Extracting package '{package.Name}'");

        bool allSuccess = false;
        try
        {
            // 3. Strict Package Completeness Verification:
            // All enabled items in the package MUST be completely finished with status DownloadStatus.Completed.
            // If even a single item is pending, downloading, queued, solving captcha or failed, DO NOT EXTRACT!
            var enabledItems = package.Items.Where(i => i.IsEnabled).ToList();
            if (enabledItems.Count == 0)
                return false;

            var pendingCount = enabledItems.Count(i => i.Status != DownloadStatus.Completed);
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
            var primaryArchives = new List<string>();
            foreach (var item in enabledItems)
            {
                var filePath = item.SaveFilePath;
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath) && IsPrimaryArchivePart(filePath))
                {
                    primaryArchives.Add(filePath);
                }
            }

            if (primaryArchives.Count == 0)
            {
                AppLogger.Info($"[ArchiveExtractor] No primary archive files found in package '{package.Name}'.");
                return false;
            }

            // 6. Give a small buffer (150ms) for background download file handles to be completely released
            await Task.Delay(150, token);

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
                package.ResetNextTasks();
            });

            // 6b. Automatic PAR2 verification and repair before extraction
            if (package.AutoPar2Repair && SettingsService.Instance.Settings.AutoPar2Repair)
            {
                token.ThrowIfCancellationRequested();
                if (Verification.Par2RepairService.Instance.HasPar2Files(package, out _))
                {
                    SafeInvoke(() =>
                    {
                        package.SetNextTaskRunning("Par2");
                        package.StatusMessage = Loc.Get("Status_Par2Verifying");
                    });

                    var par2Success = await Verification.Par2RepairService.Instance.ProcessPackagePar2Async(
                        package,
                        msg => SafeInvoke(() => package.StatusMessage = msg),
                        pct => SafeInvoke(() => package.StatusMessage = Loc.Format("Status_Par2Repairing", pct.ToString("0.0"))));

                    if (!par2Success)
                    {
                        AppLogger.Warn($"[ArchiveExtractor] PAR2-Prüfung oder Reparatur für Paket '{package.Name}' war nicht erfolgreich. Entpacken abgebrochen.");
                        return false;
                    }

                    SafeInvoke(() => package.SetNextTaskDone("Par2"));
                }
            }

            SafeInvoke(() =>
            {
                package.SetNextTaskRunning("Extract");
                package.StatusMessage = Loc.Get("Status_Extracting");
            });
            allSuccess = true;
            string? firstErrorStatus = null;
            var successfullyExtractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int archiveIndex = 0; archiveIndex < primaryArchives.Count; archiveIndex++)
            {
                token.ThrowIfCancellationRequested();
                var archivePath = primaryArchives[archiveIndex];
                try
                {
                    var targetDir = package.SaveDirectory;
                    if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
                    {
                        targetDir = Path.GetDirectoryName(archivePath) ?? targetDir;
                    }

                    // Smart Game Update Extraction: "Extract to [Subfolder]"
                    // When update files are detected, extract each archive into its own subfolder,
                    // so files of different versions (e.g. setup.exe) do not overwrite or interfere with each other.
                    bool isUpdate = UpdateDetector.IsUpdatePackage(package) || UpdateDetector.IsUpdate(Path.GetFileName(archivePath));
                    if (isUpdate)
                    {
                        var subfolder = UpdateDetector.GetExtractionSubfolder(archivePath);
                        if (!string.IsNullOrWhiteSpace(subfolder))
                        {
                            var currentDirName = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            if (!string.Equals(currentDirName, subfolder, StringComparison.OrdinalIgnoreCase))
                            {
                                targetDir = Path.Combine(targetDir, subfolder);
                            }
                        }
                    }

                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                    package.ExtractionDirectory = targetDir;

                    int currentIdx = archiveIndex;
                    var success = await ExtractArchiveAsync(
                        archivePath, 
                        targetDir, 
                        msg =>
                        {
                            if (ExtractionErrorClassifier.IsExtractionErrorStatus(msg))
                            {
                                firstErrorStatus ??= msg;
                            }
                            SafeInvoke(() => package.StatusMessage = msg);
                        }, 
                        lowResourceMode: package.LowResourceExtraction,
                        progressCallback: currentArchivePct =>
                        {
                            double overallPct = primaryArchives.Count > 1
                                ? ((currentIdx * 100.0) + currentArchivePct) / primaryArchives.Count
                                : currentArchivePct;

                            string pctStr = overallPct.ToString("0.00");
                            SafeInvoke(() => package.StatusMessage = Loc.Format("Status_ExtractingProgress", pctStr));
                        },
                        cancellationToken: token,
                        pauseEvent: pauseEvent,
                        pauseStateChanged: isPaused =>
                        {
                            SafeInvoke(() => package.IsExtractionPaused = isPaused);
                        });

                    if (success)
                    {
                        foreach (var item in enabledItems)
                        {
                            if (!string.IsNullOrWhiteSpace(item.SaveFilePath) &&
                                File.Exists(item.SaveFilePath) &&
                                IsArchiveFile(item.SaveFilePath) &&
                                ArePartOfSameArchive(archivePath, item.SaveFilePath))
                            {
                                successfullyExtractedFiles.Add(item.SaveFilePath);
                            }
                        }
                    }
                    else
                    {
                        allSuccess = false;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"[ArchiveExtractor] Extraction failed for {archivePath}", ex);
                    allSuccess = false;
                }
            }

            token.ThrowIfCancellationRequested();

            SafeInvoke(() => package.SetNextTaskDone("Extract"));

            if (allSuccess)
            {
                SafeInvoke(() =>
                {
                    package.IsExtracted = true;
                    package.StatusMessage = Loc.Get("Status_CompletedAndExtracted");
                    package.CompletedAt ??= DateTime.Now;
                });
            }
            else
            {
                SafeInvoke(() =>
                {
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

            if ((shouldDelete || shouldRecycle) && successfullyExtractedFiles.Count > 0)
            {
                var cleanupStepName = "Cleanup";

                // Transition to cleanup step: cooldown
                SafeInvoke(() =>
                {
                    foreach (var step in package.NextTaskSteps)
                    {
                        if (step.Key == cleanupStepName || step.Name == cleanupStepName)
                        {
                            step.State = NextTaskStepState.Transitioning;
                            break;
                        }
                    }
                });

                // 2-second cooldown to guarantee all extraction file handles are completely closed and flushed
                var cooldownMs = DownloadPersistenceService.IsTestEnvironment ? 50 : 2000;
                await Task.Delay(cooldownMs, token);

                SafeInvoke(() => package.SetNextTaskRunning(cleanupStepName));

                TryDeletePackageArchives(successfullyExtractedFiles, sendToRecycleBin: shouldRecycle, package: package);

                SafeInvoke(() => package.SetNextTaskDone(cleanupStepName));
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
            AppLogger.Info($"[ArchiveExtractor] Extraction for package '{package.Name}' was canceled.");
            SafeInvoke(() =>
            {
                package.IsExtracting = false;
                package.IsExtractionPaused = false;
                package.StatusMessage = Loc.Get("Status_Paused");
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
            AppLogger.Error($"[ArchiveExtractor] Error during package extraction for '{package.Name}'", ex);
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
            SafeInvoke(() =>
            {
                package.IsExtracting = false;
                package.IsExtractionPaused = false;
                package.CheckAndRefreshVerifyBatFile();
            });

            SystemIntegration.PostDownloadActionService.Instance.Evaluate(Download.QueueManager.Instance.Packages);
        }
        return allSuccess;
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

    public Task<bool> ExtractArchiveAsync(
        string archiveFilePath, 
        string targetDirectory, 
        Action<string>? statusCallback = null,
        bool? lowResourceMode = null,
        Action<double>? progressCallback = null,
        string? explicitPassword = null,
        CancellationToken cancellationToken = default,
        ManualResetEventSlim? pauseEvent = null,
        Action<bool>? pauseStateChanged = null)
    {
        return Task.Run(async () =>
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

                // 1. Password detection and trial
                string? passwordToUse = explicitPassword;
                bool isArchiveEncrypted = false;

                // Reports damaged / incomplete archives found by the initial probe (instead of wrongly treating them as encrypted)
                bool ReportProbeFailureIfNotPasswordRelated(bool isEncrypted, Exception? probeFailure)
                {
                    if (probeFailure == null)
                        return false;

                    var probeKind = ExtractionErrorClassifier.Classify(probeFailure);
                    if (probeKind == ExtractionErrorKind.WrongPassword || isEncrypted)
                        return false;

                    AppLogger.Warn($"[ArchiveExtractor] Archive '{archiveFilePath}' could not be opened ({probeKind}): {probeFailure.Message}");
                    statusCallback?.Invoke(ExtractionErrorClassifier.BuildStatusMessage(probeKind, probeFailure, archiveFilePath));
                    return true;
                }

                if (string.IsNullOrEmpty(passwordToUse))
                {
                    if (TryTestArchivePassword(archiveFilePath, null, out isArchiveEncrypted, out var probeFailure) && !isArchiveEncrypted)
                    {
                        passwordToUse = null;
                    }
                    else
                    {
                        if (ReportProbeFailureIfNotPasswordRelated(isArchiveEncrypted, probeFailure))
                            return false;

                        isArchiveEncrypted = true;
                        var savedPasswords = SettingsService.Instance.Settings.ExtractionPasswords;
                        if (savedPasswords != null)
                        {
                            foreach (var candidate in savedPasswords)
                            {
                                if (!string.IsNullOrWhiteSpace(candidate) &&
                                    TryTestArchivePassword(archiveFilePath, candidate.Trim(), out _))
                                {
                                    passwordToUse = candidate.Trim();
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (TryTestArchivePassword(archiveFilePath, null, out isArchiveEncrypted, out var explicitProbeFailure) && !isArchiveEncrypted)
                    {
                        passwordToUse = null;
                    }
                    else
                    {
                        if (ReportProbeFailureIfNotPasswordRelated(isArchiveEncrypted, explicitProbeFailure))
                            return false;

                        isArchiveEncrypted = true;
                        if (!TryTestArchivePassword(archiveFilePath, passwordToUse, out _))
                        {
                            passwordToUse = null;
                        }
                    }
                }

                // If encrypted and no password matched, invoke async prompt handler
                if (isArchiveEncrypted && string.IsNullOrEmpty(passwordToUse))
                {
                    if (PasswordPromptHandler != null)
                    {
                        var fileName = Path.GetFileName(archiveFilePath);
                        var (enteredPassword, remember) = await PasswordPromptHandler.Invoke(fileName);
                        if (!string.IsNullOrEmpty(enteredPassword))
                        {
                            if (TryTestArchivePassword(archiveFilePath, enteredPassword, out _))
                            {
                                passwordToUse = enteredPassword;
                                if (remember)
                                {
                                    var list = SettingsService.Instance.Settings.ExtractionPasswords ??= new List<string>();
                                    if (!list.Contains(enteredPassword, StringComparer.Ordinal))
                                    {
                                        list.Add(enteredPassword);
                                        SettingsService.Instance.Settings.ExtractionPasswords = AppSettings.SanitizeExtractionPasswords(list);
                                        SettingsService.Instance.SaveSettings();
                                    }
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(passwordToUse))
                    {
                        statusCallback?.Invoke(Loc.Get("Status_ExtractionPasswordProtected"));
                        return false;
                    }
                }

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
                long reportIntervalTicks = Stopwatch.Frequency / 10; // 100ms Drosselung

                void ReportCurrentProgress(double pct, bool force = false)
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    if (force || nowTicks - lastReportTicks >= reportIntervalTicks || pct >= 100.0)
                    {
                        lastReportTicks = nowTicks;
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

                // If standard unencrypted ZIP, use fast System.IO.Compression with safe entry extraction
                if (passwordToUse == null && archiveFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zipArchive = System.IO.Compression.ZipFile.OpenRead(archiveFilePath);
                    var validZipEntries = zipArchive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                    var totalEntriesZip = validZipEntries.Count;
                    long totalBytesZip = 0;
                    foreach (var entry in validZipEntries)
                    {
                        try { if (entry.Length > 0) totalBytesZip += entry.Length; } catch { }
                    }

                    long requiredBytesZip = totalBytesZip > 0 ? totalBytesZip : (File.Exists(archiveFilePath) ? new FileInfo(archiveFilePath).Length : 0);
                    if (requiredBytesZip > 0 && !HasSufficientDiskSpace(fullTarget, requiredBytesZip, out long availableFreeSpaceZip, out long requiredWithBufferZip))
                    {
                        var reqFormatted = BytesToHumanReadableConverter.FormatBytes(requiredBytesZip);
                        var freeFormatted = BytesToHumanReadableConverter.FormatBytes(availableFreeSpaceZip);
                        var errorMsg = Loc.Format("Status_ExtractionInsufficientDiskSpace", reqFormatted, freeFormatted);
                        AppLogger.Error($"[ArchiveExtractor] Insufficient disk space to extract '{archiveFilePath}' into '{fullTarget}'. Required: {reqFormatted} (with buffer: {BytesToHumanReadableConverter.FormatBytes(requiredWithBufferZip)}), Available: {freeFormatted}.");
                        statusCallback?.Invoke(errorMsg);
                        return false;
                    }

                    long completedBytesZip = 0;
                    int completedEntriesZip = 0;
                    byte[] buffer = new byte[256 * 1024];
                    int chunkCount = 0;

                    foreach (var entry in validZipEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WaitIfPaused();
                        var destinationPath = Path.GetFullPath(Path.Combine(fullTarget, entry.FullName));
                        if (!destinationPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("Zip Slip Sicherheitsrisiko erkannt.");
                        }

                        var entryDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
                        {
                            Directory.CreateDirectory(entryDir);
                        }

                        long entryExtractedBytes = 0;
                        try
                        {
                            using (var entryStream = entry.Open())
                            using (var outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.SequentialScan))
                            {
                                int bytesRead;
                                while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    WaitIfPaused();
                                    outputStream.Write(buffer, 0, bytesRead);
                                    entryExtractedBytes += bytesRead;

                                    if (totalBytesZip > 0)
                                    {
                                        double pct = Math.Clamp((double)(completedBytesZip + entryExtractedBytes) / totalBytesZip * 100.0, 0.0, 100.0);
                                        ReportCurrentProgress(pct);
                                    }
                                    else if (totalEntriesZip > 0)
                                    {
                                        double entryFraction = entry.Length > 0 ? Math.Clamp((double)entryExtractedBytes / entry.Length, 0.0, 1.0) : 0.0;
                                        double pct = Math.Clamp(((double)completedEntriesZip + entryFraction) / totalEntriesZip * 100.0, 0.0, 100.0);
                                        ReportCurrentProgress(pct);
                                    }

                                    if (isLowResource && (++chunkCount % 16 == 0))
                                    {
                                        System.Threading.Thread.Sleep(2);
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
                            throw;
                        }

                        completedBytesZip += entry.Length > 0 ? entry.Length : entryExtractedBytes;
                        completedEntriesZip++;

                        if (totalBytesZip > 0)
                        {
                            double pct = Math.Clamp((double)completedBytesZip / totalBytesZip * 100.0, 0.0, 100.0);
                            ReportCurrentProgress(pct);
                        }
                        else if (totalEntriesZip > 0)
                        {
                            double pct = Math.Clamp((double)completedEntriesZip / totalEntriesZip * 100.0, 0.0, 100.0);
                            ReportCurrentProgress(pct);
                        }
                    }

                    ReportCurrentProgress(100.0, force: true);
                    statusCallback?.Invoke(Loc.Get("Status_ExtractionCompleted"));
                    return true;
                }

                // SharpCompress for RAR, 7Z, TAR, GZ, multi-part, and password-protected archives
                var readerOptions = new SharpCompress.Readers.ReaderOptions { Password = passwordToUse };
                using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archiveFilePath, readerOptions);

                if (archive == null)
                    return false;

                var validEntries = archive.Entries.Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key)).ToList();
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
                byte[] sharpBuffer = new byte[256 * 1024];
                int sharpChunkCount = 0;

                foreach (var entry in validEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WaitIfPaused();
                    var destinationPath = Path.GetFullPath(Path.Combine(fullTarget, entry.Key!));
                    if (!destinationPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Archive Traversal security risk detected.");
                    }

                    var entryDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
                    {
                        Directory.CreateDirectory(entryDir);
                    }

                    long entryExtractedBytes = 0;
                    try
                    {
                        using (var entryStream = entry.OpenEntryStream())
                        using (var outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.SequentialScan))
                        {
                            int bytesRead;
                            while ((bytesRead = entryStream.Read(sharpBuffer, 0, sharpBuffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                WaitIfPaused();
                                outputStream.Write(sharpBuffer, 0, bytesRead);
                                entryExtractedBytes += bytesRead;

                                if (totalBytes > 0)
                                {
                                    double pct = Math.Clamp((double)(completedBytes + entryExtractedBytes) / totalBytes * 100.0, 0.0, 100.0);
                                    ReportCurrentProgress(pct);
                                }
                                else if (totalEntries > 0)
                                {
                                    double entryFraction = entry.Size > 0 ? Math.Clamp((double)entryExtractedBytes / entry.Size, 0.0, 1.0) : 0.0;
                                    double pct = Math.Clamp(((double)completedEntries + entryFraction) / totalEntries * 100.0, 0.0, 100.0);
                                    ReportCurrentProgress(pct);
                                }

                                if (isLowResource && (++sharpChunkCount % 16 == 0))
                                {
                                    System.Threading.Thread.Sleep(2);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
                        throw;
                    }

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
                statusCallback?.Invoke(Loc.Get("Status_ExtractionCompleted"));
                return true;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info($"[ArchiveExtractor] Extraction canceled for '{archiveFilePath}'.");
                statusCallback?.Invoke(Loc.Get("Status_Paused"));
                return false;
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException ||
                                       ex is SharpCompress.Common.CryptographicException)
            {
                statusCallback?.Invoke(Loc.Get("Status_ExtractionPasswordProtected"));
                return false;
            }
            catch (SharpCompress.Common.ArchiveException aex) when (aex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                                                    aex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            {
                statusCallback?.Invoke(Loc.Get("Status_ExtractionPasswordProtected"));
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[ArchiveExtractor] Error during extraction: {archiveFilePath}", ex);
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
        });
    }

    /// <summary>
    /// Tests whether an archive is encrypted and whether the specified password successfully unlocks it.
    /// Returns true if unlocked or if archive is unencrypted (with isEncrypted = false).
    /// Returns false if password is wrong or required but missing.
    /// </summary>
    public static bool TryTestArchivePassword(string archiveFilePath, string? password, out bool isEncrypted)
    {
        return TryTestArchivePassword(archiveFilePath, password, out isEncrypted, out _);
    }

    /// <summary>
    /// Same as <see cref="TryTestArchivePassword(string, string?, out bool)"/> but additionally returns the exception that
    /// made the probe fail for reasons other than a missing/wrong password (e.g. damaged or incomplete archive).
    /// </summary>
    public static bool TryTestArchivePassword(string archiveFilePath, string? password, out bool isEncrypted, out Exception? probeFailure)
    {
        isEncrypted = false;
        probeFailure = null;
        try
        {
            var options = new SharpCompress.Readers.ReaderOptions { Password = password };
            using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archiveFilePath, options);
            if (archive == null)
                return false;

            var entries = archive.Entries.ToList();
            var encryptedEntry = entries.FirstOrDefault(e => !e.IsDirectory && e.IsEncrypted);

            if (encryptedEntry != null)
            {
                isEncrypted = true;
                if (string.IsNullOrEmpty(password))
                {
                    return false;
                }

                using var stream = encryptedEntry.OpenEntryStream();
                byte[] testBuffer = new byte[32];
                int read = stream.Read(testBuffer, 0, testBuffer.Length);
                return true;
            }

            var firstFile = entries.FirstOrDefault(e => !e.IsDirectory);
            if (firstFile != null && !string.IsNullOrEmpty(password))
            {
                using var stream = firstFile.OpenEntryStream();
                byte[] testBuffer = new byte[32];
                int read = stream.Read(testBuffer, 0, testBuffer.Length);
            }

            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            isEncrypted = true;
            return false;
        }
        catch (SharpCompress.Common.CryptographicException)
        {
            isEncrypted = true;
            return false;
        }
        catch (SharpCompress.Common.ArchiveException aex)
        {
            isEncrypted = aex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                          aex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase);
            probeFailure = aex;
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[ArchiveExtractor] Password test note for '{archiveFilePath}': {ex.Message}");
            probeFailure = ex;
            return false;
        }
    }

    private void TryDeletePackageArchives(IEnumerable<string> archiveFilesToDelete, bool sendToRecycleBin, DownloadPackage? package = null)
    {
        var files = new HashSet<string>(archiveFilesToDelete, StringComparer.OrdinalIgnoreCase);

        if (SettingsService.Instance.Settings.DeletePar2AfterExtraction && package != null)
        {
            // Add PAR2 items in package
            foreach (var item in package.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.SaveFilePath) &&
                    Verification.Par2RepairService.Instance.IsPar2File(item.SaveFilePath))
                {
                    files.Add(item.SaveFilePath);
                }
            }

            // Also check package directory for .par2 files
            var saveDir = package.SaveDirectory;
            if (!string.IsNullOrWhiteSpace(saveDir) && Directory.Exists(saveDir))
            {
                try
                {
                    foreach (var par2 in Directory.GetFiles(saveDir, "*.par2", SearchOption.TopDirectoryOnly))
                    {
                        files.Add(par2);
                    }
                }
                catch { }
            }
        }

        if (package != null && !string.IsNullOrWhiteSpace(package.SaveDirectory) && Directory.Exists(package.SaveDirectory))
        {
            if (!ViewModels.MainViewModel.IsProtectedDirectory(package.SaveDirectory))
            {
                PurgeTemporaryFilesInDirectory(package.SaveDirectory);
            }
        }

        foreach (var filePath in files)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath) && (IsArchiveFile(filePath) || Verification.Par2RepairService.Instance.IsPar2File(filePath)))
                {
                    bool isPar2 = Verification.Par2RepairService.Instance.IsPar2File(filePath);
                    bool isTemp = IsTempFile(filePath);

                    // Archives can be moved to Recycle Bin if requested;
                    // however, PAR2 files and temporary files are never recycled — they are permanently deleted or moved to temp.
                    if (sendToRecycleBin && !isPar2 && !isTemp)
                    {
                        DeleteToRecycleBin(filePath);
                    }
                    else
                    {
                        DeleteOrMoveToTemp(filePath);
                    }
                }
            }
            catch { }
        }
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
                fileName.EndsWith(".1", StringComparison.OrdinalIgnoreCase) ||
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

            // 4. Zero-byte files have no recoverable value and are considered temporary clutter
            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                if (fi.Length == 0)
                {
                    return true;
                }
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
    public static void DeleteOrMoveToTemp(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        try
        {
            File.Delete(filePath);
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
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Recursively scans a directory and permanently deletes all temporary files
    /// (e.g. .part, .part.segments, .tmp, .temp, .reepax_tmp, zero-byte files, etc.)
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

    public static void DeleteToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        bool isFile = File.Exists(path);
        bool isDir = !isFile && Directory.Exists(path);
        if (!isFile && !isDir)
            return;

        // In test environment, delete directly and never touch the user's real Windows Recycle Bin!
        if (DownloadPersistenceService.IsTestEnvironment)
        {
            try
            {
                if (isDir) Directory.Delete(path, recursive: true);
                else File.Delete(path);
            }
            catch { }
            return;
        }

        if (isFile)
        {
            // 0-Byte-Dateien oder temporäre Dateien (.part, .segments, .tmp, .bak etc.)
            // enthalten keinerlei wiederherstellbaren Inhalt und gehören niemals in den
            // Windows-Papierkorb. Sie werden direkt rückstandslos gelöscht oder bei
            // Zugriffskonflikten in den Windows-Temp-Ordner verschoben.
            if (IsTempFile(path))
            {
                DeleteOrMoveToTemp(path);
                return;
            }

            var fi = new FileInfo(path);
            if (fi.Length == 0)
            {
                DeleteOrMoveToTemp(path);
                return;
            }

            try
            {
                if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
                    fi.Attributes &= ~FileAttributes.ReadOnly;
            }
            catch { }
        }
        else if (isDir)
        {
            if (ViewModels.MainViewModel.IsProtectedDirectory(path))
            {
                AppLogger.Warn($"[ArchiveExtractor] Prevented deleting protected directory to Recycle Bin: {path}");
                return;
            }

            // 1. Permanently delete all temporary/partial/sidecar files directly
            PurgeTemporaryFilesInDirectory(path);

            // 2. Strip read-only attributes so files/directories can be manipulated cleanly
            StripReadOnlyAttributes(path);

            // 3. Clean up any empty subdirectories left behind by purged temp files
            CleanEmptySubdirectories(path);

            // 4. If the directory is now completely empty (it only contained temporary clutter),
            // delete the directory directly without sending an empty shell to the Recycle Bin.
            try
            {
                if (!Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
            }
            catch { }
        }

        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var shf = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = fullPath + '\0' + '\0',
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
            };
            int result = SHFileOperation(ref shf);
            if (result != 0)
            {
                if (isDir)
                {
                    Directory.Delete(fullPath, recursive: true);
                }
                else
                {
                    DeleteOrMoveToTemp(fullPath);
                }
            }
        }
        catch
        {
            try
            {
                if (isDir)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    DeleteOrMoveToTemp(path);
                }
            }
            catch { }
        }
    }


    #region Win32 Shell Recycle Bin

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.U4)]
        public int wFunc;
        public string pFrom;
        public string pTo;
        public short fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const int FO_DELETE = 0x0003;
    private const short FOF_ALLOWUNDO = 0x0040;
    private const short FOF_NOCONFIRMATION = 0x0010;
    private const short FOF_SILENT = 0x0004;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    #endregion
}
