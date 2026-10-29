using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class PackagePathOperationGateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataRename_ResumesOnlyWhenTheItemIsKept(bool removed)
    {
        var queue = new QueueManager(subscribeToDownloadEvents: false);
        var item = new DownloadItem { FileName = "download_file", OriginalUrl = "https://example.invalid/data" };
        using var handler = new MetadataHandler();
        using var client = new HttpClient(handler);
        var resolver = new LinkMetadataResolverService(client);
        await queue.StopCompletionsForRemovalAsync(new[] { item });
        Task<bool>? resolving = null;
        try
        {
            resolving = resolver.ResolveItemMetadataAsync(item);
            await handler.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(resolving.IsCompleted);
            Assert.Equal("download_file", item.FileName);
            await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed);
            Assert.True(await resolving.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(removed ? "download_file" : "resolved.bin", item.FileName);
        }
        finally
        {
            if (item.IsRemovalPending)
                await queue.ReleaseItemsFromRemovalAsync(new[] { item }, removed: true);
            if (resolving != null)
                await resolving.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        internal TaskCompletionSource<bool> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "resolved.bin" };
            Requested.TrySetResult(true);
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UserRename_WaitsForDiskWorkAndSkipsRemovedEntries(bool renamePackage, bool remove)
    {
        var root = Path.Combine(Path.GetTempPath(), "CleanupRename_" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "Original");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "data.bin");
        File.WriteAllText(path, "data");
        var vm = new MainViewModel();
        vm.Packages.Clear();
        var package = new DownloadPackage { Name = "Original", SaveDirectory = directory, IsCustomName = true, AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "data.bin", SaveFilePath = path };
        package.Items.Add(item);
        vm.Packages.Add(package);
        using var lease = await PackagePathOperationGate.Instance.EnterAsync();
        try
        {
            if (renamePackage)
                vm.RenamePackageWhenAvailable(package, "Updated");
            else
                vm.RenameItemWhenAvailable(item, "updated.bin");
            Assert.True(File.Exists(path));
            Assert.Equal("Original", package.Name);
            Assert.Equal("data.bin", item.FileName);
            if (remove)
                vm.Packages.Remove(package);
            lease.Dispose();
            using (await PackagePathOperationGate.Instance.EnterAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
            {
                Assert.Equal(remove || !renamePackage ? "Original" : "Updated", package.Name);
                Assert.Equal(remove || renamePackage ? "data.bin" : "updated.bin", item.FileName);
                Assert.Equal("data", File.ReadAllText(item.SaveFilePath));
            }
        }
        finally
        {
            lease.Dispose();
            vm.Packages.Clear();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task WaitingOperation_CanBeCancelledWithoutReleasingTheCurrentLease()
    {
        var gate = new PackagePathOperationGate();
        using var cancellation = new CancellationTokenSource();
        using var current = await gate.EnterAsync();
        var waiting = gate.EnterAsync(cancellation.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var next = gate.EnterAsync().AsTask();
        Assert.False(next.IsCompleted);
        current.Dispose();
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AutomaticPackageRename_WaitsForTheActivePathOperation()
    {
        var package = new DownloadPackage { Name = "Download_Package", AutoPar2Repair = false };
        var item = new DownloadItem { FileName = "download_file" };
        package.Items.Add(item);
        var renamed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        package.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadPackage.Name))
                renamed.TrySetResult(true);
        };
        using var lease = await PackagePathOperationGate.Instance.EnterAsync();
        item.FileName = "Sample Product.zip";
        Assert.Equal("Download_Package", package.Name);
        lease.Dispose();
        await renamed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual("Download_Package", package.Name);
    }
}
