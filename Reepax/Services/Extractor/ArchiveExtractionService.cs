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
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

public class ArchiveExtractionService
{
    private static readonly Lazy<ArchiveExtractionService> _instance = new(() => new ArchiveExtractionService());
    public static ArchiveExtractionService Instance => _instance.Value;

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

    public bool IsPackageExtracting(Guid packageId) => _extractingPackages.ContainsKey(packageId);

    public async Task CheckAndExtractPackageAsync(DownloadPackage package)
    {
        if (package == null)
            return;

        // 1. Strict Check: If auto-extract is turned off for this package, do NOTHING!
        if (!package.AutoExtractArchives)
        {
            AppLogger.Info($"[ArchiveExtractor] Auto-extract ist für Paket '{package.Name}' deaktiviert. Überspringe.");
            return;
        }

        // 2. Concurrency guard: Prevent multiple threads from extracting the same package simultaneously
        if (!_extractingPackages.TryAdd(package.Id, true))
        {
            AppLogger.Info($"[ArchiveExtractor] Paket '{package.Name}' wird bereits entpackt.");
            return;
        }

        try
        {
            // 3. Strict Package Completeness Verification:
            // All enabled items in the package MUST be completely finished with status DownloadStatus.Completed.
            // If even a single item is pending, downloading, queued, solving captcha or failed, DO NOT EXTRACT!
            var enabledItems = package.Items.Where(i => i.IsEnabled).ToList();
            if (enabledItems.Count == 0)
                return;

            var pendingCount = enabledItems.Count(i => i.Status != DownloadStatus.Completed);
            if (pendingCount > 0)
            {
                AppLogger.Info($"[ArchiveExtractor] Paket '{package.Name}' wartet noch auf {pendingCount} unvollständige Datei(en) ({enabledItems.Count - pendingCount}/{enabledItems.Count} abgeschlossen).");
                return;
            }

            // 4. Strict File Presence and Non-Empty Verification:
            // Every enabled item's destination file must physically exist on disk and have a valid file size (> 0 bytes).
            foreach (var item in enabledItems)
            {
                if (string.IsNullOrWhiteSpace(item.SaveFilePath) || !File.Exists(item.SaveFilePath))
                {
                    AppLogger.Warn($"[ArchiveExtractor] Paket '{package.Name}' kann noch nicht entpackt werden: Datei '{item.SaveFilePath}' ist noch nicht auf der Festplatte vorhanden.");
                    return;
                }

                var fileInfo = new FileInfo(item.SaveFilePath);
                if (fileInfo.Length == 0)
                {
                    AppLogger.Warn($"[ArchiveExtractor] Paket '{package.Name}' kann noch nicht entpackt werden: Datei '{item.SaveFilePath}' hat 0 Bytes.");
                    return;
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
                return;
            }

            // 6. Give a small buffer (150ms) for background download file handles to be completely released
            await Task.Delay(150);

            SafeInvoke(() =>
            {
                package.ResetNextTasks();
            });

            // 6b. Automatic PAR2 verification and repair before extraction
            if (package.AutoPar2Repair && SettingsService.Instance.Settings.AutoPar2Repair)
            {
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
                        return;
                    }

                    SafeInvoke(() => package.SetNextTaskDone("Par2"));
                }
            }

            SafeInvoke(() =>
            {
                package.SetNextTaskRunning("Extract");
                package.StatusMessage = Loc.Get("Status_Extracting");
            });
            bool allSuccess = true;
            var successfullyExtractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int archiveIndex = 0; archiveIndex < primaryArchives.Count; archiveIndex++)
            {
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

                    int currentIdx = archiveIndex;
                    var success = await ExtractArchiveAsync(
                        archivePath, 
                        targetDir, 
                        msg =>
                        {
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
                catch (Exception ex)
                {
                    AppLogger.Error($"[ArchiveExtractor] Extraction failed for {archivePath}", ex);
                    allSuccess = false;
                }
            }

            SafeInvoke(() => package.SetNextTaskDone("Extract"));

            if (allSuccess)
            {
                SafeInvoke(() => package.StatusMessage = Loc.Get("Status_CompletedAndExtracted"));
            }
            else
            {
                SafeInvoke(() => package.StatusMessage = Loc.Get("Status_CompletedExtractionError"));
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
                await Task.Delay(cooldownMs);

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
        finally
        {
            _extractingPackages.TryRemove(package.Id, out _);
            SafeInvoke(() => package.CheckAndRefreshVerifyBatFile());
        }
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

    public Task<bool> ExtractArchiveAsync(
        string archiveFilePath, 
        string targetDirectory, 
        Action<string>? statusCallback = null,
        bool? lowResourceMode = null,
        Action<double>? progressCallback = null)
    {
        return Task.Run(() =>
        {
            var originalPriority = System.Threading.Thread.CurrentThread.Priority;
            var isLowResource = lowResourceMode
                ?? DriveHardwareDetector.IsLowResourceRecommended(targetDirectory);

            try
            {
                if (!File.Exists(archiveFilePath))
                    return false;

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

                // If standard ZIP, use fast System.IO.Compression with safe entry extraction
                if (archiveFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zipArchive = System.IO.Compression.ZipFile.OpenRead(archiveFilePath);
                    var validZipEntries = zipArchive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                    var totalEntriesZip = validZipEntries.Count;
                    long totalBytesZip = 0;
                    foreach (var entry in validZipEntries)
                    {
                        try { if (entry.Length > 0) totalBytesZip += entry.Length; } catch { }
                    }

                    long completedBytesZip = 0;
                    int completedEntriesZip = 0;
                    byte[] buffer = new byte[81920];
                    int chunkCount = 0;

                    foreach (var entry in validZipEntries)
                    {
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
                        using (var entryStream = entry.Open())
                        using (var outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            int bytesRead;
                            while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
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

                // SharpCompress for RAR, 7Z, TAR, GZ and multi-part archives
                using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archiveFilePath);

                if (archive == null)
                    return false;

                var validEntries = archive.Entries.Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key)).ToList();
                var totalEntries = validEntries.Count;
                long totalBytes = 0;
                foreach (var entry in validEntries)
                {
                    try { if (entry.Size > 0) totalBytes += entry.Size; } catch { }
                }

                long completedBytes = 0;
                int completedEntries = 0;
                byte[] sharpBuffer = new byte[81920];
                int sharpChunkCount = 0;

                foreach (var entry in validEntries)
                {
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
                    using (var entryStream = entry.OpenEntryStream())
                    using (var outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        int bytesRead;
                        while ((bytesRead = entryStream.Read(sharpBuffer, 0, sharpBuffer.Length)) > 0)
                        {
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
            catch (CryptographicException)
            {
                statusCallback?.Invoke(Loc.Get("Status_ExtractionPasswordProtected"));
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[ArchiveExtractor] Error during extraction: {archiveFilePath}", ex);
                statusCallback?.Invoke(Loc.Format("Status_ExtractionFailed", ex.Message));
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
    public static bool IsTempFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            // 1. Files located within the standard Windows Temp directory
            var winTemp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(filePath);
            if (fullPath.StartsWith(winTemp, StringComparison.OrdinalIgnoreCase))
                return true;

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
                fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 3. Patterns: temporary prefix (~, ~$), permission test files, or contains .tmp. / .temp.
            if (fileName.StartsWith("~") ||
                fileName.StartsWith("__perm_test_", StringComparison.OrdinalIgnoreCase) ||
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

    public static void DeleteToRecycleBin(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        try
        {
            // 0-Byte-Dateien oder temporäre Dateien (.part, .segments, .tmp, .bak etc.)
            // enthalten keinerlei wiederherstellbaren Inhalt und gehören niemals in den
            // Windows-Papierkorb. Sie werden direkt rückstandslos gelöscht oder bei
            // Zugriffskonflikten in den Windows-Temp-Ordner verschoben.
            if (IsTempFile(filePath))
            {
                DeleteOrMoveToTemp(filePath);
                return;
            }

            var fi = new FileInfo(filePath);
            if (fi.Length == 0)
            {
                DeleteOrMoveToTemp(filePath);
                return;
            }

            var fullPath = Path.GetFullPath(filePath);
            var shf = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = fullPath + '\0' + '\0',
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
            };
            int result = SHFileOperation(ref shf);
            if (result != 0)
            {
                DeleteOrMoveToTemp(filePath);
            }
        }
        catch
        {
            DeleteOrMoveToTemp(filePath);
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
