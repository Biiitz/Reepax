using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

public partial class ArchiveExtractionService
{
    private static string GetPackageExtractionTarget(DownloadPackage package, string archivePath)
    {
        string target = package.SaveDirectory;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            target = Path.GetDirectoryName(archivePath) ?? target;
        if (UpdateDetector.IsUpdatePackage(package) || UpdateDetector.IsUpdate(Path.GetFileName(archivePath)))
        {
            string subfolder = UpdateDetector.GetExtractionSubfolder(archivePath);
            if (!string.IsNullOrWhiteSpace(subfolder) && !string.Equals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(target)), subfolder, StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(target, subfolder);
        }
        return target;
    }

    private bool PackageArchiveSourcesUnchanged(DownloadPackage package, string[] paths)
    {
        foreach (string path in paths)
        {
            string stamp = BuildArchiveSourceStamp(path);
            lock (package.ArchivePasswordState.Sync)
                if (!package.ArchivePasswordState.Archives.TryGetValue(GetPackageArchiveKey(package, path), out var check) || check.SourceStamp != stamp)
                    return false;
        }
        return true;
    }

    private async Task<PreparedPackageArchive?> ExtractPreparedPackageArchiveAsync(
        DownloadPackage package, PreparedPackageArchive prepared, string target,
        Action<string> status, Action<double> progress, CancellationToken token, ManualResetEventSlim pauseEvent,
        SystemIntegration.PowerManagementService.KeepAwakeScope keepAwake)
    {
        void Checkpoint()
        {
            token.ThrowIfCancellationRequested();
            if (pauseEvent.IsSet) return;
            keepAwake.Suspend();
            SafeInvoke(() => package.IsExtractionPaused = true);
            pauseEvent.Wait(token);
            keepAwake.Resume();
            SafeInvoke(() => package.IsExtractionPaused = false);
        }
        while (true)
        {
            Checkpoint();
            using var workspace = new ArchiveExtractionWorkspace(target);
            Exception? failure = null;
            string? failureStatus = null;
            bool success = await ExtractArchiveCoreAsync(prepared.Path, workspace.OutputDirectory,
                message =>
                {
                    if (ExtractionErrorClassifier.IsExtractionErrorStatus(message)) failureStatus = message;
                    else status(message);
                }, package.LowResourceExtraction, value => progress(Math.Min(value, 99.0)), prepared.Password,
                token, pauseEvent, value => SafeInvoke(() => package.IsExtractionPaused = value),
                isArchiveEncrypted: prepared.Check.IsEncrypted, failureCallback: error => failure = error,
                keepAwakeScope: keepAwake).ConfigureAwait(false);
            Checkpoint();
            if (success)
            {
                using (await PackagePathOperationGate.Instance.EnterAsync(token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    workspace.Publish();
                }
                return prepared;
            }
            if (!prepared.Check.IsEncrypted || failure is UnsupportedEncryptedVolumesException ||
                failure == null || ExtractionErrorClassifier.Classify(failure) is not
                    (ExtractionErrorKind.WrongPassword or ExtractionErrorKind.CorruptArchive))
            {
                status(failureStatus ?? BuildPasswordCheckFailure(prepared.Path, failure));
                return null;
            }
            lock (package.ArchivePasswordState.Sync)
            {
                prepared.Check.Trials[prepared.Password!].Outcome = ArchivePasswordOutcome.NotConfirmed;
                prepared.Check.Password = null;
                prepared.Check.Failure = failure;
            }
            var retry = await Task.Run(() => PreparePackagePasswords(package, new[] { prepared.Path }, Checkpoint), token).ConfigureAwait(false);
            if (!retry.Ready)
            {
                if (retry.FailureStatus != null) status(retry.FailureStatus);
                else SetPackagePasswordWaiting(package, unconfirmed: ExtractionErrorClassifier.Classify(failure) == ExtractionErrorKind.CorruptArchive);
                return null;
            }
            prepared = retry.Archives.Single();
        }
    }
}
