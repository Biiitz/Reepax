using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public sealed class ItemRenameDownloadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ItemRenameDownload_").FullName;

    [Theory]
    [InlineData("renamed.bin")]
    [InlineData("INITIAL.bin")]
    public async Task RenamedPartialDownload_ResumesFromItsCheckpointAndProducesExactBytes(string name)
    {
        byte[] data = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
        int requests = 0;
        using var handler = new ResponseHandler(request =>
        {
            int offset = ++requests == 1 ? 0 : 513;
            Assert.Equal(offset == 0 ? null : (long?)offset, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(offset == 0 ? data[..513] : data[513..])
            };
            response.Content.Headers.ContentLength = 1024 - offset;
            if (offset != 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, 1023, 1024);
            return response;
        });
        using var client = new HttpClient(handler);
        var engine = new DownloadEngine(client);
        var item = Item();
        string checksum = Convert.ToHexString(SHA256.HashData(data));
        item.ExpectedChecksum = checksum;
        int completed = 0;
        engine.DownloadCompleted += _ => completed++;
        await engine.StartDownloadAsync(item, "https://example.invalid/data", null, null, null, null);
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.True(File.Exists(item.SaveFilePath + ".part.segments"));
        item.Status = DownloadStatus.Paused;
        string original = item.SaveFilePath!;
        byte[] checkpoint = File.ReadAllBytes(original + ".part.segments");
        ItemRenameSafetyTests.RenameThroughViewModel(item, name);
        Assert.Equal(name, item.FileName);
        Assert.Equal(Path.Combine(_root, name), item.SaveFilePath);
        Assert.True(item.IsCustomName);
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.Equal(513, item.DownloadedBytes);
        Assert.Equal(checksum, item.ExpectedChecksum);
        Assert.Null(item.CalculatedChecksum);
        Assert.Equal(data[..513], File.ReadAllBytes(item.SaveFilePath + ".part"));
        Assert.Equal(checkpoint, File.ReadAllBytes(item.SaveFilePath + ".part.segments"));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), path => string.Equals(path, original + ".part", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), path => string.Equals(path, original + ".part.segments", StringComparison.Ordinal));
        await engine.ResumeDownloadAsync(item);
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(2, requests);
        Assert.Equal(1, completed);
        Assert.Equal(name, item.FileName);
        Assert.Equal(Path.Combine(_root, name), item.SaveFilePath);
        Assert.Equal(data.Length, item.DownloadedBytes);
        Assert.Equal(checksum, item.ExpectedChecksum);
        Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Equal(checksum, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.SaveFilePath!))));
        Assert.False(File.Exists(item.SaveFilePath + ".part"));
        Assert.False(File.Exists(item.SaveFilePath + ".part.segments"));
    }

    [Fact]
    public async Task ActiveDownload_UserRenameIsRejectedWithoutInterruptingItsExactResult()
    {
        byte[] data = Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray();
        using var stream = new PausingStream(data);
        using var handler = new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = data.Length;
            return response;
        });
        using var client = new HttpClient(handler);
        var engine = new DownloadEngine(client);
        var item = Item();
        string checksum = Convert.ToHexString(SHA256.HashData(data));
        item.ExpectedChecksum = checksum;
        string original = item.SaveFilePath!;
        int completed = 0;
        engine.DownloadCompleted += _ => completed++;
        Task download = engine.StartDownloadAsync(item, "https://example.invalid/data", null, null, null, null);
        try
        {
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(engine.IsDownloading(item.Id));
            Assert.True(File.Exists(original + ".part"));
            ItemRenameSafetyTests.RenameThroughViewModel(item, "renamed.bin", Loc.Get("Status_ItemRenameBusy"));
            Assert.Equal(original, item.SaveFilePath);
            Assert.Equal("initial.bin", item.FileName);
            Assert.False(item.IsCustomName);
            Assert.Equal(DownloadStatus.Downloading, item.Status);
            Assert.Equal(checksum, item.ExpectedChecksum);
            Assert.Null(item.CalculatedChecksum);
            Assert.DoesNotContain(Directory.EnumerateFiles(_root), path => Path.GetFileName(path).StartsWith("renamed"));
            stream.Release.TrySetResult();
            await download.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(original, item.SaveFilePath);
            Assert.Equal("initial.bin", item.FileName);
            Assert.Equal(checksum, item.ExpectedChecksum);
            Assert.Equal(1, completed);
            Assert.Equal(data, File.ReadAllBytes(original));
            Assert.False(File.Exists(original + ".part"));
            Assert.False(File.Exists(original + ".part.segments"));
        }
        finally
        {
            _ = engine.CancelOrPauseDownload(item.Id);
            stream.Release.TrySetResult();
            await download.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".part")]
    [InlineData(".part.segments")]
    public async Task AutomaticNameCollision_PreservesOtherFilesAndDownloadsUnderOriginalName(string suffix)
    {
        byte[] data = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        string occupied = Path.Combine(_root, "server.bin") + suffix;
        File.WriteAllText(occupied, "unrelated data");
        var item = Item();
        using var handler = new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "server.bin" };
            return response;
        });
        using var client = new HttpClient(handler);
        var engine = new DownloadEngine(client);
        await engine.StartDownloadAsync(item, "https://example.invalid/data", null, null, null, null);
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("initial.bin", item.FileName);
        Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
        Assert.Equal("unrelated data", File.ReadAllText(occupied));
    }

    private DownloadItem Item() => new()
    {
        FileName = "initial.bin", SaveFilePath = Path.Combine(_root, "initial.bin"), Status = DownloadStatus.Paused
    };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class PausingStream(byte[] data) : MemoryStream(data, writable: false)
    {
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sentPrefix;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentPrefix)
            {
                _sentPrefix = true;
                return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 513)], cancellationToken);
            }
            Blocked.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
