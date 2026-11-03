using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Reepax.Models;
using Reepax.Services.Download;
using Xunit;

namespace Reepax.Tests;

public sealed class ItemRenameDownloadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ItemRenameDownload_").FullName;

    [Fact]
    public async Task RenamedPartialDownload_ResumesFromItsCheckpointAndProducesExactBytes()
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
        int completed = 0;
        engine.DownloadCompleted += _ => completed++;
        await engine.StartDownloadAsync(item, "https://example.invalid/data", null, null, null, null);
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.True(File.Exists(item.SaveFilePath + ".part.segments"));
        item.Status = DownloadStatus.Paused;
        string original = item.SaveFilePath!;
        Assert.True(item.Rename("renamed.bin"));
        Assert.False(File.Exists(original + ".part"));
        Assert.False(File.Exists(original + ".part.segments"));
        await engine.ResumeDownloadAsync(item);
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(2, requests);
        Assert.Equal(1, completed);
        Assert.Equal(data, File.ReadAllBytes(item.SaveFilePath!));
        Assert.False(File.Exists(item.SaveFilePath + ".part"));
        Assert.False(File.Exists(item.SaveFilePath + ".part.segments"));
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

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
