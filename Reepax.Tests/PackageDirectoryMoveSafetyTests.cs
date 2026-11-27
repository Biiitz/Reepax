using System.IO;
using Reepax.Models;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public sealed class PackageDirectoryMoveSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("MoveSafety_").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopiedSource_CannotLoseAnExternalChange(bool replaceFile)
    {
        var plan = CreatePlan();
        string source = plan.Files[0].Source;
        string target = Path.Combine(plan.Target, plan.Files[0].RelativePath);
        bool changed = false;
        var service = new PackageDirectoryMoveService(allowHardLinks: false, fileCopied: copied =>
        {
            if (copied != source) return;
            try
            {
                if (replaceFile) File.Move(source, source + ".previous");
                File.WriteAllText(source, "external change");
                changed = true;
            }
            catch (IOException) { }
        });
        var failure = await Record.ExceptionAsync(() => service.ExecuteAsync(plan));
        if (changed)
        {
            Assert.True((File.Exists(source) && File.ReadAllText(source) == "external change") ||
                (File.Exists(target) && File.ReadAllText(target) == "external change"),
                "A successful external edit must survive relocation.");
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal("original", File.ReadAllText(target));
            Assert.False(File.Exists(source));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedDestination_CannotCauseDeletionOfTheOnlyCorrectCopy(bool replaceFile)
    {
        var plan = CreatePlan();
        string source = plan.Files[0].Source;
        string target = Path.Combine(plan.Target, plan.Files[0].RelativePath);
        var service = new PackageDirectoryMoveService(allowHardLinks: false, fileCopied: copied =>
        {
            if (copied != source) return;
            string stage = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(plan.Target)!, ".download-move-*"));
            string staged = Path.Combine(stage, plan.Files[0].RelativePath);
            try
            {
                if (replaceFile) File.Move(staged, staged + ".previous");
                File.WriteAllText(staged, "external change");
            }
            catch (IOException) { }
        });
        var failure = await Record.ExceptionAsync(() => service.ExecuteAsync(plan));
        if (failure == null)
        {
            Assert.Equal("original", File.ReadAllText(target));
            Assert.False(File.Exists(source));
        }
        else
        {
            Assert.IsAssignableFrom<IOException>(failure);
            Assert.Equal("original", File.ReadAllText(source));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableSourceDeletion_PreservesTheOriginalAndReportsIncompleteCleanup(bool readOnly)
    {
        var plan = CreatePlan();
        string source = plan.Files[0].Source;
        using var reader = readOnly ? null : new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (readOnly) File.SetAttributes(source, FileAttributes.ReadOnly);
        try
        {
            Assert.False(await new PackageDirectoryMoveService(allowHardLinks: false).ExecuteAsync(plan));
            Assert.Equal("original", File.ReadAllText(source));
            Assert.Equal("original", File.ReadAllText(Path.Combine(plan.Target, plan.Files[0].RelativePath)));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(plan.Target)!, ".download-move-*"));
        }
        finally
        {
            if (readOnly && File.Exists(source)) File.SetAttributes(source, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task FailedCopy_ReleasesAllLocksAndRemovesStagingWithoutDeletingSources()
    {
        var plan = CreatePlan();
        var service = new PackageDirectoryMoveService(allowHardLinks: false,
            fileCopied: _ => throw new IOException("Synthetic copy failure."));
        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(plan));
        Assert.False(Directory.Exists(plan.Target));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(plan.Target)!, ".download-move-*"));
        Assert.Equal("original", File.ReadAllText(plan.Files[0].Source));
        Assert.Equal("second payload", File.ReadAllText(plan.Files[1].Source));
        foreach (var file in plan.Files) File.AppendAllText(file.Source, " unlocked");
    }

    [Fact]
    public async Task NewUnplannedFile_IsPreservedAndReportsIncompleteCleanup()
    {
        var plan = CreatePlan();
        string extra = Path.Combine(plan.Source!, "new.bin");
        var service = new PackageDirectoryMoveService(allowHardLinks: false, fileCopied: source =>
        {
            if (source == plan.Files[0].Source) File.WriteAllText(extra, "new external file");
        });
        Assert.False(await service.ExecuteAsync(plan));
        Assert.Equal("new external file", File.ReadAllText(extra));
        Assert.Equal("original", File.ReadAllText(Path.Combine(plan.Target, plan.Files[0].RelativePath)));
    }

    [Fact]
    public async Task CopyMove_PreservesBinaryFilesPartialDownloadsAndNestedContents()
    {
        var initial = CreatePlan();
        byte[] partial = Enumerable.Range(0, 32769).Select(index => (byte)(index % 251)).ToArray();
        string partialPath = initial.Files[0].Source + ".part";
        File.WriteAllBytes(partialPath, partial);
        File.WriteAllText(partialPath + ".segments", "synthetic checkpoint");
        string nested = Path.Combine(initial.Source!, "Nested", "payload.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllBytes(nested, partial);
        initial.Package.ExtractedFiles = [ExtractedPackageFile.Capture(nested)];
        var plan = Assert.Single(PackageDirectoryMoveService.Prepare([initial.Package], [initial.Package],
            Path.GetDirectoryName(initial.Target)!));
        Assert.True(await new PackageDirectoryMoveService(allowHardLinks: false).ExecuteAsync(plan));
        Assert.Equal(partial, File.ReadAllBytes(Path.Combine(plan.Target, "first.bin.part")));
        Assert.Equal("synthetic checkpoint", File.ReadAllText(Path.Combine(plan.Target, "first.bin.part.segments")));
        Assert.Equal(partial, File.ReadAllBytes(Path.Combine(plan.Target, "Nested", "payload.bin")));
        Assert.False(Directory.Exists(plan.Source));
    }

    [Fact]
    public async Task CopyMove_WithForeignContentsCanBeRetriedAfterOwnedFileIsUnlocked()
    {
        var initial = CreatePlan();
        string foreign = Path.Combine(initial.Source!, "unrelated.bin");
        byte[] untouched = [11, 17, 23];
        File.WriteAllBytes(foreign, untouched);
        var plan = Assert.Single(PackageDirectoryMoveService.Prepare([initial.Package], [initial.Package],
            Path.GetDirectoryName(initial.Target)!));
        var service = new PackageDirectoryMoveService(allowHardLinks: false);
        using (File.Open(initial.Files[1].Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(plan));
        Assert.False(Directory.Exists(plan.Target));
        Assert.Equal(initial.Source, initial.Package.SaveDirectory);
        Assert.Equal("original", File.ReadAllText(initial.Files[0].Source));
        Assert.Equal("second payload", File.ReadAllText(initial.Files[1].Source));
        Assert.Equal(untouched, File.ReadAllBytes(foreign));

        Assert.True(await service.ExecuteAsync(plan));
        Assert.Equal("original", File.ReadAllText(Path.Combine(plan.Target, initial.Files[0].RelativePath)));
        Assert.Equal("second payload", File.ReadAllText(Path.Combine(plan.Target, initial.Files[1].RelativePath)));
        Assert.False(File.Exists(initial.Files[0].Source));
        Assert.False(File.Exists(initial.Files[1].Source));
        Assert.Equal(untouched, File.ReadAllBytes(foreign));
        Assert.False(File.Exists(Path.Combine(plan.Target, "unrelated.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_DoesNotAdoptAFileThatReplacedThePlannedSource(bool allowHardLinks)
    {
        var plan = CreatePlan();
        string source = plan.Files[0].Source;
        string retained = source + ".retained";
        byte[] original = File.ReadAllBytes(source);
        byte[] foreign = original.Select(value => (byte)(value ^ 1)).ToArray();
        DateTime lastWrite = File.GetLastWriteTimeUtc(source);
        File.Move(source, retained);
        File.WriteAllBytes(source, foreign);
        File.SetLastWriteTimeUtc(source, lastWrite);
        var service = new PackageDirectoryMoveService(allowHardLinks);

        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(plan));
        Assert.False(Directory.Exists(plan.Target));
        Assert.Equal(foreign, File.ReadAllBytes(source));
        Assert.Equal(original, File.ReadAllBytes(retained));
        Assert.Equal(plan.Source, plan.Package.SaveDirectory);
        Assert.False(Directory.EnumerateDirectories(Path.GetDirectoryName(plan.Target)!, ".download-move-*").Any());

        string independent = Path.Combine(_root, "independent.bin");
        File.Move(source, independent);
        File.Move(retained, source);
        Assert.True(await service.ExecuteAsync(plan));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(plan.Target, plan.Files[0].RelativePath)));
        Assert.Equal(foreign, File.ReadAllBytes(independent));
        Assert.False(Directory.Exists(plan.Source));
    }

    private PackageDirectoryMoveService.Plan CreatePlan()
    {
        string source = Path.Combine(_root, "Source", "Sample");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "first.bin"), "original");
        File.WriteAllText(Path.Combine(source, "second.bin"), "second payload");
        var package = new DownloadPackage { Name = "Sample", SaveDirectory = source };
        package.Items.Add(new DownloadItem { FileName = "first.bin", SaveFilePath = Path.Combine(source, "first.bin") });
        package.Items.Add(new DownloadItem { FileName = "second.bin", SaveFilePath = Path.Combine(source, "second.bin") });
        var plan = Assert.Single(PackageDirectoryMoveService.Prepare([package], [package], Path.Combine(_root, "Target")));
        return plan with { Files = plan.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray() };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
