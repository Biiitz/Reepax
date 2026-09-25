using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public class Par2RepairServiceTests
{
    [Fact]
    public void ResolvePar2ExecutablePath_FindsExecutable()
    {
        var exePath = Par2RepairService.Instance.ResolvePar2ExecutablePath();

        Assert.NotNull(exePath);
        Assert.True(File.Exists(exePath), $"Executable should exist at: {exePath}");
        Assert.True(Par2RepairService.Instance.IsPar2Available);
    }

    [Theory]
    [InlineData("archive.par2", true)]
    [InlineData("archive.vol00+01.par2", true)]
    [InlineData("ARCHIVE.PAR2", true)]
    [InlineData("archive.part01.rar", false)]
    [InlineData("archive.zip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPar2File_RecognizesExtensions(string? path, bool expected)
    {
        var actual = Par2RepairService.Instance.IsPar2File(path);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FindPrimaryPar2File_PrefersMainIndexFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Reepax_Par2Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var volFile = Path.Combine(tempDir, "archive.vol01+02.par2");
            var mainFile = Path.Combine(tempDir, "archive.par2");
            File.WriteAllText(volFile, "volume par2");
            File.WriteAllText(mainFile, "main par2");

            var resolved = Par2RepairService.Instance.FindPrimaryPar2File(tempDir);

            Assert.NotNull(resolved);
            Assert.Equal(mainFile, resolved);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindPrimaryPar2File_FallsBackToVolumeIfMainMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Reepax_Par2Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var volFile = Path.Combine(tempDir, "archive.vol01+02.par2");
            File.WriteAllText(volFile, "volume par2 only");

            var resolved = Par2RepairService.Instance.FindPrimaryPar2File(tempDir);

            Assert.NotNull(resolved);
            Assert.Equal(volFile, resolved);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ParseVerificationOutput_AllFilesIntact()
    {
        var sampleOutput = @"
Loading 'test.par2'.
Loaded 6 new packets
There are 2 recoverable files and 0 other files.
Verifying source files:
Target: 'part1.rar' - found.
Target: 'part2.rar' - found.
All files are correct, repair is not required.
";
        var result = new Par2VerifyResult();
        Par2RepairService.ParseVerificationOutput(sampleOutput, 0, result);

        Assert.Equal(Par2VerificationStatus.AllFilesIntact, result.Status);
        Assert.True(result.IsRepairedOrIntact);
    }

    [Fact]
    public void ParseVerificationOutput_RepairPossible()
    {
        var sampleOutput = @"
Loading 'test.par2'.
Verifying source files:
Target: 'part1.rar' - damaged.
Target: 'part2.rar' - found.
Repair is required.
1 file(s) exist but are damaged.
1 file(s) are ok.
You have 4 out of 8 data blocks available.
You have 6 recovery blocks available.
Repair is possible.
4 recovery blocks will be used to repair.
";
        var result = new Par2VerifyResult();
        Par2RepairService.ParseVerificationOutput(sampleOutput, 1, result);

        Assert.Equal(Par2VerificationStatus.RepairPossible, result.Status);
        Assert.Equal(1, result.DamagedFilesCount);
        Assert.Equal(1, result.OkFilesCount);
        Assert.Equal(4, result.AvailableDataBlocks);
        Assert.Equal(8, result.TotalDataBlocks);
        Assert.Equal(6, result.AvailableRecoveryBlocks);
        Assert.False(result.IsRepairedOrIntact);
    }

    [Fact]
    public void ParseVerificationOutput_RepairNotPossible()
    {
        var sampleOutput = @"
Loading 'test.par2'.
Repair is required.
1 file(s) exist but are damaged.
1 file(s) are ok.
You have 5 out of 10 data blocks available.
You have 1 recovery blocks available.
Repair is not possible.
You need 4 more recovery blocks to be able to repair.
";
        var result = new Par2VerifyResult();
        Par2RepairService.ParseVerificationOutput(sampleOutput, 1, result);

        Assert.Equal(Par2VerificationStatus.RepairNotPossible, result.Status);
        Assert.Equal(4, result.NeededRecoveryBlocks);
        Assert.Equal(1, result.AvailableRecoveryBlocks);
    }

    [Fact]
    public async Task EndToEnd_CreatePar2_Corrupt_And_RepairAsync()
    {
        var exePath = Par2RepairService.Instance.ResolvePar2ExecutablePath();
        Assert.NotNull(exePath);

        var tempDir = Path.Combine(Path.GetTempPath(), "Reepax_Par2E2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var originalContent1 = "The quick brown fox jumps over the lazy dog 1234567890 ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var originalContent2 = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt";

            var file1 = Path.Combine(tempDir, "file1.bin");
            var file2 = Path.Combine(tempDir, "file2.bin");
            var par2File = Path.Combine(tempDir, "archive.par2");

            File.WriteAllText(file1, originalContent1, Encoding.UTF8);
            File.WriteAllText(file2, originalContent2, Encoding.UTF8);

            // 1. Create parity with par2.exe
            var createPsi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"c -s20 -c6 \"{par2File}\" \"{file1}\" \"{file2}\"",
                WorkingDirectory = tempDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(createPsi))
            {
                Assert.NotNull(p);
                await p.WaitForExitAsync();
                Assert.Equal(0, p.ExitCode);
            }

            Assert.True(File.Exists(par2File));

            // 2. Initial verify: should be 100% intact
            var initialVerify = await Par2RepairService.Instance.VerifyAsync(par2File);
            Assert.Equal(Par2VerificationStatus.AllFilesIntact, initialVerify.Status);

            // 3. Corrupt file1
            File.WriteAllText(file1, "CORRUPTED CONTENT REPLACED ALL ORIGINAL DATA", Encoding.UTF8);

            // 4. Verify after corruption: should detect damaged file and repair possible
            var corruptVerify = await Par2RepairService.Instance.VerifyAsync(par2File);
            Assert.Equal(Par2VerificationStatus.RepairPossible, corruptVerify.Status);

            // 5. Repair
            var repairResult = await Par2RepairService.Instance.RepairAsync(par2File, purgeBackups: true);
            Assert.True(repairResult.Success, $"Repair should succeed. Error: {repairResult.ErrorMessage}");

            // 6. Verify restored file content
            var restoredContent1 = File.ReadAllText(file1, Encoding.UTF8);
            Assert.Equal(originalContent1, restoredContent1);

            // 7. Verify parity again: should be 100% intact
            var postRepairVerify = await Par2RepairService.Instance.VerifyAsync(par2File);
            Assert.Equal(Par2VerificationStatus.AllFilesIntact, postRepairVerify.Status);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void DownloadPackage_EnsureNextTaskSteps_IncludesPar2StepWhenPar2ItemPresent()
    {
        var package = new DownloadPackage
        {
            Name = "TestPackage",
            AutoExtractArchives = true,
            AutoPar2Repair = true
        };

        package.Items.Add(new DownloadItem
        {
            FileName = "game.par2",
            SaveFilePath = @"C:\Downloads\game.par2",
            IsEnabled = true
        });

        package.Items.Add(new DownloadItem
        {
            FileName = "game.part01.rar",
            SaveFilePath = @"C:\Downloads\game.part01.rar",
            IsEnabled = true
        });

        package.EnsureNextTaskSteps();

        Assert.True(package.NextTaskSteps.Count >= 2);
        var par2Step = package.NextTaskSteps.FirstOrDefault(s => s.Key == "Par2");
        var extractStep = package.NextTaskSteps.FirstOrDefault(s => s.Key == "Extract");

        Assert.NotNull(par2Step);
        Assert.NotNull(extractStep);
        Assert.True(package.NextTaskSteps.IndexOf(par2Step) < package.NextTaskSteps.IndexOf(extractStep));
    }

    [Fact]
    public void AppSettings_Par2Settings_DefaultsAreDisabled()
    {
        var settings = new AppSettings();
        Assert.False(settings.AutoPar2Repair);
        Assert.False(settings.DeletePar2AfterExtraction);
    }
}
