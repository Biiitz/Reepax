using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Reepax.Services.Extractor;

public partial class ArchiveExtractionService
{
    private static readonly SemaphoreSlim PasswordPromptGate = new(1, 1);
    internal static Func<string, bool, CancellationToken, Task<(string? Password, bool Remember)>>? PasswordRetryPromptHandler { get; set; }

    public Task<bool> ExtractArchiveAsync(
        string archiveFilePath,
        string targetDirectory,
        Action<string>? statusCallback = null,
        bool? lowResourceMode = null,
        Action<double>? progressCallback = null,
        string? explicitPassword = null,
        CancellationToken cancellationToken = default,
        ManualResetEventSlim? pauseEvent = null,
        Action<bool>? pauseStateChanged = null,
        Action<string>? passwordResolved = null)
    {
        return Task.Run(async () =>
        {
            Exception? lastFailure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(archiveFilePath)) return false;
                var missingVolume = ExtractionErrorClassifier.FindMissingVolume(archiveFilePath);
                if (!string.IsNullOrEmpty(missingVolume))
                {
                    statusCallback?.Invoke(ExtractionErrorClassifier.BuildMissingVolumeStatus(missingVolume));
                    return false;
                }

                void WaitForProbe()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pauseEvent?.IsSet == false)
                    {
                        pauseStateChanged?.Invoke(true);
                        statusCallback?.Invoke(Loc.Get("Status_ExtractionPaused"));
                        pauseEvent.Wait(cancellationToken);
                        pauseStateChanged?.Invoke(false);
                        statusCallback?.Invoke(Loc.Get("Status_Extracting"));
                    }
                }

                bool initialProbe = ProbeArchivePassword(archiveFilePath, null, WaitForProbe,
                    out bool encrypted, out var initialFailure);
                if (initialFailure is UnsupportedEncryptedVolumesException)
                {
                    ReportFailure(initialFailure);
                    return false;
                }
                if (initialProbe && !encrypted)
                {
                    using var workspace = new PackageExtractionWorkspace(new[] { targetDirectory });
                    bool success = await ExtractArchiveCoreAsync(archiveFilePath, workspace.GetStagingDirectory(targetDirectory), statusCallback,
                        lowResourceMode, progressCallback == null ? null : pct => progressCallback(Math.Min(pct, 99.0)),
                        cancellationToken: cancellationToken, pauseEvent: pauseEvent, pauseStateChanged: pauseStateChanged).ConfigureAwait(false);
                    if (success)
                    {
                        WaitForProbe();
                        using (await PackagePathOperationGate.Instance.EnterAsync(cancellationToken).ConfigureAwait(false))
                            workspace.Publish(GetArchiveSourceFiles(archiveFilePath), cancellationToken);
                        progressCallback?.Invoke(100);
                        statusCallback?.Invoke(Loc.Get("Status_ExtractionCompleted"));
                    }
                    return success;
                }
                if (!encrypted && initialFailure != null &&
                    ExtractionErrorClassifier.Classify(initialFailure) != ExtractionErrorKind.WrongPassword)
                {
                    ReportFailure(initialFailure);
                    return false;
                }

                var candidates = new Queue<string>();
                if (!string.IsNullOrEmpty(explicitPassword)) candidates.Enqueue(explicitPassword);
                foreach (var entry in SettingsService.Instance.GetArchivePasswordEntries()
                    .OrderByDescending(entry => entry.LastSuccessAt).ThenByDescending(entry => entry.SuccessCount))
                    candidates.Enqueue(entry.Password);
                var tried = new HashSet<string>(StringComparer.Ordinal);
                bool retryPrompt = false;
                while (true)
                {
                    WaitForProbe();
                    bool remember = false;
                    string? password;
                    if (candidates.Count > 0)
                    {
                        password = candidates.Dequeue();
                    }
                    else
                    {
                        if (PasswordPromptHandler == null && PasswordRetryPromptHandler == null)
                        {
                            ReportFailure(lastFailure);
                            return false;
                        }
                        await PasswordPromptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            var response = PasswordPromptHandler != null
                                ? PasswordPromptHandler(Path.GetFileName(archiveFilePath))
                                : PasswordRetryPromptHandler!(Path.GetFileName(archiveFilePath), retryPrompt, cancellationToken);
                            (password, remember) = await response.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        finally { PasswordPromptGate.Release(); }
                        if (string.IsNullOrEmpty(password))
                        {
                            ReportFailure(lastFailure);
                            return false;
                        }
                        retryPrompt = true;
                    }
                    if (!tried.Add(password)) continue;

                    var attempt = SettingsService.Instance.RecordArchivePasswordAttempt(password);
                    if (!ProbeArchivePassword(archiveFilePath, password, WaitForProbe, out _, out var probeFailure))
                    {
                        if (probeFailure is UnsupportedEncryptedVolumesException)
                        {
                            ReportFailure(probeFailure);
                            return false;
                        }
                        lastFailure = probeFailure;
                        continue;
                    }

                    // Password probes are provisional; only verified output may reach the destination.
                    using var workspace = new PackageExtractionWorkspace(new[] { targetDirectory });
                    Exception? extractionFailure = null;
                    string? extractionStatus = null;
                    bool extracted = await ExtractArchiveCoreAsync(archiveFilePath, workspace.GetStagingDirectory(targetDirectory),
                        message =>
                        {
                            extractionStatus = message;
                            if (!ExtractionErrorClassifier.IsExtractionErrorStatus(message)) statusCallback?.Invoke(message);
                        }, lowResourceMode,
                        progressCallback == null ? null : pct => progressCallback(Math.Min(pct, 99.0)),
                        password, cancellationToken, pauseEvent, pauseStateChanged, isArchiveEncrypted: true,
                        failureCallback: failure => extractionFailure = failure).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!extracted)
                    {
                        if (extractionFailure is UnsupportedEncryptedVolumesException)
                        {
                            ReportFailure(extractionFailure);
                            return false;
                        }
                        lastFailure = extractionFailure;
                        if (extractionFailure != null && ExtractionErrorClassifier.Classify(extractionFailure) is
                            ExtractionErrorKind.WrongPassword or ExtractionErrorKind.CorruptArchive)
                            continue;
                        if (extractionStatus != null) statusCallback?.Invoke(extractionStatus);
                        return false;
                    }
                    WaitForProbe();
                    using (await PackagePathOperationGate.Instance.EnterAsync(cancellationToken).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        workspace.Publish(GetArchiveSourceFiles(archiveFilePath), cancellationToken);
                    }
                    if (remember) SettingsService.Instance.RememberArchivePassword(password);
                    if (attempt == null && remember)
                        attempt = SettingsService.Instance.RecordArchivePasswordAttempt(password);
                    if (attempt != null) SettingsService.Instance.RecordArchivePasswordSuccess(attempt);
                    passwordResolved?.Invoke(password);
                    progressCallback?.Invoke(100);
                    statusCallback?.Invoke(Loc.Get("Status_ExtractionCompleted"));
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                statusCallback?.Invoke(Loc.Get("Status_Paused"));
                return false;
            }
            catch (Exception failure)
            {
                AppLogger.Warn("[ArchiveExtractor] Password-protected extraction failed.");
                ReportFailure(failure);
                return false;
            }

            void ReportFailure(Exception? failure)
            {
                statusCallback?.Invoke(failure is UnsupportedEncryptedVolumesException
                    ? Loc.Get("Status_ExtractionUnsupportedEncryptedVolumes")
                    : failure == null
                    ? Loc.Get("Status_ExtractionPasswordProtected")
                    : ExtractionErrorClassifier.BuildStatusMessage(ExtractionErrorClassifier.Classify(failure), failure, archiveFilePath));
            }
        });
    }

    /// <summary>Probes a password; complete extraction remains necessary to verify every entry.</summary>
    public static bool TryTestArchivePassword(string archiveFilePath, string? password, out bool isEncrypted) =>
        TryTestArchivePassword(archiveFilePath, password, out isEncrypted, out _);

    public static bool TryTestArchivePassword(string archiveFilePath, string? password, out bool isEncrypted,
        out Exception? probeFailure) => ProbeArchivePassword(archiveFilePath, password, null, out isEncrypted, out probeFailure);

    private static bool ProbeArchivePassword(string archiveFilePath, string? password, Action? checkpoint,
        out bool isEncrypted, out Exception? probeFailure)
    {
        isEncrypted = false;
        probeFailure = null;
        try
        {
            checkpoint?.Invoke();
            using var archive = ArchiveFactory.OpenArchive(archiveFilePath, new ReaderOptions { Password = password });
            var entries = archive.Entries.ToList();
            var encryptedEntry = entries.Where(entry => !entry.IsDirectory && entry.IsEncrypted)
                .OrderBy(entry => entry.IsSolid ? long.MaxValue : entry.Size).FirstOrDefault();
            isEncrypted = encryptedEntry != null;
            ValidateEncryptedArchiveLayout(archive);
            if (encryptedEntry == null) return true;
            if (string.IsNullOrEmpty(password)) return false;
            if (!encryptedEntry.IsComplete) throw new EndOfStreamException("Incomplete archive entry.");
            if (encryptedEntry.Size <= 1024 * 1024 && !encryptedEntry.IsSolid)
            {
                using var sink = new ArchiveEntryOutputStream(Stream.Null, _ => checkpoint?.Invoke());
                encryptedEntry.WriteTo(sink, new ExtractionOptions { CheckCrc = true });
            }
            else
            {
                using var stream = encryptedEntry.OpenEntryStream();
                byte[] sample = new byte[64 * 1024];
                int remaining = sample.Length;
                while (remaining > 0)
                {
                    checkpoint?.Invoke();
                    int read = stream.Read(sample, 0, remaining);
                    if (read == 0) break;
                    remaining -= read;
                }
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure)
        {
            if (ExtractionErrorClassifier.Classify(failure) == ExtractionErrorKind.WrongPassword) isEncrypted = true;
            probeFailure = failure;
            return false;
        }
    }

    private static void ValidateEncryptedArchiveLayout(IArchive archive)
    {
        // Older encrypted split entries remain blocked until their decoder paths are verified.
        if (archive.Type == ArchiveType.Rar && archive.Entries.Any(entry => !entry.IsDirectory && entry.IsEncrypted &&
            (entry.IsSplitAfter || entry.VolumeIndexFirst != entry.VolumeIndexLast) &&
            (entry is not SharpCompress.Archives.Rar.RarArchiveEntry rar || !rar.IsRar5)))
            throw new UnsupportedEncryptedVolumesException();
    }

    private sealed class UnsupportedEncryptedVolumesException : NotSupportedException
    {
        internal UnsupportedEncryptedVolumesException() : base("Encrypted split entries cannot be extracted safely.") { }
    }
}
