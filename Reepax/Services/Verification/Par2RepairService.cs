using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Verification;

public enum Par2VerificationStatus
{
    AllFilesIntact,
    RepairPossible,
    RepairNotPossible,
    NoPar2FilesFound,
    Failed
}

public class Par2VerifyResult
{
    public Par2VerificationStatus Status { get; set; } = Par2VerificationStatus.Failed;
    public int DamagedFilesCount { get; set; }
    public int MissingFilesCount { get; set; }
    public int OkFilesCount { get; set; }
    public int TotalDataBlocks { get; set; }
    public int AvailableDataBlocks { get; set; }
    public int AvailableRecoveryBlocks { get; set; }
    public int NeededRecoveryBlocks { get; set; }
    public string? RawOutput { get; set; }
    public string? ErrorMessage { get; set; }

    public bool IsRepairedOrIntact => Status == Par2VerificationStatus.AllFilesIntact;
}

public class Par2RepairResult
{
    public bool Success { get; set; }
    public int RepairedFilesCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RawOutput { get; set; }
}

public class Par2RepairService
{
    private static readonly Lazy<Par2RepairService> _instance = new(() => new Par2RepairService());
    public static Par2RepairService Instance => _instance.Value;

    private static readonly Regex _progressRegex = new(
        @"(?:Scanning|Processing|Repairing|Verifying|Constructing|Solving):\s*([\d\.]+)%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _neededBlocksRegex = new(
        @"You need\s+(\d+)\s+more recovery blocks",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _recoveryBlocksRegex = new(
        @"You have\s+(\d+)\s+recovery blocks available",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _dataBlocksRegex = new(
        @"You have\s+(\d+)\s+out of\s+(\d+)\s+data blocks available",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _damagedFilesRegex = new(
        @"(\d+)\s+file\(s\)\s+exist but are damaged",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _missingFilesRegex = new(
        @"(\d+)\s+file\(s\)\s+are missing",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _okFilesRegex = new(
        @"(\d+)\s+file\(s\)\s+are ok",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _volPar2Pattern = new(
        @"\.vol\d+(\+\d+)?\.par2$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private string? _cachedExecutablePath;

    /// <summary>
    /// Resolves the full path to the par2cmdline binary (par2.exe).
    /// Prioritizes the user settings directory (%LocalAppData%\Reepax\par2.exe),
    /// automatically deploying from bundled binaries if not yet present in AppData.
    /// </summary>
    public string? ResolvePar2ExecutablePath()
    {
        if (!string.IsNullOrEmpty(_cachedExecutablePath) && File.Exists(_cachedExecutablePath))
            return _cachedExecutablePath;

        // 1. User settings / AppData directory (%LocalAppData%\Reepax\par2.exe)
        var appDataPar2 = Path.Combine(SettingsService.AppDataDirectory, "par2.exe");
        if (File.Exists(appDataPar2))
        {
            _cachedExecutablePath = appDataPar2;
            return appDataPar2;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 2. Bundled fallback: If in application directory, deploy/copy to AppData where settings are stored
        var bundledPath = Path.Combine(baseDir, "par2.exe");
        if (!File.Exists(bundledPath))
        {
            bundledPath = Path.Combine(baseDir, "runtimes", "win-x64", "native", "par2.exe");
        }

        if (File.Exists(bundledPath))
        {
            try
            {
                Directory.CreateDirectory(SettingsService.AppDataDirectory);
                File.Copy(bundledPath, appDataPar2, overwrite: true);
                if (File.Exists(appDataPar2))
                {
                    _cachedExecutablePath = appDataPar2;
                    return appDataPar2;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Par2RepairService] Konnte par2.exe nicht nach AppData kopieren: {ex.Message}");
            }

            _cachedExecutablePath = bundledPath;
            return bundledPath;
        }

        // 3. Current working directory fallback
        var candidate3 = Path.Combine(Environment.CurrentDirectory, "par2.exe");
        if (File.Exists(candidate3))
        {
            _cachedExecutablePath = candidate3;
            return candidate3;
        }

        // 4. System PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), "par2.exe");
                    if (File.Exists(p))
                    {
                        _cachedExecutablePath = p;
                        return p;
                    }
                }
                catch { }
            }
        }

        return null;
    }

    public bool IsPar2Available => !string.IsNullOrEmpty(ResolvePar2ExecutablePath());

    public bool IsPar2File(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return path.EndsWith(".par2", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the primary index par2 file (e.g. 'archive.par2' rather than 'archive.vol01+02.par2').
    /// If no primary index exists, returns the first volume par2 found.
    /// </summary>
    public string? FindPrimaryPar2File(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        var par2Files = Directory.GetFiles(directory, "*.par2", SearchOption.TopDirectoryOnly);
        if (par2Files.Length == 0)
            return null;

        // Prefer main .par2 file without .volXX+YY pattern
        var mainIndex = par2Files.FirstOrDefault(f => !_volPar2Pattern.IsMatch(f));
        if (!string.IsNullOrEmpty(mainIndex))
            return mainIndex;

        return par2Files[0];
    }

    /// <summary>
    /// Checks whether a package contains PAR2 files either in its items or in its target save directory.
    /// </summary>
    public bool HasPar2Files(DownloadPackage package, out string? primaryPar2Path)
    {
        primaryPar2Path = null;
        if (package == null)
            return false;

        // 1. Check in save directory
        var saveDir = package.SaveDirectory;
        if (!string.IsNullOrWhiteSpace(saveDir) && Directory.Exists(saveDir))
        {
            var found = FindPrimaryPar2File(saveDir);
            if (!string.IsNullOrEmpty(found))
            {
                primaryPar2Path = found;
                return true;
            }
        }

        // 2. Check in package items (physical files on disk)
        var par2PhysicalItems = package.Items
            .Where(i => i.IsEnabled && !string.IsNullOrWhiteSpace(i.SaveFilePath) && IsPar2File(i.SaveFilePath) && File.Exists(i.SaveFilePath))
            .Select(i => i.SaveFilePath!)
            .ToList();

        if (par2PhysicalItems.Count > 0)
        {
            var mainIndex = par2PhysicalItems.FirstOrDefault(f => !_volPar2Pattern.IsMatch(f)) ?? par2PhysicalItems[0];
            primaryPar2Path = mainIndex;
            return true;
        }

        // 3. Check in package items by declared name or path (even before files are downloaded to disk)
        var par2Items = package.Items
            .Where(i => i.IsEnabled && (IsPar2File(i.FileName) || IsPar2File(i.SaveFilePath)))
            .ToList();

        if (par2Items.Count > 0)
        {
            var mainItem = par2Items.FirstOrDefault(i => !string.IsNullOrEmpty(i.FileName) && !_volPar2Pattern.IsMatch(i.FileName)) ?? par2Items[0];
            primaryPar2Path = mainItem.SaveFilePath ?? mainItem.FileName;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Verifies files against a PAR2 set using 'par2 v'.
    /// </summary>
    public async Task<Par2VerifyResult> VerifyAsync(
        string par2FilePath,
        Action<string>? statusCallback = null,
        Action<double>? progressCallback = null,
        CancellationToken ct = default)
    {
        var exePath = ResolvePar2ExecutablePath();
        if (string.IsNullOrEmpty(exePath))
        {
            return new Par2VerifyResult
            {
                Status = Par2VerificationStatus.Failed,
                ErrorMessage = "par2.exe konnte nicht gefunden werden."
            };
        }

        if (!File.Exists(par2FilePath))
        {
            return new Par2VerifyResult
            {
                Status = Par2VerificationStatus.NoPar2FilesFound,
                ErrorMessage = $"PAR2-Datei '{par2FilePath}' existiert nicht."
            };
        }

        var workingDir = Path.GetDirectoryName(par2FilePath) ?? Environment.CurrentDirectory;
        var fileName = Path.GetFileName(par2FilePath);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"v -q \"{fileName}\"",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var outputBuilder = new StringBuilder();
        var result = new Par2VerifyResult();

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (outputBuilder)
                {
                    outputBuilder.AppendLine(e.Data);
                }

                ParseProgressAndStatus(e.Data, statusCallback, progressCallback);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (outputBuilder)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            }))
            {
                await process.WaitForExitAsync(ct);
            }

            var output = outputBuilder.ToString();
            result.RawOutput = output;

            ParseVerificationOutput(output, process.ExitCode, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new Par2VerifyResult
            {
                Status = Par2VerificationStatus.Failed,
                ErrorMessage = "PAR2-Überprüfung abgebrochen."
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Par2RepairService] Fehler bei PAR2-Verifikation von '{par2FilePath}'", ex);
            return new Par2VerifyResult
            {
                Status = Par2VerificationStatus.Failed,
                ErrorMessage = ex.Message,
                RawOutput = outputBuilder.ToString()
            };
        }
    }

    /// <summary>
    /// Repairs damaged files using available recovery packets via 'par2 r'.
    /// </summary>
    public async Task<Par2RepairResult> RepairAsync(
        string par2FilePath,
        bool purgeBackups = true,
        Action<string>? statusCallback = null,
        Action<double>? progressCallback = null,
        CancellationToken ct = default)
    {
        var exePath = ResolvePar2ExecutablePath();
        if (string.IsNullOrEmpty(exePath))
        {
            return new Par2RepairResult
            {
                Success = false,
                ErrorMessage = "par2.exe konnte nicht gefunden werden."
            };
        }

        if (!File.Exists(par2FilePath))
        {
            return new Par2RepairResult
            {
                Success = false,
                ErrorMessage = $"PAR2-Datei '{par2FilePath}' existiert nicht."
            };
        }

        var workingDir = Path.GetDirectoryName(par2FilePath) ?? Environment.CurrentDirectory;
        var fileName = Path.GetFileName(par2FilePath);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"r -q \"{fileName}\"",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var outputBuilder = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (outputBuilder)
                {
                    outputBuilder.AppendLine(e.Data);
                }

                ParseProgressAndStatus(e.Data, statusCallback, progressCallback);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (outputBuilder)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            }))
            {
                await process.WaitForExitAsync(ct);
            }

            var output = outputBuilder.ToString();
            bool success = process.ExitCode == 0 || output.Contains("Repair complete", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("All files are correct, repair is not required", StringComparison.OrdinalIgnoreCase);

            // Clean up temporary .1 backup files created by par2 if requested
            if (success && purgeBackups)
            {
                try
                {
                    foreach (var backupFile in Directory.GetFiles(workingDir, "*.1", SearchOption.TopDirectoryOnly))
                    {
                        Extractor.ArchiveExtractionService.DeleteOrMoveToTemp(backupFile);
                    }
                }
                catch { }
            }

            int repairedCount = 0;
            var matchDamaged = _damagedFilesRegex.Match(output);
            if (matchDamaged.Success && int.TryParse(matchDamaged.Groups[1].Value, out int count))
            {
                repairedCount = count;
            }

            return new Par2RepairResult
            {
                Success = success,
                RepairedFilesCount = repairedCount,
                RawOutput = output,
                ErrorMessage = success ? null : "Reparatur fehlgeschlagen oder unvollständig."
            };
        }
        catch (OperationCanceledException)
        {
            return new Par2RepairResult
            {
                Success = false,
                ErrorMessage = "PAR2-Reparatur abgebrochen."
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Par2RepairService] Fehler bei PAR2-Reparatur von '{par2FilePath}'", ex);
            return new Par2RepairResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                RawOutput = outputBuilder.ToString()
            };
        }
    }

    /// <summary>
    /// Processes PAR2 verification and necessary repair for a package before extraction.
    /// Returns true if files are intact (or successfully repaired), false if unrepairable.
    /// </summary>
    public async Task<bool> ProcessPackagePar2Async(
        DownloadPackage package,
        Action<string>? statusCallback = null,
        Action<double>? progressCallback = null,
        CancellationToken ct = default)
    {
        if (package == null)
            return true;

        if (!HasPar2Files(package, out var primaryPar2) || string.IsNullOrEmpty(primaryPar2))
        {
            // No PAR2 files available; nothing to verify/repair
            return true;
        }

        statusCallback?.Invoke(Loc.Get("Status_Par2Verifying"));

        // 1. Run Verification
        var verifyResult = await VerifyAsync(
            primaryPar2,
            pctText => statusCallback?.Invoke(pctText),
            pct => progressCallback?.Invoke(pct),
            ct);

        if (verifyResult.Status == Par2VerificationStatus.AllFilesIntact)
        {
            AppLogger.Info($"[Par2RepairService] Paket '{package.Name}': Alle Dateien sind intakt. Keine Reparatur nötig.");
            statusCallback?.Invoke(Loc.Get("Status_Par2Intact"));
            return true;
        }

        if (verifyResult.Status == Par2VerificationStatus.RepairPossible)
        {
            AppLogger.Info($"[Par2RepairService] Paket '{package.Name}': Beschädigte Dateien erkannt, Reparatur möglich. Starte Reparatur mit {verifyResult.AvailableRecoveryBlocks} Paritätsblöcken...");
            statusCallback?.Invoke(Loc.Format("Status_Par2Repairing", "0"));

            var repairResult = await RepairAsync(
                primaryPar2,
                purgeBackups: true,
                statusCallback,
                pct => progressCallback?.Invoke(pct),
                ct);

            if (repairResult.Success)
            {
                AppLogger.Info($"[Par2RepairService] Paket '{package.Name}': Reparatur erfolgreich abgeschlossen ({repairResult.RepairedFilesCount} Datei(en) repariert).");
                statusCallback?.Invoke(Loc.Get("Status_Par2Repaired"));
                return true;
            }
            else
            {
                AppLogger.Error($"[Par2RepairService] Paket '{package.Name}': Reparatur fehlgeschlagen: {repairResult.ErrorMessage}");
                statusCallback?.Invoke(Loc.Format("Status_Par2RepairFailed", repairResult.ErrorMessage ?? string.Empty));
                return false;
            }
        }

        if (verifyResult.Status == Par2VerificationStatus.RepairNotPossible)
        {
            var missing = verifyResult.NeededRecoveryBlocks > 0 ? verifyResult.NeededRecoveryBlocks : 1;
            AppLogger.Warn($"[Par2RepairService] Paket '{package.Name}': Reparatur nicht möglich. Es fehlen {missing} Wiederherstellungsblöcke.");
            statusCallback?.Invoke(Loc.Format("Status_Par2RepairFailedMissingBlocks", missing));
            return false;
        }

        // Failed verification
        AppLogger.Warn($"[Par2RepairService] Paket '{package.Name}': PAR2-Prüfung fehlgeschlagen: {verifyResult.ErrorMessage}");
        statusCallback?.Invoke(Loc.Format("Status_Par2VerifyFailed", verifyResult.ErrorMessage ?? string.Empty));
        return false;
    }

    public static void ParseVerificationOutput(string output, int exitCode, Par2VerifyResult result)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            result.Status = exitCode == 0 ? Par2VerificationStatus.AllFilesIntact : Par2VerificationStatus.Failed;
            return;
        }

        // Check overall outcome
        if (output.Contains("All files are correct, repair is not required", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = Par2VerificationStatus.AllFilesIntact;
        }
        else if (output.Contains("Repair is possible", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = Par2VerificationStatus.RepairPossible;
        }
        else if (output.Contains("Repair is not possible", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = Par2VerificationStatus.RepairNotPossible;
        }
        else if (exitCode == 0)
        {
            result.Status = Par2VerificationStatus.AllFilesIntact;
        }
        else
        {
            result.Status = Par2VerificationStatus.Failed;
        }

        // Parse missing blocks
        var matchNeeded = _neededBlocksRegex.Match(output);
        if (matchNeeded.Success && int.TryParse(matchNeeded.Groups[1].Value, out int needed))
        {
            result.NeededRecoveryBlocks = needed;
        }

        // Parse available recovery blocks
        var matchRecovery = _recoveryBlocksRegex.Match(output);
        if (matchRecovery.Success && int.TryParse(matchRecovery.Groups[1].Value, out int recovery))
        {
            result.AvailableRecoveryBlocks = recovery;
        }

        // Parse data blocks
        var matchData = _dataBlocksRegex.Match(output);
        if (matchData.Success)
        {
            if (int.TryParse(matchData.Groups[1].Value, out int avail)) result.AvailableDataBlocks = avail;
            if (int.TryParse(matchData.Groups[2].Value, out int total)) result.TotalDataBlocks = total;
        }

        // Parse file counts
        var matchDamaged = _damagedFilesRegex.Match(output);
        if (matchDamaged.Success && int.TryParse(matchDamaged.Groups[1].Value, out int damaged))
        {
            result.DamagedFilesCount = damaged;
        }

        var matchMissing = _missingFilesRegex.Match(output);
        if (matchMissing.Success && int.TryParse(matchMissing.Groups[1].Value, out int missing))
        {
            result.MissingFilesCount = missing;
        }

        var matchOk = _okFilesRegex.Match(output);
        if (matchOk.Success && int.TryParse(matchOk.Groups[1].Value, out int ok))
        {
            result.OkFilesCount = ok;
        }
    }

    private static void ParseProgressAndStatus(string line, Action<string>? statusCallback, Action<double>? progressCallback)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var match = _progressRegex.Match(line);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
        {
            progressCallback?.Invoke(pct);
            if (line.Contains("Repairing", StringComparison.OrdinalIgnoreCase))
            {
                statusCallback?.Invoke(Loc.Format("Status_Par2Repairing", pct.ToString("0.0")));
            }
            else if (line.Contains("Verifying", StringComparison.OrdinalIgnoreCase) || line.Contains("Scanning", StringComparison.OrdinalIgnoreCase))
            {
                statusCallback?.Invoke(Loc.Format("Status_Par2VerifyingProgress", pct.ToString("0.0")));
            }
        }
    }
}
