using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Reepax.Services.Extractor;

internal sealed class ExtractionRecoveryJournal : IDisposable
{
    private const string JournalName = "publication.json";
    private readonly string _root;
    private readonly string _target;
    private readonly FileStream _lease;
    private State _state;
    internal bool Retain { get; private set; }
    internal string Root => _root;

    internal sealed class Entry
    {
        public string Path { get; set; } = "";
        public string Hash { get; set; } = "";
        public string? OriginalHash { get; set; }
    }

    internal sealed class State
    {
        public int Version { get; set; } = 1;
        public string Workspace { get; set; } = "";
        public bool Committed { get; set; }
        public bool RolledBack { get; set; }
        public string[] Members { get; set; } = Array.Empty<string>();
        public List<Entry> Entries { get; set; } = new();
        public List<string> Directories { get; set; } = new();
    }

    internal ExtractionRecoveryJournal(string root, bool existing = false)
    {
        _root = Path.GetFullPath(root);
        _target = Path.GetDirectoryName(_root)!;
        ValidateRoot(_root);
        ArchiveExtractionWorkspace.RejectReparsePoints(Path.Combine(_root, "lease.lock"));
        ArchiveExtractionWorkspace.RejectReparsePoints(Path.Combine(_root, JournalName));
        _lease = new FileStream(Path.Combine(_root, "lease.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _state = existing ? ReadState(_root) : new State { Workspace = Path.GetFileName(_root) };
            ValidateState();
            if (!existing) Save();
        }
        catch { _lease.Dispose(); throw; }
    }

    internal void Prepare(string output, CancellationToken token)
    {
        if (_state.Entries.Count != 0 || _state.Committed || _state.RolledBack)
            throw new InvalidOperationException("Publication has already been prepared.");
        var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
        var entries = new List<Entry>();
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(output, file);
            string target = Destination(relative);
            ArchiveExtractionWorkspace.RejectReparsePoints(file);
            if (Directory.Exists(target) || (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0))
                throw new IOException("An extraction destination cannot be replaced.");
            entries.Add(new Entry { Path = relative, Hash = Hash(file, token), OriginalHash = File.Exists(target) ? Hash(target, token) : null });
        }
        _state.Entries = entries;
        _state.Directories = Directory.GetDirectories(output, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output, path))
            .Where(path => !Directory.Exists(Destination(path))).OrderBy(path => path.Length).ToList();
        ValidateState();
        Save();
    }

    internal void Publish(CancellationToken token)
    {
        foreach (string directory in _state.Directories)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Destination(directory));
        }
        for (int i = 0; i < _state.Entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = _state.Entries[i];
            string target = Destination(entry.Path);
            string source = Path.Combine(_root, "output", entry.Path);
            if (!Matches(source, entry.Hash, token)) throw new IOException("Staged extraction data changed before publication.");
            if (entry.OriginalHash != null)
            {
                if (!Matches(target, entry.OriginalHash, token)) throw new IOException("An extraction destination changed before publication.");
                Directory.CreateDirectory(Path.Combine(_root, "backup"));
                File.Move(target, Backup(i));
            }
            // The complete plan is durable before the first destination changes.
            File.Move(source, target);
        }
    }

    internal static void CommitAll(IEnumerable<ExtractionRecoveryJournal> publications)
    {
        var journals = publications.ToArray();
        if (journals.Length == 0) return;
        string[] members = journals.Select(journal => journal._root).ToArray();
        try
        {
            foreach (var journal in journals) { journal._state.Members = members; journal.Save(); }
            WriteDecision(members[0]);
            foreach (var journal in journals) { journal._state.Committed = true; journal.Save(); }
        }
        catch
        {
            foreach (var journal in journals) journal.Retain = true;
            throw;
        }
    }

    internal void Rollback()
    {
        if (_state.Committed || _state.RolledBack || HasCommitDecision()) return;
        try
        {
            for (int i = _state.Entries.Count - 1; i >= 0; i--)
            {
                var entry = _state.Entries[i];
                string target = Destination(entry.Path);
                string backup = Backup(i);
                ArchiveExtractionWorkspace.RejectReparsePoints(backup);
                if (File.Exists(backup))
                {
                    if (entry.OriginalHash == null || !Matches(backup, entry.OriginalHash, CancellationToken.None))
                        throw new IOException("An extraction backup changed.");
                    if (File.Exists(target))
                    {
                        if (!Matches(target, entry.Hash, CancellationToken.None))
                            throw new IOException("An extraction destination changed during recovery.");
                        File.Delete(target);
                    }
                    File.Move(backup, target);
                }
                else if (entry.OriginalHash != null)
                {
                    if (!Matches(target, entry.OriginalHash, CancellationToken.None))
                        throw new IOException("An extraction backup is missing.");
                }
                else if (!File.Exists(Path.Combine(_root, "output", entry.Path)) && File.Exists(target))
                {
                    if (!Matches(target, entry.Hash, CancellationToken.None))
                        throw new IOException("An extraction destination changed during recovery.");
                    File.Delete(target);
                }
            }
            foreach (string directory in _state.Directories.AsEnumerable().Reverse())
            {
                string path = Destination(directory);
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            _state.RolledBack = true;
            Save();
        }
        catch { Retain = true; throw; }
    }

    internal static void RecoverPending(string target)
    {
        if (!Directory.Exists(target)) return;
        foreach (string root in Directory.GetDirectories(target, ".archive-work-*", SearchOption.TopDirectoryOnly))
        {
            if (!File.Exists(Path.Combine(root, JournalName))) continue;
            ExtractionRecoveryJournal journal;
            try { journal = new ExtractionRecoveryJournal(root, existing: true); }
            catch (IOException) when (IsLeased(root)) { continue; }
            var group = new List<ExtractionRecoveryJournal> { journal };
            try
            {
                foreach (string member in journal._state.Members.Where(member => !string.Equals(member, root, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!Directory.Exists(member)) continue;
                    var other = new ExtractionRecoveryJournal(member, existing: true);
                    group.Add(other);
                    if (!other._state.Members.SequenceEqual(journal._state.Members, StringComparer.OrdinalIgnoreCase) &&
                        (journal.HasCommitDecision() || other._state.Members.Length != 0))
                        throw new IOException("Inconsistent extraction recovery group.");
                }
                if (journal.HasCommitDecision() || group.All(item => item._state.Committed))
                {
                    // Persist every member before removing the shared commit decision.
                    foreach (var item in group) { item._state.Committed = true; item.Save(); }
                }
                else
                {
                    if (group.Any(item => item._state.Committed)) throw new IOException("An extraction commit decision is missing.");
                    foreach (var item in group.AsEnumerable().Reverse()) item.Rollback();
                }
            }
            catch
            {
                foreach (var item in group) item.Retain = true;
                throw new IOException("Extraction recovery could not finish; recovery files were retained.");
            }
            finally
            {
                foreach (var item in group.AsEnumerable().Reverse()) item.Dispose();
                foreach (var item in group.AsEnumerable().Reverse()) item.Cleanup();
            }
        }
    }

    private bool HasCommitDecision()
    {
        if (_state.Members.Length == 0) return false;
        string path = Path.Combine(_state.Members[0], "commit");
        ArchiveExtractionWorkspace.RejectReparsePoints(path);
        if (!File.Exists(path)) return false;
        byte[] decision = File.ReadAllBytes(path);
        if (decision.Length != 1 || decision[0] != 1) throw new IOException("Invalid extraction commit decision.");
        return true;
    }

    private static bool IsLeased(string root)
    {
        try { using var file = new FileStream(Path.Combine(root, "lease.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    private string Destination(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split(new[] { '/', '\\' }).Any(part => part is ".." or "." || part.StartsWith(".archive-work-", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Invalid extraction recovery destination.");
        string path = Path.GetFullPath(Path.Combine(_target, relative));
        string prefix = Path.EndsInDirectorySeparator(_target) ? _target : _target + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid extraction recovery destination.");
        ArchiveExtractionWorkspace.RejectReparsePoints(path);
        return path;
    }

    private string Backup(int index) => Path.Combine(_root, "backup", index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void ValidateState()
    {
        if (_state.Version != 1 || _state.Workspace != Path.GetFileName(_root) ||
            _state.Entries == null || _state.Directories == null || _state.Members == null)
            throw new IOException("Invalid extraction recovery journal.");
        foreach (string member in _state.Members)
        {
            if (!Path.IsPathFullyQualified(member)) throw new IOException("Invalid extraction recovery member.");
            ValidateRoot(member);
        }
        if (_state.Members.Length > 0 && (!_state.Members.Contains(_root, StringComparer.OrdinalIgnoreCase) ||
            _state.Members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != _state.Members.Length))
            throw new IOException("Invalid extraction recovery group.");
        foreach (var entry in _state.Entries)
        {
            _ = Destination(entry.Path);
            if (!ValidHash(entry.Hash) || (entry.OriginalHash != null && !ValidHash(entry.OriginalHash)))
                throw new IOException("Invalid extraction recovery checksum.");
        }
        if (_state.Entries.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _state.Entries.Count)
            throw new IOException("Duplicate extraction recovery destination.");
        foreach (string directory in _state.Directories) _ = Destination(directory);
    }

    private static bool ValidHash(string hash) => hash != null && hash.Length == 64 && hash.All(Uri.IsHexDigit);

    private static void ValidateRoot(string root)
    {
        string name = Path.GetFileName(root);
        if (!name.StartsWith(".archive-work-", StringComparison.Ordinal) || !Guid.TryParseExact(name[14..], "N", out _))
            throw new IOException("Invalid extraction recovery workspace.");
        ArchiveExtractionWorkspace.RejectReparsePoints(root);
    }

    private static State ReadState(string root)
    {
        using var stream = File.OpenRead(Path.Combine(root, JournalName));
        return JsonSerializer.Deserialize<State>(stream) ?? throw new IOException("Empty extraction recovery journal.");
    }

    private void Save()
    {
        string path = Path.Combine(_root, JournalName);
        string temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _state);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteDecision(string root)
    {
        using var stream = new FileStream(Path.Combine(root, "commit"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.WriteByte(1);
        stream.Flush(flushToDisk: true);
    }

    private static string Hash(string file, CancellationToken token)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, read)); }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool Matches(string file, string hash, CancellationToken token) =>
        File.Exists(file) && string.Equals(Hash(file, token), hash, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _lease.Dispose();
    }

    internal void Cleanup()
    {
        if (Retain) return;
        if (!_state.Committed && !_state.RolledBack && _state.Entries.Count > 0)
            throw new IOException("Unfinished extraction recovery files must be retained.");
        ArchiveExtractionWorkspace.RejectReparsePoints(_root);
        foreach (string path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            ArchiveExtractionWorkspace.RejectReparsePoints(path);
        Directory.Delete(_root, recursive: true);
    }
}
