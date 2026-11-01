using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public sealed class ExtractionRecoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ExtractionRecovery_").FullName;

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void InterruptedPublication_RecoversAtEachMoveBoundary(int phase, bool original)
    {
        string target = Path.Combine(_root, "destination");
        var journal = Prepare(target, original);
        if (phase >= 1 && original) MoveOriginalToBackup(journal, target);
        if (phase >= 2) File.Move(Path.Combine(journal.Root, "output", "payload.txt"), Path.Combine(target, "payload.txt"));
        string workspace = journal.Root;
        journal.Dispose();
        ExtractionRecoveryJournal.RecoverPending(target);
        ExtractionRecoveryJournal.RecoverPending(target);
        Assert.Equal(original, File.Exists(Path.Combine(target, "payload.txt")));
        if (original) Assert.Equal("original", File.ReadAllText(Path.Combine(target, "payload.txt")));
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public void NewExtraction_AutomaticallyRecoversPreviousInterruptedPublication()
    {
        string target = Path.Combine(_root, "destination");
        var journal = Prepare(target, original: true);
        MoveOriginalToBackup(journal, target);
        File.Move(Path.Combine(journal.Root, "output", "payload.txt"), Path.Combine(target, "payload.txt"));
        string oldWorkspace = journal.Root;
        journal.Dispose();
        using var next = new ArchiveExtractionWorkspace(target);
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "payload.txt")));
        Assert.False(Directory.Exists(oldWorkspace));
    }

    [Fact]
    public void CommittedGroup_WithOnlyOnePersistedMember_KeepsAllVerifiedResults()
    {
        string firstTarget = Path.Combine(_root, "first");
        string secondTarget = Path.Combine(_root, "second");
        var first = Prepare(firstTarget, original: true);
        var second = Prepare(secondTarget, original: true);
        first.Publish(CancellationToken.None);
        second.Publish(CancellationToken.None);
        ExtractionRecoveryJournal.CommitAll(new[] { first, second });
        first.Dispose();
        second.Dispose();
        Rewrite(second.Root, state => state.Committed = false);
        ExtractionRecoveryJournal.RecoverPending(secondTarget);
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(firstTarget, "payload.txt")));
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(secondTarget, "payload.txt")));
        Assert.False(Directory.Exists(first.Root));
        Assert.False(Directory.Exists(second.Root));
    }

    [Fact]
    public void CommitPreparationInterruptedBeforeDecision_RestoresEveryDestination()
    {
        string firstTarget = Path.Combine(_root, "first");
        string secondTarget = Path.Combine(_root, "second");
        var first = Prepare(firstTarget, original: true);
        var second = Prepare(secondTarget, original: true);
        first.Publish(CancellationToken.None);
        second.Publish(CancellationToken.None);
        first.Dispose();
        second.Dispose();
        Rewrite(first.Root, state => state.Members = new[] { first.Root, second.Root });
        ExtractionRecoveryJournal.RecoverPending(firstTarget);
        Assert.Equal("original", File.ReadAllText(Path.Combine(firstTarget, "payload.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(secondTarget, "payload.txt")));
        Assert.False(Directory.Exists(first.Root));
        Assert.False(Directory.Exists(second.Root));
    }

    [Fact]
    public void ActiveWorkspace_IsNotRecoveredByAnotherExtraction()
    {
        string target = Path.Combine(_root, "destination");
        using var journal = Prepare(target, original: true);
        ExtractionRecoveryJournal.RecoverPending(target);
        Assert.True(Directory.Exists(journal.Root));
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "payload.txt")));
        journal.Rollback();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedDestinationOrMissingBackup_RetainsRecoveryData(bool missingBackup)
    {
        string target = Path.Combine(_root, "destination");
        var journal = Prepare(target, original: true);
        journal.Publish(CancellationToken.None);
        journal.Dispose();
        if (missingBackup) File.Delete(Path.Combine(journal.Root, "backup", "0"));
        else File.WriteAllText(Path.Combine(target, "payload.txt"), "user changes");
        Assert.Throws<IOException>(() => ExtractionRecoveryJournal.RecoverPending(target));
        Assert.True(Directory.Exists(journal.Root));
        Assert.Equal(missingBackup ? "replacement" : "user changes", File.ReadAllText(Path.Combine(target, "payload.txt")));
        if (!missingBackup)
        {
            File.WriteAllText(Path.Combine(target, "payload.txt"), "replacement");
            ExtractionRecoveryJournal.RecoverPending(target);
            Assert.Equal("original", File.ReadAllText(Path.Combine(target, "payload.txt")));
            Assert.False(Directory.Exists(journal.Root));
        }
    }

    [Fact]
    public void InvalidJournalPath_NeverTouchesOutsideFiles()
    {
        string target = Path.Combine(_root, "destination");
        string outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "keep");
        var journal = Prepare(target, original: false);
        journal.Dispose();
        Rewrite(journal.Root, state => state.Entries[0].Path = "../outside.txt");
        Assert.Throws<IOException>(() => ExtractionRecoveryJournal.RecoverPending(target));
        Assert.Equal("keep", File.ReadAllText(outside));
        Assert.True(Directory.Exists(journal.Root));
    }

    [Fact]
    public void CancellationDuringPublication_RestoresFilesAndRemovesNewDirectories()
    {
        string target = Path.Combine(_root, "destination");
        using var workspace = new ArchiveExtractionWorkspace(target);
        Directory.CreateDirectory(Path.Combine(workspace.OutputDirectory, "new", "empty"));
        File.WriteAllText(Path.Combine(workspace.OutputDirectory, "new", "payload.txt"), "replacement");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => workspace.Publish(retainRollback: true, cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(target, "new")));
    }

    private static ExtractionRecoveryJournal Prepare(string target, bool original)
    {
        Directory.CreateDirectory(target);
        if (original) File.WriteAllText(Path.Combine(target, "payload.txt"), "original");
        string root = Path.Combine(target, ".archive-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "output"));
        File.WriteAllText(Path.Combine(root, "output", "payload.txt"), "replacement");
        var journal = new ExtractionRecoveryJournal(root);
        journal.Prepare(Path.Combine(root, "output"), CancellationToken.None);
        return journal;
    }

    private static void MoveOriginalToBackup(ExtractionRecoveryJournal journal, string target)
    {
        Directory.CreateDirectory(Path.Combine(journal.Root, "backup"));
        File.Move(Path.Combine(target, "payload.txt"), Path.Combine(journal.Root, "backup", "0"));
    }

    private static void Rewrite(string root, Action<ExtractionRecoveryJournal.State> update)
    {
        string path = Path.Combine(root, "publication.json");
        var state = JsonSerializer.Deserialize<ExtractionRecoveryJournal.State>(File.ReadAllText(path))!;
        update(state);
        File.WriteAllText(path, JsonSerializer.Serialize(state));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
