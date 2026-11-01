using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public sealed class DownloadTargetCollisionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("TargetCollision_");
    private readonly string _previousLanguage = LocalizationService.Instance.CurrentLanguage;

    [Theory]
    [InlineData(1, false, "de")]
    [InlineData(1, true, "en")]
    [InlineData(4, false, "en")]
    [InlineData(4, true, "de")]
    public async Task ExistingTarget_IsNeverOverwrittenAndCompletePartCanBeRecovered(int connections, bool appearsDuringRequest, string language)
    {
        DownloadPersistenceService.IsTestEnvironment = true;
        LocalizationService.Instance.CurrentLanguage = language;
        string path = Path.Combine(_root.FullName, "payload.bin");
        byte[] original = { 9, 8, 7, 6 };
        byte[] data = Enumerable.Range(0, connections == 1 ? 65536 : 8 * 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
        if (!appearsDuringRequest) File.WriteAllBytes(path, original);
        int requests = 0;
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1 && appearsDuringRequest) File.WriteAllBytes(path, original);
            var range = request.Headers.Range?.Ranges.Single();
            int start = (int)(range?.From ?? 0), end = (int)(range?.To ?? data.Length - 1);
            var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data, start, end - start + 1)
            };
            if (range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.Length);
            return Task.FromResult(response);
        }));
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = connections };
        var item = new DownloadItem
        {
            FileName = "payload.bin", SaveFilePath = path, DirectDownloadUrl = "https://example.invalid/payload.bin",
            IsCustomName = true, Status = DownloadStatus.Paused
        };
        int completed = 0, failed = 0;
        engine.DownloadCompleted += _ => completed++;
        engine.DownloadFailed += (_, _) => failed++;
        await engine.ResumeDownloadAsync(item);
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal(Loc.Get("Error_DownloadDestinationExists"), item.ErrorMessage);
        Assert.Equal(0, completed);
        Assert.Equal(1, failed);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(data, File.ReadAllBytes(path + ".part"));
        Assert.False(engine.IsDownloading(item.Id));
        int requestsBeforeRetry = requests;
        File.Move(path, Path.Combine(_root.FullName, "preserved.bin"));
        await engine.ResumeDownloadAsync(item);
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(1, completed);
        Assert.Equal(requestsBeforeRetry, requests);
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root.FullName, "preserved.bin")));
        Assert.False(File.Exists(path + ".part"));
        Assert.False(File.Exists(path + ".part.segments"));
    }

    [Fact]
    public async Task LaterDownload_CannotReplaceAnAlreadyCompletedFile()
    {
        string path = Path.Combine(_root.FullName, "archive.part1.rar");
        using var client = new HttpClient(new TestMockHttpMessageHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri!.AbsolutePath == "/first" ? new byte[] { 1, 2, 3, 4 } : new byte[] { 5, 6, 7, 8 })
            })));
        var engine = new DownloadEngine(client) { MaxConnectionsPerDownload = 1 };
        var first = new DownloadItem { FileName = Path.GetFileName(path), SaveFilePath = path, DirectDownloadUrl = "https://example.invalid/first", IsCustomName = true };
        var second = new DownloadItem { FileName = Path.GetFileName(path), SaveFilePath = path, DirectDownloadUrl = "https://example.invalid/second", IsCustomName = true };
        await engine.ResumeDownloadAsync(first);
        await engine.ResumeDownloadAsync(second);
        Assert.Equal(DownloadStatus.Completed, first.Status);
        Assert.Equal(DownloadStatus.Failed, second.Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path));
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(path + ".part"));
    }

    public void Dispose()
    {
        LocalizationService.Instance.CurrentLanguage = _previousLanguage;
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(_root.Parent?.FullName, temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected temporary directory.");
        _root.Delete(recursive: true);
    }
}
