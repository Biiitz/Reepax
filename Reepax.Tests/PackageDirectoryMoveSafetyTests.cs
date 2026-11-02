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
        var service = new PackageDirectoryMoveService(allowDirectoryRename: false, fileCopied: copied =>
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
        var service = new PackageDirectoryMoveService(allowDirectoryRename: false, fileCopied: copied =>
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
            Assert.False(await new PackageDirectoryMoveService(allowDirectoryRename: false).ExecuteAsync(plan));
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
        var service = new PackageDirectoryMoveService(allowDirectoryRename: false,
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
        var service = new PackageDirectoryMoveService(allowDirectoryRename: false, fileCopied: source =>
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
        var plan = Assert.Single(PackageDirectoryMoveService.Prepare([initial.Package], [initial.Package],
            Path.GetDirectoryName(initial.Target)!));
        Assert.True(await new PackageDirectoryMoveService(allowDirectoryRename: false).ExecuteAsync(plan));
        Assert.Equal(partial, File.ReadAllBytes(Path.Combine(plan.Target, "first.bin.part")));
        Assert.Equal("synthetic checkpoint", File.ReadAllText(Path.Combine(plan.Target, "first.bin.part.segments")));
        Assert.Equal(partial, File.ReadAllBytes(Path.Combine(plan.Target, "Nested", "payload.bin")));
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
