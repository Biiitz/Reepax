using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.Verification;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class AsyncDiskCleanupTests
{
    [Fact]
    public async Task Removal_KeepsTheDispatcherResponsiveDuringDiskWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "TargetFolder");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "data.bin");
        File.WriteAllText(file, "data");
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var dispatcher = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task? removal = null;
        try
        {
            removal = await dispatcher.InvokeAsync(() =>
            {
                var vm = new MainViewModel();
                vm.Packages.Clear();
                var package = new DownloadPackage { Name = "TargetFolder", SaveDirectory = directory, AutoPar2Repair = false };
                package.Items.Add(new DownloadItem { FileName = "data.bin", SaveFilePath = file });
                vm.Packages.Add(package);
                vm.DiskCleanupRunner = action => Task.Run(async () =>
                {
                    entered.TrySetResult(true);
                    await proceed.Task;
                    action();
                });
                return vm.RemovePackage(package, true);
            }).Task;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(removal.IsCompleted);
            bool responsive = await dispatcher.InvokeAsync(() => true).Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(responsive);
            Assert.True(File.Exists(file));
            proceed.TrySetResult(true);
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(file));
        }
        finally
        {
            proceed.TrySetResult(true);
            if (removal != null)
                await removal.WaitAsync(TimeSpan.FromSeconds(10));
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_PreservesFilesOwnedByAnotherPackageInASubdirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "TargetFolder");
        var nested = Path.Combine(directory, "NestedFolder");
        Directory.CreateDirectory(nested);
        var selectedFile = Path.Combine(directory, "selected.bin");
        var otherFile = Path.Combine(nested, "other.bin");
        File.WriteAllText(selectedFile, "selected");
        File.WriteAllText(otherFile, "other");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var selected = new DownloadPackage { Name = "TargetFolder", SaveDirectory = directory, AutoPar2Repair = false };
        selected.Items.Add(new DownloadItem { FileName = "selected.bin", SaveFilePath = selectedFile });
        var other = new DownloadPackage { Name = "NestedFolder", SaveDirectory = nested, AutoPar2Repair = false };
        other.Items.Add(new DownloadItem { FileName = "other.bin", SaveFilePath = otherFile });
        vm.Packages.Add(selected);
        vm.Packages.Add(other);
        try
        {
            await vm.RemovePackage(selected, true);
            Assert.DoesNotContain(selected, vm.Packages);
            Assert.Contains(other, vm.Packages);
            Assert.False(File.Exists(selectedFile));
            Assert.Equal("other", File.ReadAllText(otherFile));
        }
        finally
        {
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_PreservesAFileSharedWithAnotherItem()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "shared.bin");
        File.WriteAllText(file, "shared");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var package = new DownloadPackage { Name = "TargetFolder", SaveDirectory = root, AutoPar2Repair = false };
        var selected = new DownloadItem { FileName = "shared.bin", SaveFilePath = file };
        var other = new DownloadItem { FileName = "shared.bin", SaveFilePath = file };
        package.Items.Add(selected);
        package.Items.Add(other);
        vm.Packages.Add(package);
        try
        {
            await vm.RemoveItem(selected, true);
            Assert.DoesNotContain(selected, package.Items);
            Assert.Contains(other, package.Items);
            Assert.Equal("shared", File.ReadAllText(file));
        }
        finally
        {
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_PreservesAnEmptyPackageSharingTheDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "TargetFolder");
        Directory.CreateDirectory(directory);
        var selectedFile = Path.Combine(directory, "selected.bin");
        var otherFile = Path.Combine(directory, "other.bin");
        File.WriteAllText(selectedFile, "selected");
        File.WriteAllText(otherFile, "other");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var selected = new DownloadPackage { Name = "TargetFolder", SaveDirectory = directory, AutoPar2Repair = false };
        selected.Items.Add(new DownloadItem { FileName = "selected.bin", SaveFilePath = selectedFile });
        var other = new DownloadPackage { Name = "OtherFolder", SaveDirectory = directory, AutoPar2Repair = false };
        vm.Packages.Add(selected);
        vm.Packages.Add(other);
        try
        {
            await vm.RemovePackage(selected, true);
            Assert.Contains(other, vm.Packages);
            Assert.False(File.Exists(selectedFile));
            Assert.Equal("other", File.ReadAllText(otherFile));
        }
        finally
        {
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_StopsWhenTheConfirmedPathHasChanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "TargetFolder");
        var second = Path.Combine(root, "ChangedFolder");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var firstFile = Path.Combine(first, "data.bin");
        var secondFile = Path.Combine(second, "data.bin");
        File.WriteAllText(firstFile, "first");
        File.WriteAllText(secondFile, "second");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var package = new DownloadPackage { Name = "TargetFolder", SaveDirectory = first, AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = firstFile };
        package.Items.Add(item);
        vm.Packages.Add(package);
        vm.CleanupConfirmationOverride = _ =>
        {
            package.SaveDirectory = second;
            item.SaveFilePath = secondFile;
            return (true, true);
        };
        try
        {
            await vm.RemovePackage(package);
            Assert.Contains(package, vm.Packages);
            Assert.Equal(Loc.Get("Status_CleanupChanged"), vm.StatusSummary);
            Assert.Equal("first", File.ReadAllText(firstFile));
            Assert.Equal("second", File.ReadAllText(secondFile));
            Assert.False(item.IsRemovalPending);
        }
        finally
        {
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_CancelledAtConfirmationKeepsThePackageAndFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "data.bin");
        File.WriteAllText(file, "data");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var package = new DownloadPackage { Name = "TargetFolder", SaveDirectory = root, AutoPar2Repair = false };
        package.Items.Add(new DownloadItem { FileName = "data.bin", SaveFilePath = file });
        vm.Packages.Add(package);
        string? option = null;
        vm.CleanupConfirmationOverride = value => { option = value; return (false, false); };
        try
        {
            await vm.RemovePackage(package);
            Assert.NotNull(option);
            Assert.Contains(package, vm.Packages);
            Assert.Equal("data", File.ReadAllText(file));
            Assert.False(vm.IsRemovalInProgress);
        }
        finally
        {
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_DefersAnotherPackagesAutomaticRenameUntilDiskWorkFinishes()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "TargetFolder");
        var otherDirectory = Path.Combine(root, "Download_Package");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(otherDirectory);
        var targetFile = Path.Combine(target, "selected.bin");
        var otherFile = Path.Combine(otherDirectory, "download_file");
        File.WriteAllText(targetFile, "selected");
        File.WriteAllText(otherFile, "other");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var selected = new DownloadPackage { Name = "TargetFolder", SaveDirectory = target, AutoPar2Repair = false };
        selected.Items.Add(new DownloadItem { FileName = "selected.bin", SaveFilePath = targetFile });
        var other = new DownloadPackage { Name = "Download_Package", SaveDirectory = otherDirectory, AutoPar2Repair = false };
        var otherItem = new DownloadItem { FileName = "download_file", SaveFilePath = otherFile };
        other.Items.Add(otherItem);
        vm.Packages.Add(selected);
        vm.Packages.Add(other);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renamed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        other.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DownloadPackage.Name))
                renamed.TrySetResult(true);
        };
        vm.DiskCleanupRunner = action => Task.Run(async () =>
        {
            entered.TrySetResult(true);
            await proceed.Task;
            action();
        });
        Task? removal = null;
        try
        {
            removal = vm.RemovePackage(selected, true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            otherItem.Rename("TargetFolder.zip", isUserAction: false);
            Assert.Equal("Download_Package", other.Name);
            proceed.TrySetResult(true);
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            await renamed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(selected, vm.Packages);
            Assert.Contains(other, vm.Packages);
            Assert.Equal("other", File.ReadAllText(otherItem.SaveFilePath));
        }
        finally
        {
            proceed.TrySetResult(true);
            if (removal != null)
                await removal.WaitAsync(TimeSpan.FromSeconds(10));
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_DeletesAMixedSelectionWithoutRemovingOtherItems()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var firstDirectory = Path.Combine(root, "FirstFolder");
        var secondDirectory = Path.Combine(root, "SecondFolder");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstFile = Path.Combine(firstDirectory, "first.bin");
        var secondFile = Path.Combine(secondDirectory, "second.bin");
        var otherFile = Path.Combine(secondDirectory, "other.bin");
        File.WriteAllText(firstFile, "first");
        File.WriteAllText(secondFile, "second");
        File.WriteAllText(otherFile, "other");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var first = new DownloadPackage { Name = "FirstFolder", SaveDirectory = firstDirectory, IsSelected = true, AutoPar2Repair = false };
        first.Items.Add(new DownloadItem { FileName = "first.bin", SaveFilePath = firstFile });
        var second = new DownloadPackage { Name = "SecondFolder", SaveDirectory = secondDirectory, AutoPar2Repair = false };
        var selectedItem = new DownloadItem { FileName = "second.bin", SaveFilePath = secondFile, IsSelected = true };
        var otherItem = new DownloadItem { FileName = "other.bin", SaveFilePath = otherFile };
        second.Items.Add(selectedItem);
        second.Items.Add(otherItem);
        vm.Packages.Add(first);
        vm.Packages.Add(second);
        vm.CleanupConfirmationOverride = _ => (true, true);
        try
        {
            await vm.DeleteSelected();
            Assert.DoesNotContain(first, vm.Packages);
            Assert.Contains(second, vm.Packages);
            Assert.DoesNotContain(selectedItem, second.Items);
            Assert.Contains(otherItem, second.Items);
            Assert.False(File.Exists(firstFile));
            Assert.False(File.Exists(secondFile));
            Assert.Equal("other", File.ReadAllText(otherFile));
        }
        finally
        {
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_DiskWorkFailureKeepsTheSelection()
    {
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var package = new DownloadPackage { Name = "TargetFolder", AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin" };
        package.Items.Add(item);
        vm.Packages.Add(package);
        vm.DiskCleanupRunner = _ => Task.FromException(new IOException());
        try
        {
            await vm.RemovePackage(package, true);
            Assert.Contains(package, vm.Packages);
            Assert.Contains(item, package.Items);
            Assert.False(item.IsRemovalPending);
            Assert.False(vm.IsRemovalInProgress);
            Assert.Equal(Loc.Get("Status_CleanupFailed"), vm.StatusSummary);
        }
        finally
        {
            vm.Packages.Clear();
        }
    }

    [Fact]
    public async Task Removal_WaitsForARepairUsingTheSelectedPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "TargetFolder");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "data.bin");
        File.WriteAllText(file, "data");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var package = new DownloadPackage { Name = "TargetFolder", SaveDirectory = directory, AutoPar2Repair = false };
        package.Items.Add(new DownloadItem { FileName = "data.bin", SaveFilePath = file });
        vm.Packages.Add(package);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.DiskCleanupRunner = action => Task.Run(() => { action(); scanned.TrySetResult(true); });
        var repair = Par2RepairService.Instance.RunTrackedPackageOperationAsync(package, async () =>
        {
            started.TrySetResult(true);
            await proceed.Task;
            return true;
        });
        Task? removal = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            removal = vm.RemovePackage(package, true);
            await scanned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (await PackagePathOperationGate.Instance.EnterAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
            {
                Assert.False(removal.IsCompleted);
                Assert.Equal("data", File.ReadAllText(file));
            }
            proceed.TrySetResult(true);
            Assert.True(await repair.WaitAsync(TimeSpan.FromSeconds(10)));
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(package, vm.Packages);
            Assert.False(File.Exists(file));
        }
        finally
        {
            proceed.TrySetResult(true);
            await repair.WaitAsync(TimeSpan.FromSeconds(10));
            if (removal != null)
                await removal.WaitAsync(TimeSpan.FromSeconds(10));
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_DoesNotWaitForAnUnrelatedRepair()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "TargetFolder");
        var otherDirectory = Path.Combine(root, "OtherFolder");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(otherDirectory);
        var file = Path.Combine(target, "data.bin");
        File.WriteAllText(file, "data");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var selected = new DownloadPackage { Name = "TargetFolder", SaveDirectory = target, AutoPar2Repair = false };
        selected.Items.Add(new DownloadItem { FileName = "data.bin", SaveFilePath = file });
        var other = new DownloadPackage { Name = "OtherFolder", SaveDirectory = otherDirectory, AutoPar2Repair = false };
        other.Items.Add(new DownloadItem { FileName = "other.bin", SaveFilePath = Path.Combine(otherDirectory, "other.bin") });
        vm.Packages.Add(selected);
        vm.Packages.Add(other);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repair = Par2RepairService.Instance.RunTrackedPackageOperationAsync(other, async () =>
        {
            started.TrySetResult(true);
            await proceed.Task;
            return true;
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await vm.RemovePackage(selected, true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(repair.IsCompleted);
            Assert.Contains(other, vm.Packages);
            Assert.False(File.Exists(file));
        }
        finally
        {
            proceed.TrySetResult(true);
            await repair.WaitAsync(TimeSpan.FromSeconds(10));
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_WaitsForARepairAfterItsPackagePathChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "TargetFolder");
        var otherDirectory = Path.Combine(root, "OtherFolder");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(otherDirectory);
        var file = Path.Combine(target, "data.bin");
        File.WriteAllText(file, "data");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var selected = new DownloadPackage { Name = "TargetFolder", SaveDirectory = target, AutoPar2Repair = false };
        selected.Items.Add(new DownloadItem { FileName = "data.bin", SaveFilePath = file });
        var other = new DownloadPackage { Name = "OtherFolder", SaveDirectory = target, AutoPar2Repair = false };
        other.Items.Add(new DownloadItem { FileName = "other.bin", SaveFilePath = Path.Combine(otherDirectory, "other.bin") });
        vm.Packages.Add(selected);
        vm.Packages.Add(other);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.DiskCleanupRunner = action => Task.Run(() => { action(); scanned.TrySetResult(true); });
        var repair = Par2RepairService.Instance.RunTrackedPackageOperationAsync(other, async () =>
        {
            started.TrySetResult(true);
            await proceed.Task;
            return true;
        });
        Task? removal = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            other.SaveDirectory = otherDirectory;
            removal = vm.RemovePackage(selected, true);
            await scanned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (await PackagePathOperationGate.Instance.EnterAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
            {
                Assert.False(removal.IsCompleted);
                Assert.Equal("data", File.ReadAllText(file));
            }
            proceed.TrySetResult(true);
            await repair.WaitAsync(TimeSpan.FromSeconds(10));
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(selected, vm.Packages);
            Assert.Contains(other, vm.Packages);
            Assert.False(File.Exists(file));
        }
        finally
        {
            proceed.TrySetResult(true);
            await repair.WaitAsync(TimeSpan.FromSeconds(10));
            if (removal != null)
                await removal.WaitAsync(TimeSpan.FromSeconds(10));
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Removal_StopsWhenAnotherExtractionCanUseTheDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupCase_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "TargetFolder");
        Directory.CreateDirectory(target);
        var file = Path.Combine(target, "data.bin");
        var archive = Path.Combine(root, "other.zip");
        File.WriteAllText(file, "data");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("other.txt").Open()))
            writer.Write("other");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var selected = new DownloadPackage { Name = "TargetFolder", SaveDirectory = target, AutoPar2Repair = false };
        selected.Items.Add(new DownloadItem { FileName = "data.bin", SaveFilePath = file });
        var other = new DownloadPackage { Name = "OtherFolder", SaveDirectory = root, IsCustomName = true, AutoExtractArchives = true, AutoPar2Repair = false };
        other.Items.Add(new DownloadItem { FileName = "other.zip", SaveFilePath = archive, Status = DownloadStatus.Completed });
        vm.Packages.Add(selected);
        vm.Packages.Add(other);
        var service = ArchiveExtractionService.Instance;
        var extraction = service.CheckAndExtractPackageAsync(other, force: true);
        try
        {
            Assert.True(service.IsPackageExtracting(other.Id));
            service.PausePackageExtraction(other.Id);
            await vm.RemovePackage(selected, true);
            Assert.Contains(selected, vm.Packages);
            Assert.Equal(Loc.Get("Status_CleanupBusy"), vm.StatusSummary);
            Assert.Equal("data", File.ReadAllText(file));
        }
        finally
        {
            service.CancelPackageExtraction(other.Id);
            service.ResumePackageExtraction(other.Id);
            await extraction.WaitAsync(TimeSpan.FromSeconds(10));
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
