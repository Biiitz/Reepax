using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Reepax.Tests;

public sealed partial class ProcessRecoveryTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task BrowserDownload_ResumesAfterProcessRestartWithoutOpeningAnotherBrowser(bool entityTag, bool hasChecksum)
    {
        PrepareDestination();
        await using var source = new BrowserHandoffSource(512 * 1024) { HoldBody = true, IncludeEntityTag = entityTag };
        byte[] original = source.Bytes.ToArray();
        if (hasChecksum) File.WriteAllBytes(Path.Combine(_directory.FullName, "download.bin"), original);
        int browserProcess;
        long savedBytes;
        await using (var child = new RecoveryChildProcess(_directory.FullName, "browser-save", "none", source.Url("/resume"), 1))
        {
            var pausing = await child.ReceiveAsync();
            Assert.Equal("pausing", pausing.Boundary);
            source.ReleaseBody.TrySetResult();
            var saved = await child.ReceiveAsync();
            Assert.Equal("saved", saved.Boundary);
            Assert.InRange(saved.Downloaded, 1, original.Length - 1);
            Assert.False(saved.Completed);
            Assert.False(saved.Notified);
            savedBytes = saved.Downloaded;
            browserProcess = saved.ProcessId;
            await child.AssertExitedSuccessfullyAsync();
        }
        string final = Path.Combine(Downloads, "download.bin");
        Assert.False(File.Exists(final));
        Assert.Equal(original[..(int)savedBytes], File.ReadAllBytes(final + ".part"));
        using (var checkpoint = JsonDocument.Parse(File.ReadAllText(final + ".part.segments")))
            Assert.Equal(savedBytes, checkpoint.RootElement.GetProperty("Segments")[0].GetProperty("Done").GetInt64());
        using (var storage = new Reepax.Services.Storage.DownloadPersistenceService(Path.Combine(_directory.FullName, "downloads.json")))
        {
            var restored = Assert.Single(Assert.Single(storage.LoadDownloads()).Items);
            Assert.Equal(source.Url("/resume"), restored.DirectDownloadUrl);
            Assert.Equal(hasChecksum, !string.IsNullOrWhiteSpace(restored.ExpectedChecksum));
            Assert.Contains("example_session=ready", restored.Cookies);
            Assert.EndsWith("/start", restored.Referer);
        }
        if (!entityTag && !hasChecksum)
            for (int index = 0; index < source.Bytes.Length; index++) source.Bytes[index] ^= 0x55;
        byte[] current = source.Bytes.ToArray();
        await using (var child = new RecoveryChildProcess(_directory.FullName, "browser-recover", "none", source.Url("/resume"), 1))
        {
            var recovered = await child.ReceiveAsync();
            await child.AssertExitedSuccessfullyAsync();
            Assert.NotEqual(browserProcess, recovered.ProcessId);
            Assert.Equal("recovered", recovered.Boundary);
            Assert.True(recovered.Completed);
            Assert.True(recovered.Notified);
            Assert.Equal(savedBytes, recovered.ResumedFrom);
            Assert.Equal(original.Length, recovered.Downloaded);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(current)), recovered.Checksum, ignoreCase: true);
        }
        var requests = source.Requests.Where(request => request.Path == "/resume").ToArray();
        Assert.Equal(2, requests.Length);
        Assert.True(requests[0].Browser);
        Assert.Equal("", requests[0].Range);
        Assert.False(requests[1].Browser);
        Assert.Equal(entityTag || hasChecksum ? $"bytes={savedBytes}-" : "", requests[1].Range);
        Assert.Equal(entityTag ? "\"sample-v1\"" : "", requests[1].IfRange);
        Assert.Equal(requests[0].UserAgent, requests[1].UserAgent);
        Assert.Contains("example_session=ready", requests[1].Cookie);
        Assert.Equal(requests[0].Referer, requests[1].Referer);
        Assert.Single(source.Requests.Where(request => request.Path == "/start"));
        Assert.Equal(current, File.ReadAllBytes(final));
        Assert.False(File.Exists(final + ".part"));
        Assert.False(File.Exists(final + ".part.segments"));
        AssertUnaffected();
    }
}
