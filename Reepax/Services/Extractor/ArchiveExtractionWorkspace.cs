using System;
using System.IO;
using System.Linq;
using System.Threading;
using Reepax.Services.Storage;

namespace Reepax.Services.Extractor;

internal sealed class ArchiveExtractionWorkspace : IDisposable
{
    private readonly string _target;
    private readonly bool _createdTarget;
    private readonly ExtractionRecoveryJournal _journal;
    private bool _publicationPending;
    internal string OutputDirectory { get; }
    internal string TargetDirectory => _target;

    internal ArchiveExtractionWorkspace(string targetDirectory)
    {
        _target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        RejectReparsePoints(_target);
        ExtractionRecoveryJournal.RecoverPending(_target);
        _createdTarget = !Directory.Exists(_target);
        Directory.CreateDirectory(_target);
        string root = Path.Combine(_target, ".archive-work-" + Guid.NewGuid().ToString("N"));
        OutputDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(OutputDirectory);
        try { _journal = new ExtractionRecoveryJournal(root); }
        catch
        {
            try
            {
                RejectReparsePoints(root);
                foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)) RejectReparsePoints(entry);
                Directory.Delete(root, recursive: true);
                if (_createdTarget && !Directory.EnumerateFileSystemEntries(_target).Any()) Directory.Delete(_target);
            }
            catch { AppLogger.Warn("[ArchiveExtractor] Extraction workspace initialization cleanup failed."); }
            throw;
        }
    }

    internal void Publish() => Publish(retainRollback: false);

    internal void Publish(bool retainRollback, CancellationToken cancellationToken = default)
    {
        if (_publicationPending) throw new InvalidOperationException("Publication is already pending.");
        try
        {
            _journal.Prepare(OutputDirectory, cancellationToken);
            _publicationPending = true;
            _journal.Publish(cancellationToken);
            if (!retainRollback) CommitAll(new[] { this });
        }
        catch
        {
            RollbackPublication();
            throw;
        }
    }

    internal static void CommitAll(System.Collections.Generic.IEnumerable<ArchiveExtractionWorkspace> workspaces)
    {
        var all = workspaces.ToArray();
        ExtractionRecoveryJournal.CommitAll(all.Select(workspace => workspace._journal));
        foreach (var workspace in all) workspace._publicationPending = false;
    }

    internal void RollbackPublication()
    {
        if (!_publicationPending) return;
        _journal.Rollback();
        _publicationPending = false;
    }

    internal static void RejectReparsePoints(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked extraction destinations are not supported.");
        }
    }

    public void Dispose()
    {
        try { RollbackPublication(); }
        catch { AppLogger.Warn("[ArchiveExtractor] Extraction recovery files were retained."); }
        try
        {
            _journal.Dispose();
            _journal.Cleanup();
            if (_createdTarget && Directory.Exists(_target) && !Directory.EnumerateFileSystemEntries(_target).Any()) Directory.Delete(_target);
        }
        catch { AppLogger.Warn("[ArchiveExtractor] Extraction workspace cleanup failed."); }
    }
}
