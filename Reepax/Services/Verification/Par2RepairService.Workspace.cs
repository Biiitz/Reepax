using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Reepax.Services.Storage;

namespace Reepax.Services.Verification;

public partial class Par2RepairService
{
    internal sealed partial class RepairBackupScope
    {
        private sealed record DirectoryIdentity(ulong Volume, ulong Low, ulong High, long Created);
        private sealed record Output(string Target, Identity Identity);
        private sealed record Workspace(DirectoryIdentity? Identity, Output[]? Outputs = null, bool PurgeBackups = false);

        private readonly Dictionary<string, Identity> _workspaceInputs = new(StringComparer.OrdinalIgnoreCase);
        private Output[]? _pendingOutputs;

        internal string? WorkspacePath => _journal is { Stage: not null } journal ? GetWorkspacePath(journal) : null;

        internal async Task StageInputsAsync(CancellationToken token)
        {
            var journal = _journal!;
            CheckWorkspace(journal);
            foreach (var candidate in _candidates)
            {
                token.ThrowIfCancellationRequested();
                var entry = candidate.Entry;
                string staged = GetWorkspaceFile(journal, entry.Original);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                bool readOnly = (File.GetAttributes(entry.Original) & FileAttributes.ReadOnly) != 0;
                // The captured source handles deny writes through a hard link until PAR2 has stopped.
                if (!readOnly && CreateHardLinkW(ToNativePath(staged), ToNativePath(entry.Original), 0))
                {
                    if (!Matches(staged, entry.Identity))
                        throw new IOException("A PAR2 input changed while it was linked.");
                }
                else
                {
                    await using var input = new FileStream(entry.Original, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 128 * 1024, useAsync: true);
                    if (ReadIdentity(input.SafeFileHandle) != entry.Identity)
                        throw new IOException("A PAR2 input changed before it could be copied.");
                    await using var copy = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        128 * 1024, useAsync: true);
                    await input.CopyToAsync(copy, token).ConfigureAwait(false);
                }
                _workspaceInputs.Add(entry.Original, ReadReplacement(staged)
                    ?? throw new IOException("A staged PAR2 input could not be identified."));
            }
        }

        internal async Task VerifyWorkspaceOutputsAsync(IReadOnlyDictionary<string, (ulong Length, byte[] Md5)> targets,
            CancellationToken token)
        {
            var journal = _journal!;
            CheckWorkspace(journal);
            var outputs = new List<Output>();
            foreach (var (target, expected) in targets)
            {
                token.ThrowIfCancellationRequested();
                string staged = GetWorkspaceFile(journal, target);
                if (HasReparsePoint(staged)) throw new IOException("A staged PAR2 output is linked.");
                await using var file = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, useAsync: true);
                var identity = ReadIdentity(file.SafeFileHandle);
                if (identity == null || (ulong)identity.Length != expected.Length)
                    throw new InvalidDataException("A staged PAR2 output has an unexpected length.");
                if (_workspaceInputs.TryGetValue(target, out var stagedInput) && identity == stagedInput) continue;
                byte[] hash = await MD5.HashDataAsync(file, token).ConfigureAwait(false);
                if (!hash.AsSpan().SequenceEqual(expected.Md5))
                    throw new InvalidDataException("A staged PAR2 output did not pass verification.");
                outputs.Add(new(target, identity));
            }
            _pendingOutputs = outputs.ToArray();
        }

        internal void PublishWorkspace(bool purgeBackups)
        {
            var journal = _journal! with
            {
                Decision = RecoveryDecision.Publish,
                Stage = _journal.Stage! with { Outputs = _pendingOutputs!, PurgeBackups = purgeBackups }
            };
            CloseTargetHandles();
            Save(_journalPath!, journal, overwrite: true);
            _journal = journal;
            RepairDecisionSaved?.Invoke();
            ApplyWorkspace(journal);
            File.Delete(_journalPath!);
            _journal = null;
        }

        private static void ApplyWorkspace(Journal journal)
        {
            var stage = journal.Stage!;
            // No input or native output is written until workspace identity has been saved.
            if (stage.Identity == null) return;
            string workspace = GetWorkspacePath(journal);
            bool workspaceExists = !IsAbsent(workspace);
            if (workspaceExists) CheckWorkspace(journal);

            if (journal.Decision == RecoveryDecision.Publish)
            {
                var originals = journal.Entries.ToDictionary(entry => entry.Original, StringComparer.OrdinalIgnoreCase);
                foreach (var output in stage.Outputs!)
                {
                    string target = output.Target;
                    originals.TryGetValue(target, out var original);
                    if (HasReparsePoint(target) || (original != null && HasReparsePoint(original.Backup)))
                        throw new IOException("A PAR2 publication path is linked.");
                    if (!Matches(target, output.Identity))
                    {
                        string staged = GetWorkspaceFile(journal, target);
                        if (!workspaceExists || HasReparsePoint(staged))
                            throw new IOException("A verified PAR2 output is unavailable.");
                        using var source = RelocationFileAccess.OpenSource(staged);
                        if (!source.CanDelete || ReadIdentity(source.Stream.SafeFileHandle) != output.Identity)
                            throw new IOException("A verified PAR2 output changed.");
                        PublishOutput(source, target, original);
                    }

                    if (stage.PurgeBackups && original != null && !IsAbsent(original.Backup))
                    {
                        using var current = File.OpenHandle(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var backup = RelocationFileAccess.OpenSource(original.Backup);
                        if (ReadIdentity(current) == output.Identity && ReadIdentity(backup.Stream.SafeFileHandle) == original.Identity &&
                            !backup.TryDelete())
                            throw new IOException("A staged PAR2 backup could not be cleaned.");
                    }
                }
            }

            if (workspaceExists)
            {
                RejectWorkspaceLinks(workspace);
                Directory.Delete(workspace, recursive: true);
            }
        }

        private static void PublishOutput(RelocationFileAccess.SourceLease source, string target, Entry? original)
        {
            if (!IsAbsent(target))
            {
                if (original == null) throw new IOException("A previously missing PAR2 target is now occupied.");
                using var current = RelocationFileAccess.OpenSource(target);
                if (ReadIdentity(current.Stream.SafeFileHandle) != original.Identity)
                    throw new IOException("A PAR2 target changed before publication.");
                current.MoveTo(original.Backup);
            }
            else if (original != null && !Matches(original.Backup, original.Identity))
            {
                throw new IOException("The original PAR2 target and its backup are unavailable.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            source.MoveTo(target);
        }

        private static string GetWorkspacePath(Journal journal) =>
            Path.Combine(journal.Directory, ".par2-work-" + journal.Id.ToString("N"));

        private static string GetWorkspaceFile(Journal journal, string target)
        {
            string relative = Path.GetRelativePath(journal.Directory, target);
            if (Path.IsPathRooted(relative) || relative == "." || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException("A PAR2 workspace path is outside the repair directory.");
            return Path.Combine(GetWorkspacePath(journal), relative);
        }

        private static void CheckWorkspace(Journal journal)
        {
            string workspace = GetWorkspacePath(journal);
            if (HasReparsePoint(workspace) || ReadDirectoryIdentity(workspace) != journal.Stage!.Identity)
                throw new IOException("The PAR2 workspace has changed.");
        }

        private static void RejectWorkspaceLinks(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The PAR2 workspace contains a link.");
                if ((attributes & FileAttributes.Directory) != 0) RejectWorkspaceLinks(path);
            }
        }

        private static DirectoryIdentity ReadDirectoryIdentity(string path)
        {
            const uint readAttributes = 0x80, openExisting = 3, openDirectory = 0x02000000, noFollow = 0x00200000;
            using var handle = CreateFileW(ToNativePath(path), readAttributes, FileShare.ReadWrite | FileShare.Delete,
                0, openExisting, openDirectory | noFollow, 0);
            if (handle.IsInvalid || !TryGetIdentity(handle, out var identity))
                throw new IOException("The PAR2 workspace could not be identified.");
            return new(identity.Volume, identity.Low, identity.High, File.GetCreationTimeUtc(handle).ToFileTimeUtc());
        }

        private static string ToNativePath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(string link, string original, nint reserved);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string path, nint security);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security,
            uint creation, uint flags, nint template);
    }
}
