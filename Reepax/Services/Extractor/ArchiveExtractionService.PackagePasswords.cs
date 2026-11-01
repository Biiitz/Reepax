using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

internal sealed record PreparedPackageArchive(string Path, string Key, string? Password, ArchivePasswordCheck Check);
internal sealed record PackageArchivePasswordTarget(string Key, string DisplayName);
internal sealed record PackagePasswordPreparation(PreparedPackageArchive[] Archives, bool PasswordsMissing, string? FailureStatus)
{
    internal bool Ready => !PasswordsMissing && FailureStatus == null;
}
internal sealed record ArchivePasswordCandidate(string Password, ArchivePasswordOutcome Outcome, bool IsSaved);

public partial class ArchiveExtractionService
{
    internal bool CanRetryPackagePasswords(DownloadPackage package)
    {
        long settingsRevision = SettingsService.Instance.ArchivePasswordListRevision;
        var state = package.ArchivePasswordState;
        lock (state.Sync)
            return !state.Cancelled && (!package.RequiresArchivePassword || state.Ready ||
                state.Revision != state.CheckedRevision || state.SettingsRevision != settingsRevision);
    }

    internal void AddPackageArchivePassword(DownloadPackage package, string archiveName, string password, bool remember, bool retry = false)
    {
        if (string.IsNullOrEmpty(password)) return;
        archiveName = ResolvePackageArchiveKey(package, archiveName);
        if (archiveName != "*" && !GetPackageArchivePasswordTargets(package).Any(target => target.Key.Equals(archiveName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Invalid archive identifier.", nameof(archiveName));
        var state = package.ArchivePasswordState;
        lock (state.Sync)
        {
            if (!state.ManualCandidates.TryGetValue(archiveName, out var passwords))
                state.ManualCandidates.Add(archiveName, passwords = new(StringComparer.Ordinal));
            passwords[password] = remember;
            state.LastInputArchive = archiveName;
            state.LastInputPassword = password;
            if (retry)
                foreach (var archive in state.Archives.Where(pair => archiveName == "*" || pair.Key.Equals(archiveName, StringComparison.OrdinalIgnoreCase)))
                    archive.Value.Trials.Remove(password);
            state.Revision++;
        }
        SafeInvoke(package.NotifyArchivePasswordStateChanged);
    }

    internal ArchivePasswordCandidate[] GetPackagePasswordCandidates(DownloadPackage package, string archiveName)
    {
        archiveName = ResolvePackageArchiveKey(package, archiveName);
        var saved = SettingsService.Instance.GetArchivePasswordEntries().Select(entry => entry.Password).ToArray();
        var state = package.ArchivePasswordState;
        lock (state.Sync)
        {
            var candidates = GetManualCandidates(state, archiveName).Concat(saved).Distinct(StringComparer.Ordinal);
            state.Archives.TryGetValue(archiveName, out var check);
            return candidates.Select(password => new ArchivePasswordCandidate(password,
                check != null && check.Trials.TryGetValue(password, out var trial) ? trial.Outcome : ArchivePasswordOutcome.NotTried,
                saved.Contains(password, StringComparer.Ordinal))).ToArray();
        }
    }

    internal string GetPackageArchivePasswordStatus(DownloadPackage package, string archiveName)
    {
        archiveName = ResolvePackageArchiveKey(package, archiveName);
        var state = package.ArchivePasswordState;
        lock (state.Sync)
        {
            if (!state.Archives.TryGetValue(archiveName, out var check) || !check.Inspected)
                return Loc.Get("ArchivePasswords_NotChecked");
            if (check.Failure != null && !check.IsEncrypted)
                return Loc.Get("ArchivePasswords_CheckFailed");
            if (check.Password != null && check.Trials.TryGetValue(check.Password, out var trial) && trial.Outcome == ArchivePasswordOutcome.Verified)
                return Loc.Get("ArchivePasswords_Verified");
            return Loc.Get(!check.IsEncrypted ? "ArchivePasswords_NotEncrypted" :
                check.Password != null ? "ArchivePasswords_Accepted" : "ArchivePasswords_Required");
        }
    }

    internal async Task<PackagePasswordPreparation> CheckPackagePasswordsAsync(DownloadPackage package, CancellationToken cancellationToken)
    {
        PackagePasswordPreparation result;
        using (await PackagePathOperationGate.Instance.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            if (package.IsExtracting || IsPackageExtracting(package.Id))
                throw new InvalidOperationException("The package is being extracted.");
            var archives = GetPrimaryArchives(package);
            if (archives.Count == 0 || !package.AreDownloadsCompleted)
                return new(Array.Empty<PreparedPackageArchive>(), false, Loc.Get("Status_ExtractionWaitingForDownloads"));
            result = await Task.Run(() => PreparePackagePasswords(package, archives, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (package.ItemsSnapshot.Any(item => item.IsUnavailableForProcessing))
                    throw new OperationCanceledException(cancellationToken);
            }), cancellationToken).ConfigureAwait(false);
        }
        SafeInvoke(package.NotifyArchivePasswordStateChanged);
        return result;
    }

    private PackagePasswordPreparation PreparePackagePasswords(DownloadPackage package, IEnumerable<string> archives, Action checkpoint)
    {
        var snapshot = SettingsService.Instance.GetArchivePasswordSnapshot();
        var saved = snapshot.Entries.OrderByDescending(entry => entry.LastSuccessAt)
            .ThenByDescending(entry => entry.SuccessCount).Select(entry => entry.Password).ToArray();
        var prepared = new List<PreparedPackageArchive>();
        var acceptedSavedPasswords = new List<string>();
        var state = package.ArchivePasswordState;
        bool missing = false;
        string? failureStatus = null;
        long revision;
        lock (state.Sync) revision = state.Revision;
        foreach (var path in archives)
        {
            checkpoint();
            string name = GetPackageArchiveKey(package, path);
            string stamp = BuildArchiveSourceStamp(path);
            ArchivePasswordCheck check;
            lock (state.Sync)
            {
                if (!state.Archives.TryGetValue(name, out check!) || check.SourceStamp != stamp)
                    state.Archives[name] = check = new ArchivePasswordCheck { SourceStamp = stamp };
                check.DisplayName = GetPackageArchiveDisplayName(package, path);
            }
            string? missingVolume = ExtractionErrorClassifier.FindMissingVolume(path);
            if (!string.IsNullOrEmpty(missingVolume))
            {
                failureStatus ??= ExtractionErrorClassifier.BuildMissingVolumeStatus(missingVolume);
                continue;
            }
            bool initialProbe = ProbeArchivePassword(path, null, checkpoint, out bool encrypted, out var initialFailure);
            lock (state.Sync)
            {
                check.IsEncrypted = encrypted;
                check.Inspected = true;
                check.Failure = initialFailure;
                check.Password = null;
            }
            if (initialFailure is UnsupportedEncryptedVolumesException || (!initialProbe && !encrypted))
            {
                failureStatus ??= BuildPasswordCheckFailure(path, initialFailure);
                continue;
            }
            if (!encrypted)
            {
                prepared.Add(new(path, name, null, check));
                continue;
            }
            string[] candidates;
            lock (state.Sync) candidates = GetManualCandidates(state, name).Concat(acceptedSavedPasswords).Concat(saved).Distinct(StringComparer.Ordinal).ToArray();
            string? accepted = null;
            foreach (var password in candidates)
            {
                checkpoint();
                ArchivePasswordTrial? trial;
                lock (state.Sync) check.Trials.TryGetValue(password, out trial);
                if (trial != null)
                {
                    if (trial.Outcome is ArchivePasswordOutcome.Accepted or ArchivePasswordOutcome.Verified)
                    {
                        accepted = password;
                        break;
                    }
                    continue;
                }
                trial = new ArchivePasswordTrial { Counter = SettingsService.Instance.RecordArchivePasswordAttempt(password) };
                bool matches = ProbeArchivePassword(path, password, checkpoint, out _, out var failure);
                lock (state.Sync)
                {
                    trial.Outcome = matches ? ArchivePasswordOutcome.Accepted : ArchivePasswordOutcome.NotConfirmed;
                    check.Trials[password] = trial;
                    check.Failure = failure;
                }
                if (failure is UnsupportedEncryptedVolumesException)
                {
                    failureStatus ??= BuildPasswordCheckFailure(path, failure);
                    break;
                }
                if (matches) { accepted = password; break; }
            }
            lock (state.Sync) check.Password = accepted;
            if (accepted == null) missing = true;
            else
            {
                prepared.Add(new(path, name, accepted, check));
                if (saved.Contains(accepted, StringComparer.Ordinal) && !acceptedSavedPasswords.Contains(accepted, StringComparer.Ordinal))
                    acceptedSavedPasswords.Insert(0, accepted);
            }
        }
        lock (state.Sync)
        {
            state.CheckedRevision = revision;
            state.SettingsRevision = snapshot.Revision;
            state.Ready = !missing && failureStatus == null;
        }
        return new(prepared.ToArray(), missing, failureStatus);
    }

    private static IEnumerable<string> GetManualCandidates(PackageArchivePasswordState state, string name)
    {
        if (state.LastInputPassword != null && (state.LastInputArchive == "*" ||
            string.Equals(state.LastInputArchive, name, StringComparison.OrdinalIgnoreCase)))
            yield return state.LastInputPassword;
        if (state.ManualCandidates.TryGetValue(name, out var scoped))
            foreach (string password in scoped.Keys) yield return password;
        if (state.ManualCandidates.TryGetValue("*", out var shared))
            foreach (string password in shared.Keys) yield return password;
    }

    internal void InvalidatePackageArchivePasswords(DownloadPackage package)
    {
        var checks = GetPrimaryArchives(package).ToDictionary(path => GetPackageArchiveKey(package, path),
            path => new ArchivePasswordCheck { SourceStamp = BuildArchiveSourceStamp(path), DisplayName = GetPackageArchiveDisplayName(package, path) }, StringComparer.OrdinalIgnoreCase);
        lock (package.ArchivePasswordState.Sync)
        {
            package.ArchivePasswordState.Archives.Clear();
            foreach (var pair in checks) package.ArchivePasswordState.Archives[pair.Key!] = pair.Value;
            package.ArchivePasswordState.Ready = false;
            package.ArchivePasswordState.Revision++;
        }
        SafeInvoke(package.NotifyArchivePasswordStateChanged);
    }

    internal PackageArchivePasswordTarget[] GetPackageArchivePasswordTargets(DownloadPackage package) =>
        GetPrimaryArchives(package).Select(path => new PackageArchivePasswordTarget(
            GetPackageArchiveKey(package, path), GetPackageArchiveDisplayName(package, path))).ToArray();

    private string ResolvePackageArchiveKey(DownloadPackage package, string name)
    {
        if (name == "*") return name;
        var target = GetPackageArchivePasswordTargets(package).FirstOrDefault(target =>
            target.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            target.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (target != null) return target.Key;
        // Successful cleanup may remove the archives while their session history is still visible.
        lock (package.ArchivePasswordState.Sync)
            return package.ArchivePasswordState.Archives.FirstOrDefault(pair =>
                pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                pair.Value.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)).Key ?? name;
    }

    private static string GetPackageArchiveKey(DownloadPackage package, string path)
    {
        string fullPath = Path.GetFullPath(path);
        var item = package.ItemsSnapshot.FirstOrDefault(item =>
            string.Equals(item.SaveFilePath, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.SaveFilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        // Item identity survives path changes, including moves from outside the package directory.
        return item != null ? "item:" + item.Id.ToString("N") : "path:" + GetPackageArchiveDisplayName(package, path);
    }

    private static string GetPackageArchiveDisplayName(DownloadPackage package, string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = string.IsNullOrWhiteSpace(package.SaveDirectory) ? Path.GetDirectoryName(fullPath)! : Path.GetFullPath(package.SaveDirectory);
        return Path.GetRelativePath(root, fullPath);
    }

    private string[] GetArchiveSourceFiles(string path)
    {
        path = Path.GetFullPath(path);
        return Directory.GetFiles(Path.GetDirectoryName(path)!).Where(candidate => ArePartOfSameArchive(path, candidate))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string BuildArchiveSourceStamp(string path) => string.Join(";", GetArchiveSourceFiles(path)
        .Select(candidate => { var file = new FileInfo(candidate); return file.Name + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks; }));

    private static string BuildPasswordCheckFailure(string path, Exception? failure) =>
        failure is UnsupportedEncryptedVolumesException ? Loc.Get("Status_ExtractionUnsupportedEncryptedVolumes") :
        failure == null ? Loc.Format("Status_ExtractionFailed", Loc.Get("ArchivePasswords_CheckFailed")) :
        ExtractionErrorClassifier.BuildStatusMessage(ExtractionErrorClassifier.Classify(failure), failure, path);

    private void SetPackagePasswordWaiting(DownloadPackage package, bool unconfirmed = false)
    {
        lock (package.ArchivePasswordState.Sync) package.ArchivePasswordState.Ready = false;
        SafeInvoke(() =>
        {
            package.RequiresArchivePassword = true;
            package.IsExtracted = false;
            foreach (var step in package.NextTaskSteps.Where(step => step.Key == "Extract")) step.State = NextTaskStepState.Pending;
            package.StatusMessage = Loc.Get(unconfirmed ? "Status_ArchivePasswordUnconfirmed" : "Status_ArchivePasswordRequired");
            package.NotifyArchivePasswordStateChanged();
        });
    }

    private void CommitPackagePasswordResults(DownloadPackage package, IEnumerable<PreparedPackageArchive> archives)
    {
        var state = package.ArchivePasswordState;
        foreach (var archive in archives.Where(archive => archive.Password != null))
        {
            string password = archive.Password!;
            ArchivePasswordTrial trial;
            bool remember;
            lock (state.Sync)
            {
                trial = archive.Check.Trials[password];
                remember = (state.ManualCandidates.TryGetValue(archive.Key, out var scoped) && scoped.GetValueOrDefault(password)) ||
                    (state.ManualCandidates.TryGetValue("*", out var shared) && shared.GetValueOrDefault(password));
            }
            if (remember) SettingsService.Instance.RememberArchivePassword(password);
            if (trial.Counter == null && remember) trial.Counter = SettingsService.Instance.RecordArchivePasswordAttempt(password);
            if (trial.Counter != null) SettingsService.Instance.RecordArchivePasswordSuccess(trial.Counter);
            lock (state.Sync) trial.Outcome = ArchivePasswordOutcome.Verified;
        }
        lock (state.Sync) state.ManualRequest = false;
        SafeInvoke(() => { package.RequiresArchivePassword = false; package.NotifyArchivePasswordStateChanged(); });
    }

    public void CancelPackageExtraction(DownloadPackage package)
    {
        lock (package.ArchivePasswordState.Sync)
        {
            package.ArchivePasswordState.Cancelled = true;
            package.ArchivePasswordState.ManualRequest = false;
        }
        CancelPackageExtraction(package.Id);
        SafeInvoke(() =>
        {
            package.RequiresArchivePassword = false;
            package.StatusMessage = Loc.Get("Status_Paused");
            package.NotifyArchivePasswordStateChanged();
        });
    }
}
