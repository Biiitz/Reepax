using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.Services.Verification;
using Reepax.ViewModels;
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

    [Fact]
    public void ResolvePar2ExecutablePath_DoesNotCopyPar2ToAppData()
    {
        var exePath = Par2RepairService.Instance.ResolvePar2ExecutablePath();
        Assert.NotNull(exePath);
        Assert.True(File.Exists(exePath));

        // Delete any par2.exe in AppData if left from old versions/tests
        var appDataPar2 = Path.Combine(Reepax.Services.Storage.SettingsService.AppDataDirectory, "par2.exe");
        if (File.Exists(appDataPar2))
        {
            try { File.Delete(appDataPar2); } catch { }
        }

        // Reset cached path
        var field = typeof(Par2RepairService).GetField("_cachedExecutablePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(Par2RepairService.Instance, null);

        var resolvedPath = Par2RepairService.Instance.ResolvePar2ExecutablePath();
        Assert.NotNull(resolvedPath);
        Assert.False(File.Exists(appDataPar2), "par2.exe must not be copied to AppDataDirectory!");
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
        Par2RepairService.ParseVerificationOutput(sampleOutput, 2, result);

        Assert.Equal(Par2VerificationStatus.RepairNotPossible, result.Status);
        Assert.Equal(4, result.NeededRecoveryBlocks);
        Assert.Equal(1, result.AvailableRecoveryBlocks);
    }

    [Theory]
    [InlineData(true, false, "file1.bin")]
    [InlineData(false, false, "file1.bin")]
    [InlineData(true, true, "file1.bin")]
    [InlineData(true, false, "All files are correct, repair is not required.bin")]
    [InlineData(false, false, "Repair is possible - Repair complete.bin")]
    public async Task EndToEnd_CreatePar2_Corrupt_And_RepairAsync(bool purgeBackups, bool useVolume, string fileName)
    {
        var exePath = Par2RepairService.Instance.ResolvePar2ExecutablePath();
        Assert.NotNull(exePath);

        var tempDir = Path.Combine(Path.GetTempPath(), "Reepax_Par2E2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var originalContent1 = "The quick brown fox jumps over the lazy dog 1234567890 ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var originalContent2 = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt";

            var file1 = Path.Combine(tempDir, fileName);
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

            var patchFile = Path.Combine(tempDir, "patch.1");
            File.WriteAllText(patchFile, "legitimate patch content");
            var unrelatedFile = Path.Combine(tempDir, "document.txt");
            var unrelatedBackup = unrelatedFile + ".1";
            File.WriteAllText(unrelatedFile, "current document");
            File.WriteAllText(unrelatedBackup, "previous document");

            // 4. Verify after corruption: should detect damaged file and repair possible
            var corruptVerify = await Par2RepairService.Instance.VerifyAsync(par2File);
            Assert.Equal(Par2VerificationStatus.RepairPossible, corruptVerify.Status);

            File.WriteAllText(file1 + ".1", "independent backup");
            var repairInput = useVolume
                ? Directory.GetFiles(tempDir, "*.vol*.par2").First()
                : par2File;

            // 5. Repair
            var repairResult = await Par2RepairService.Instance.RepairAsync(repairInput, purgeBackups);
            Assert.True(repairResult.Success, $"Repair should succeed. Error: {repairResult.ErrorMessage}");

            Assert.True(File.Exists(patchFile), "patch.1 must be preserved after PAR2 repair cleanup");
            Assert.Equal("current document", File.ReadAllText(unrelatedFile));
            Assert.Equal("previous document", File.ReadAllText(unrelatedBackup));
            Assert.Equal("independent backup", File.ReadAllText(file1 + ".1"));
            Assert.Equal(!purgeBackups, File.Exists(file1 + ".2"));
            if (!purgeBackups)
                Assert.Equal("CORRUPTED CONTENT REPLACED ALL ORIGINAL DATA", File.ReadAllText(file1 + ".2", Encoding.UTF8));

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

    [Theory]
    [InlineData("All files are correct, repair is not required.bin", false)]
    [InlineData("Repair is possible - Repair complete.bin", false)]
    [InlineData("All files are correct, repair is not required.bin", true)]
    [InlineData("Repair is possible - Repair complete.bin", true)]
    public async Task UnrepairableFiles_WithOutcomeTextInNames_DoNotReportSuccess(string fileName, bool missing)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Par2Outcome_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            byte[] original = Enumerable.Range(0, 1024).Select(index => (byte)((index * 37 + 11) % 251)).ToArray();
            byte[] damaged = Enumerable.Repeat((byte)255, original.Length).ToArray();
            byte[] independent = Encoding.UTF8.GetBytes("independent original and backup");
            string file = Path.Combine(directory, fileName);
            File.WriteAllBytes(file, original);
            string parity = await CreateParityAsync(directory, file);
            File.WriteAllBytes(file + ".1", independent);
            string unrelated = Path.Combine(directory, "unrelated.bin");
            File.WriteAllBytes(unrelated, independent);
            File.WriteAllBytes(unrelated + ".1", independent);
            if (missing) File.Delete(file);
            else File.WriteAllBytes(file, damaged);

            var service = Par2RepairService.Instance;
            var verification = await service.VerifyAsync(parity, ct: timeout.Token);
            Assert.Equal(Par2VerificationStatus.RepairNotPossible, verification.Status);
            Assert.False(verification.IsRepairedOrIntact);
            Assert.True(verification.NeededRecoveryBlocks > 0);
            var repair = await service.RepairAsync(parity, ct: timeout.Token);
            Assert.False(repair.Success);
            Assert.False(string.IsNullOrWhiteSpace(repair.ErrorMessage));

            var package = new DownloadPackage
            {
                Name = "Example package", SaveDirectory = directory, AutoExtractArchives = true, AutoPar2Repair = true
            };
            package.Items.Add(new DownloadItem { PackageId = package.Id, FileName = fileName, SaveFilePath = file,
                Status = DownloadStatus.Completed, TotalBytes = original.Length, DownloadedBytes = original.Length });
            package.Items.Add(new DownloadItem { PackageId = package.Id, FileName = Path.GetFileName(parity),
                SaveFilePath = parity, Status = DownloadStatus.Completed });
            package.EnsureNextTaskSteps();
            Assert.False(await service.ProcessPackagePar2Async(package, ct: timeout.Token));
            if (!missing)
            {
                var viewModel = new Reepax.ViewModels.MainViewModel();
                await viewModel.Par2RepairPackageCommand.ExecuteAsync(package);
                Assert.Equal(Reepax.Services.Localization.Loc.Get("Status_Par2RepairFailedGeneric"), viewModel.StatusSummary);
                Assert.Contains(package.NextTaskSteps, step => step.Key == "Par2" && step.State == NextTaskStepState.Failed);
            }

            Assert.Equal(!missing, File.Exists(file));
            if (!missing) Assert.Equal(damaged, File.ReadAllBytes(file));
            Assert.Equal(independent, File.ReadAllBytes(file + ".1"));
            Assert.Equal(independent, File.ReadAllBytes(unrelated));
            Assert.Equal(independent, File.ReadAllBytes(unrelated + ".1"));
            Assert.False(File.Exists(file + ".2"));
            Assert.Empty(service.GetActiveRepairs());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("en", "unrepairable", false)]
    [InlineData("de", "unrepairable", true)]
    [InlineData("en", "cancelled", false)]
    [InlineData("de", "error", false)]
    [InlineData("en", "callback-error", false)]
    public async Task ManualRepair_FailureEndsStepAndSuccessfulRetryPreservesData(string language, string failure, bool missing)
    {
        string directory = Directory.CreateTempSubdirectory("ManualPar2_").FullName;
        var service = Par2RepairService.Instance;
        var previousHook = service.ProcessStarted;
        var actions = PostDownloadActionService.Instance;
        var previousAction = actions.CurrentAction;
        var settings = SettingsService.Instance.Settings;
        var previousLanguage = settings.Language;
        string previousCurrentLanguage = LocalizationService.Instance.CurrentLanguage;
        try
        {
            actions.CancelCountdown();
            actions.CurrentAction = PostDownloadAction.None;
            var viewModel = new MainViewModel();
            LocalizationService.Instance.CurrentLanguage = language;
            byte[] original = Enumerable.Range(0, 80).Select(index => (byte)(index * 3)).ToArray();
            byte[] damaged = Enumerable.Repeat((byte)255, original.Length).ToArray();
            byte[] independent = Encoding.UTF8.GetBytes("independent installed result and backup");
            string file = Path.Combine(directory, "payload.bin");
            File.WriteAllBytes(file, original);
            string parity = await CreateParityAsync(directory, file);
            string[] volumes = Directory.GetFiles(directory, "data.vol*.par2");
            string recovery = Directory.CreateDirectory(Path.Combine(directory, "recovery")).FullName;
            if (failure == "unrepairable")
                foreach (string volume in volumes) File.Move(volume, Path.Combine(recovery, Path.GetFileName(volume)));
            if (missing) File.Delete(file);
            else File.WriteAllBytes(file, damaged);
            File.WriteAllBytes(file + ".1", independent);
            string installed = Path.Combine(directory, "installed.bin");
            File.WriteAllBytes(installed, independent);
            File.WriteAllBytes(installed + ".1", independent);

            string expected = Convert.ToHexString(SHA256.HashData(original));
            var package = new DownloadPackage { Name = "Example package", SaveDirectory = directory,
                AutoExtractArchives = true, AutoPar2Repair = true, Status = DownloadStatus.Completed };
            var item = new DownloadItem { PackageId = package.Id, FileName = "payload.bin", SaveFilePath = file,
                Status = DownloadStatus.Completed, TotalBytes = original.Length, DownloadedBytes = original.Length,
                ExpectedChecksum = expected, CalculatedChecksum = expected };
            package.Items.Add(item);
            package.Items.Add(new DownloadItem { PackageId = package.Id, FileName = "data.par2", SaveFilePath = parity,
                Status = DownloadStatus.Completed, TotalBytes = new FileInfo(parity).Length });
            package.EnsureNextTaskSteps();
            package.SetNextTaskDone("Extract");
            package.SetNextTaskDone("Par2");
            bool observedRunning = false;
            service.ProcessStarted = (_, _) =>
            {
                observedRunning = MainViewModel.MatchesStatusFilter(package, DownloadStatusFilter.Running);
                if (failure == "cancelled") throw new OperationCanceledException();
                if (failure == "error") throw new IOException("Simulated native operation failure.");
            };
            System.ComponentModel.PropertyChangedEventHandler fault = (_, args) =>
            {
                if (args.PropertyName == nameof(DownloadPackage.StatusMessage) &&
                    package.StatusMessage == Loc.Get("Status_Par2Verifying"))
                    throw new IOException("Simulated status observer failure.");
            };
            if (failure == "callback-error") package.PropertyChanged += fault;
            try
            {
                if (failure == "callback-error")
                    await Assert.ThrowsAsync<IOException>(() => viewModel.Par2RepairPackageCommand.ExecuteAsync(package));
                else
                    await viewModel.Par2RepairPackageCommand.ExecuteAsync(package);
            }
            finally { package.PropertyChanged -= fault; }
            service.ProcessStarted = previousHook;

            Assert.Contains(package.NextTaskSteps, step => step.Key == "Par2" && step.State == NextTaskStepState.Failed);
            Assert.Equal(Loc.Get("Status_Par2RepairFailedGeneric"), viewModel.StatusSummary);
            Assert.False(package.IsFullyCompleted);
            Assert.False(PostDownloadActionService.CheckHasActiveWork(new[] { package }));
            Assert.False(MainViewModel.MatchesStatusFilter(package, DownloadStatusFilter.Running));
            Assert.True(MainViewModel.MatchesStatusFilter(package, DownloadStatusFilter.Failed));
            Assert.False(package.HasCompletedNotified);
            Assert.Empty(service.GetActiveRepairs());
            if (failure != "callback-error") Assert.True(observedRunning);
            actions.CurrentAction = PostDownloadAction.Sleep;
            actions.NotifyWorkStarted();
            actions.Evaluate(new[] { package });
            Assert.False(actions.IsCountdownActive);
            Assert.Equal(!missing, File.Exists(file));
            if (!missing) Assert.Equal(damaged, File.ReadAllBytes(file));
            Assert.Equal(independent, File.ReadAllBytes(file + ".1"));
            Assert.Equal(independent, File.ReadAllBytes(installed));
            Assert.Equal(independent, File.ReadAllBytes(installed + ".1"));

            if (failure == "unrepairable")
                foreach (string volume in volumes) File.Move(Path.Combine(recovery, Path.GetFileName(volume)), volume);
            await viewModel.Par2RepairPackageCommand.ExecuteAsync(package);

            Assert.Contains(package.NextTaskSteps, step => step.Key == "Par2" && step.State == NextTaskStepState.Done);
            Assert.Equal(Loc.Get("Status_Par2Repaired"), viewModel.StatusSummary);
            Assert.True(package.IsFullyCompleted);
            Assert.False(MainViewModel.MatchesStatusFilter(package, DownloadStatusFilter.Running));
            Assert.False(MainViewModel.MatchesStatusFilter(package, DownloadStatusFilter.Failed));
            Assert.Equal(original, File.ReadAllBytes(file));
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            Assert.Equal(expected, item.ExpectedChecksum);
            Assert.Null(item.CalculatedChecksum);
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(independent, File.ReadAllBytes(file + ".1"));
            Assert.Equal(independent, File.ReadAllBytes(installed));
            Assert.Equal(independent, File.ReadAllBytes(installed + ".1"));
            Assert.False(File.Exists(file + ".2"));
            Assert.Empty(service.GetActiveRepairs());
            actions.Evaluate(new[] { package });
            Assert.True(actions.IsCountdownActive);
        }
        finally
        {
            service.ProcessStarted = previousHook;
            actions.CancelCountdown();
            actions.CurrentAction = previousAction;
            settings.Language = previousLanguage;
            LocalizationService.Instance.CurrentLanguage = previousCurrentLanguage;
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ManualRepair_WithoutAutomaticExtractionIsRunningOnlyWhileNativeWorkIsActive()
    {
        string directory = Directory.CreateTempSubdirectory("ManualPar2Activity_").FullName;
        var service = Par2RepairService.Instance;
        var previousHook = service.ProcessStarted;
        string previousLanguage = LocalizationService.Instance.CurrentLanguage;
        try
        {
            var viewModel = new MainViewModel();
            LocalizationService.Instance.CurrentLanguage = "de";
            string file = Path.Combine(directory, "payload.bin");
            File.WriteAllBytes(file, Enumerable.Range(0, 80).Select(index => (byte)index).ToArray());
            string parity = await CreateParityAsync(directory, file);
            var package = new DownloadPackage { SaveDirectory = directory, AutoExtractArchives = false,
                Status = DownloadStatus.Completed };
            package.Items.Add(new DownloadItem { PackageId = package.Id, FileName = "payload.bin", SaveFilePath = file,
                Status = DownloadStatus.Completed });
            package.Items.Add(new DownloadItem { PackageId = package.Id, FileName = "data.par2", SaveFilePath = parity,
                Status = DownloadStatus.Completed });
            bool observedRunning = false;
            service.ProcessStarted = (_, _) => observedRunning = MainViewModel.MatchesStatusFilter(package, DownloadStatusFilter.Running);

            await viewModel.Par2RepairPackageCommand.ExecuteAsync(package);

            Assert.True(observedRunning);
            Assert.True(package.IsFullyCompleted);
            Assert.False(MainViewModel.MatchesStatusFilter(package, DownloadStatusFilter.Running));
            Assert.Empty(package.NextTaskSteps);
            Assert.Empty(service.GetActiveRepairs());
        }
        finally
        {
            service.ProcessStarted = previousHook;
            LocalizationService.Instance.CurrentLanguage = previousLanguage;
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task PackageRepair_WithForeignTarget_PreservesBothPackagesAndAllowsRetry(bool manual, bool volumeMetadata)
    {
        string directory = Directory.CreateTempSubdirectory("SharedPar2_").FullName;
        var queue = QueueManager.Instance;
        bool previousTestMode = DownloadPersistenceService.IsTestEnvironment;
        var package = new DownloadPackage
        {
            Name = "Example archive", SaveDirectory = directory, AutoExtractArchives = true,
            AutoPar2Repair = true, DeleteArchiveAfterExtraction = false
        };
        var other = new DownloadPackage { Name = "Example neighbor", SaveDirectory = directory };
        try
        {
            DownloadPersistenceService.IsTestEnvironment = true;
            var viewModel = manual ? new MainViewModel() : null;
            string archive = Path.Combine(directory, "archive.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("payload.bin").Open())
                entry.Write(Encoding.UTF8.GetBytes("example archive content"));
            byte[] original = File.ReadAllBytes(archive);
            byte[] damaged = original.ToArray();
            damaged[40] ^= 0x5a;
            string neighbor = Path.Combine(directory, "neighbor.bin");
            byte[] expectedNeighbor = Enumerable.Range(1, 80).Select(index => (byte)index).ToArray();
            byte[] actualNeighbor = Enumerable.Repeat((byte)255, expectedNeighbor.Length).ToArray();
            byte[] independentBackup = Encoding.UTF8.GetBytes("independent backup");
            File.WriteAllBytes(neighbor, expectedNeighbor);
            string parity = await CreateParityAsync(directory, archive, neighbor);
            if (volumeMetadata) RemoveFileDescriptions(parity);
            File.WriteAllBytes(archive, damaged);
            File.WriteAllBytes(neighbor, actualNeighbor);
            File.WriteAllBytes(neighbor + ".1", independentBackup);

            DownloadItem archiveItem = AddCompletedItem(package, archive);
            DownloadItem neighborItem = AddCompletedItem(other, neighbor);
            AddCompletedItem(package, parity);
            package.EnsureNextTaskSteps();
            queue.Packages.Add(package);
            queue.Packages.Add(other);

            async Task RunAsync()
            {
                if (viewModel != null)
                    await viewModel.Par2RepairPackageCommand.ExecuteAsync(package);
                else
                    await ArchiveExtractionService.Instance.CheckAndExtractPackageAsync(package);
            }

            string? archiveHash = archiveItem.CalculatedChecksum;
            string? neighborHash = neighborItem.CalculatedChecksum;
            await RunAsync();
            Assert.Equal(actualNeighbor, File.ReadAllBytes(neighbor));
            Assert.Equal(damaged, File.ReadAllBytes(archive));
            Assert.Equal(independentBackup, File.ReadAllBytes(neighbor + ".1"));
            Assert.False(File.Exists(neighbor + ".2"));
            Assert.Equal(archiveHash, archiveItem.CalculatedChecksum);
            Assert.Equal(neighborHash, neighborItem.CalculatedChecksum);
            Assert.False(package.IsExtracted);
            Assert.Contains(package.NextTaskSteps, step => step.Key == "Par2" && step.State == NextTaskStepState.Failed);
            Assert.Empty(Par2RepairService.Instance.GetActiveRepairs());

            string superseded = Directory.CreateDirectory(Path.Combine(directory, "superseded")).FullName;
            foreach (string path in Directory.GetFiles(directory, "*.par2"))
                File.Move(path, Path.Combine(superseded, Path.GetFileName(path)));
            File.WriteAllBytes(archive, original);
            await CreateParityAsync(directory, archive);
            if (volumeMetadata) File.WriteAllBytes(parity, []);
            File.WriteAllBytes(archive, damaged);
            await RunAsync();

            Assert.Equal(original, File.ReadAllBytes(archive));
            Assert.Equal(actualNeighbor, File.ReadAllBytes(neighbor));
            Assert.Equal(independentBackup, File.ReadAllBytes(neighbor + ".1"));
            Assert.Equal(neighborHash, neighborItem.CalculatedChecksum);
            Assert.Null(archiveItem.CalculatedChecksum);
            Assert.Contains(package.NextTaskSteps, step => step.Key == "Par2" && step.State == NextTaskStepState.Done);
            Assert.Equal(!manual, package.IsExtracted);
        }
        finally
        {
            queue.Packages.Remove(other);
            queue.Packages.Remove(package);
            DownloadPersistenceService.IsTestEnvironment = previousTestMode;
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    public async Task PackageRepair_RepairsMissingVolumeOnlyWithinPackageScope(bool dedicated, bool listed, bool repaired)
    {
        string directory = Directory.CreateTempSubdirectory(dedicated ? "ReepaxPkg_" : "SharedPar2_").FullName;
        try
        {
            string first = Path.Combine(directory, "Archivé.7z.001");
            string second = Path.Combine(directory, "Archivé.7z.002");
            byte[] firstBytes = Enumerable.Range(0, 80).Select(index => (byte)index).ToArray();
            byte[] secondBytes = Enumerable.Range(80, 80).Select(index => (byte)index).ToArray();
            File.WriteAllBytes(first, firstBytes);
            File.WriteAllBytes(second, secondBytes);
            string index = await CreateParityAsync(directory, first, second);
            File.Delete(index);
            string parity = Directory.GetFiles(directory, "*.par2").First();
            var package = new DownloadPackage { Name = "Example package", SaveDirectory = directory };
            AddCompletedItem(package, first);
            if (listed) AddCompletedItem(package, second);
            AddCompletedItem(package, parity);
            File.Delete(second);

            var service = new Par2RepairService();
            Assert.Equal(Par2VerificationStatus.RepairPossible, (await service.VerifyAsync(parity)).Status);
            Assert.Equal(repaired, await service.ProcessPackagePar2Async(package));
            Assert.Equal(firstBytes, File.ReadAllBytes(first));
            Assert.Equal(repaired, File.Exists(second));
            if (repaired) Assert.Equal(secondBytes, File.ReadAllBytes(second));
            Assert.Empty(service.GetActiveRepairs());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void RemoveFileDescriptions(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using var stream = File.Create(path);
        for (int offset = 0; offset < bytes.Length;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8, 8)));
            if (!bytes.AsSpan(offset + 48, 16).SequenceEqual("PAR 2.0\0FileDesc"u8))
                stream.Write(bytes.AsSpan(offset, length));
            offset += length;
        }
    }

    private static DownloadItem AddCompletedItem(DownloadPackage package, string path)
    {
        var item = new DownloadItem
        {
            PackageId = package.Id, FileName = Path.GetFileName(path), SaveFilePath = path, Status = DownloadStatus.Completed,
            TotalBytes = new FileInfo(path).Length, DownloadedBytes = new FileInfo(path).Length,
            CalculatedChecksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        };
        package.Items.Add(item);
        return item;
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void ParseVerificationOutput_ErrorCodeCannotBeOverriddenByOutcomeText(int exitCode)
    {
        var result = new Par2VerifyResult();
        const string output = "File: \"All files are correct, repair is not required - Repair complete.bin\" - damaged.\nRepair is possible.\n";

        Par2RepairService.ParseVerificationOutput(output, exitCode, result);

        Assert.Equal(Par2VerificationStatus.Failed, result.Status);
        Assert.False(result.IsRepairedOrIntact);
    }

    [Theory]
    [InlineData(false, "data.par2")]
    [InlineData(true, "data.par2")]
    [InlineData(false, "Repair complete.par2")]
    [InlineData(false, "All files are correct, repair is not required.par2")]
    public async Task RepairAsync_InvalidParity_PreservesUnrelatedFileAndBackup(bool cancel, string fileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RepairBackup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var parityFile = Path.Combine(tempDir, fileName);
            var document = Path.Combine(tempDir, "document.txt");
            File.WriteAllText(parityFile, "invalid parity data");
            File.WriteAllText(document, "current document");
            File.WriteAllText(document + ".1", "previous document");

            using var cancellation = new System.Threading.CancellationTokenSource();
            if (cancel)
                cancellation.Cancel();
            var result = await Par2RepairService.Instance.RepairAsync(parityFile, ct: cancellation.Token);

            Assert.False(result.Success);
            Assert.Equal("current document", File.ReadAllText(document));
            Assert.Equal("previous document", File.ReadAllText(document + ".1"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
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
            FileName = "data.par2",
            SaveFilePath = @"C:\Downloads\data.par2",
            IsEnabled = true
        });

        package.Items.Add(new DownloadItem
        {
            FileName = "data.part01.rar",
            SaveFilePath = @"C:\Downloads\data.part01.rar",
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
    public void AppSettings_Par2Settings_DefaultsAreEnabled()
    {
        var settings = new AppSettings();
        Assert.True(settings.AutoPar2Repair);
        Assert.True(settings.DeletePar2AfterExtraction);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task BackupScope_HandlesOnlyNewBackupsOfRepairTargets(bool restore, bool missingOriginal)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RepairBackup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var original = Path.Combine(tempDir, "payload");
            var document = Path.Combine(tempDir, "document.txt");
            File.WriteAllText(original, "original payload");
            File.WriteAllText(original + ".1", "independent target backup");
            File.WriteAllText(document, "current document");
            File.WriteAllText(document + ".1", "previous document");
            var parityFile = await CreateParityAsync(tempDir, original);
            using var scope = Par2RepairService.RepairBackupScope.Capture(parityFile);
            File.Move(original, original + ".2");
            if (!missingOriginal)
                File.WriteAllText(original, "replacement payload");
            var concurrentDocument = Path.Combine(tempDir, "other.txt");
            File.WriteAllText(concurrentDocument, "other current");
            File.WriteAllText(concurrentDocument + ".1", "other previous");

            if (restore)
                scope.Restore();
            else
                scope.Clean();

            Assert.Equal("current document", File.ReadAllText(document));
            Assert.Equal("previous document", File.ReadAllText(document + ".1"));
            Assert.Equal("other current", File.ReadAllText(concurrentDocument));
            Assert.Equal("other previous", File.ReadAllText(concurrentDocument + ".1"));
            Assert.Equal("independent target backup", File.ReadAllText(original + ".1"));
            if (restore)
            {
                Assert.Equal("original payload", File.ReadAllText(original));
                Assert.False(File.Exists(original + ".2"));
            }
            else
            {
                Assert.Equal(missingOriginal, File.Exists(original + ".2"));
                if (!missingOriginal)
                    Assert.Equal("replacement payload", File.ReadAllText(original));
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BackupScope_NewUnrelatedFileAtExpectedBackupPath_IsPreserved()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RepairBackup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var original = Path.Combine(tempDir, "payload.bin");
            File.WriteAllText(original, "current payload");
            var parityFile = await CreateParityAsync(tempDir, original);
            using var scope = Par2RepairService.RepairBackupScope.Capture(parityFile);
            File.WriteAllText(original + ".1", "independent backup");

            scope.Clean();
            scope.Restore();

            Assert.Equal("current payload", File.ReadAllText(original));
            Assert.Equal("independent backup", File.ReadAllText(original + ".1"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BackupScope_LockedReplacement_PreservesBothFilesOnRestoreFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RepairBackup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var original = Path.Combine(tempDir, "payload.bin");
            File.WriteAllText(original, "original payload");
            var parityFile = await CreateParityAsync(tempDir, original);
            using var scope = Par2RepairService.RepairBackupScope.Capture(parityFile);
            File.Move(original, original + ".1");
            File.WriteAllText(original, "replacement payload");
            using (File.Open(original, FileMode.Open, FileAccess.Read, FileShare.Read))
                scope.Restore();

            Assert.Equal("replacement payload", File.ReadAllText(original));
            Assert.Equal("original payload", File.ReadAllText(original + ".1"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BackupScope_WithoutValidMetadata_PreservesNewBackups()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RepairBackup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var original = Path.Combine(tempDir, "payload.bin");
            File.WriteAllText(original, "original payload");
            var parityFile = await CreateParityAsync(tempDir, original);
            var bytes = File.ReadAllBytes(parityFile);
            for (var offset = 0; offset + 64 <= bytes.Length;)
            {
                bytes[offset + 16] ^= 0xff;
                offset += checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8, 8)));
            }
            File.WriteAllBytes(parityFile, bytes);
            using var scope = Par2RepairService.RepairBackupScope.Capture(parityFile);
            File.Move(original, original + ".1");
            File.WriteAllText(original, "current payload");

            scope.Clean();
            scope.Restore();

            Assert.Equal("current payload", File.ReadAllText(original));
            Assert.Equal("original payload", File.ReadAllText(original + ".1"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static async Task<string> CreateParityAsync(string directory, params string[] files)
    {
        var parityFile = Path.Combine(directory, "data.par2");
        var startInfo = new ProcessStartInfo
        {
            FileName = Par2RepairService.Instance.ResolvePar2ExecutablePath(),
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("c");
        startInfo.ArgumentList.Add("-s20");
        startInfo.ArgumentList.Add("-c6");
        startInfo.ArgumentList.Add(parityFile);
        foreach (var file in files)
            startInfo.ArgumentList.Add(file);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        Assert.True(process.ExitCode == 0, await output + await error);
        return parityFile;
    }
}
