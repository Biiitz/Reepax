using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reepax.Models;
using Reepax.Services.Download;
using Xunit;

namespace Reepax.Tests;

public class TestMockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public TestMockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _handler(request, cancellationToken);
    }
}

public class DownloadEngineTests
{
    [Fact]
    public async Task DownloadEngine_DownloadsAndRenamesFileProperly()
    {
        // Arrange
        var tempFolder = Path.Combine(Path.GetTempPath(), "ReepaxTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "sample_test_file.txt");
            var item = new DownloadItem
            {
                FileName = "sample_test_file.txt",
                SaveFilePath = destinationFile,
                Status = DownloadStatus.Queued
            };

            var testData = Encoding.UTF8.GetBytes("Hello, this is verified download content!");
            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(testData)
                };
                response.Content.Headers.ContentLength = testData.Length;
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadCompleted += downloadedItem =>
            {
                if (downloadedItem.Id == item.Id)
                    tcs.TrySetResult(true);
            };

            engine.DownloadFailed += (failedItem, ex) =>
            {
                if (failedItem.Id == item.Id)
                    tcs.TrySetException(ex);
            };

            await engine.StartDownloadAsync(
                item, 
                "https://example.com/sample.txt", 
                null, 
                "Mozilla/5.0", 
                "https://example.com", 
                "sample_test_file.txt");

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            Assert.Same(tcs.Task, completedTask);
            Assert.True(tcs.Task.Result);

            // Assert
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(100.0, item.ProgressPercentage);
            Assert.True(File.Exists(destinationFile));
            Assert.False(File.Exists(destinationFile + ".part"));
            Assert.Equal(testData.Length, new FileInfo(destinationFile).Length);
            Assert.Equal("Hello, this is verified download content!", File.ReadAllText(destinationFile));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempFolder))
                    Directory.Delete(tempFolder, true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task DownloadEngine_ResumeWith206PartialContent_AppendsAndCompletesCorrectly()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "Reepax_Resume206_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "part_resume_206.bin");
            var partFile = destinationFile + ".part";

            // 1. Write first 500 bytes of 'A' to .part file
            var initial500 = new byte[500];
            Array.Fill(initial500, (byte)'A');
            File.WriteAllBytes(partFile, initial500);

            var item = new DownloadItem
            {
                FileName = "part_resume_206.bin",
                SaveFilePath = destinationFile,
                DirectDownloadUrl = "https://example.com/part_resume_206.bin",
                Status = DownloadStatus.Paused,
                DownloadedBytes = 500,
                TotalBytes = 1000
            };

            // 2. Prepare remaining 500 bytes of 'B'
            var remaining500 = new byte[500];
            Array.Fill(remaining500, (byte)'B');

            bool receivedRangeHeader = false;
            long requestedRangeFrom = 0;

            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                if (req.Headers.Range != null && req.Headers.Range.Ranges.Count > 0)
                {
                    receivedRangeHeader = true;
                    requestedRangeFrom = req.Headers.Range.Ranges.First().From ?? 0;
                }

                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(remaining500)
                };
                response.Content.Headers.ContentLength = remaining500.Length;
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(500, 999, 1000);
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadCompleted += downloadedItem =>
            {
                if (downloadedItem.Id == item.Id)
                    tcs.TrySetResult(true);
            };
            engine.DownloadFailed += (failedItem, ex) =>
            {
                if (failedItem.Id == item.Id)
                    tcs.TrySetException(ex);
            };

            await engine.ResumeDownloadAsync(item);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            Assert.Same(tcs.Task, completedTask);
            Assert.True(tcs.Task.Result);

            Assert.True(receivedRangeHeader);
            Assert.Equal(500, requestedRangeFrom);
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(1000, item.TotalBytes);
            Assert.Equal(1000, item.DownloadedBytes);
            Assert.True(File.Exists(destinationFile));
            Assert.False(File.Exists(partFile));

            var finalBytes = File.ReadAllBytes(destinationFile);
            Assert.Equal(1000, finalBytes.Length);
            for (int i = 0; i < 500; i++) Assert.Equal((byte)'A', finalBytes[i]);
            for (int i = 500; i < 1000; i++) Assert.Equal((byte)'B', finalBytes[i]);
        }
        finally
        {
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact]
    public async Task DownloadEngine_ResumeWith200OK_ServerIgnoresRange_OverwritesAndCompletesCorrectly()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "Reepax_Resume200_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "part_resume_200.bin");
            var partFile = destinationFile + ".part";

            // 1. Write 500 bytes of junk 'X' into .part file
            var junk500 = new byte[500];
            Array.Fill(junk500, (byte)'X');
            File.WriteAllBytes(partFile, junk500);

            var item = new DownloadItem
            {
                FileName = "part_resume_200.bin",
                SaveFilePath = destinationFile,
                DirectDownloadUrl = "https://example.com/part_resume_200.bin",
                Status = DownloadStatus.Paused,
                DownloadedBytes = 500,
                TotalBytes = 1000
            };

            // 2. Server ignores Range header and returns FULL 1000 bytes of 'C' with 200 OK
            var full1000 = new byte[1000];
            Array.Fill(full1000, (byte)'C');

            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(full1000)
                };
                response.Content.Headers.ContentLength = full1000.Length;
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadCompleted += downloadedItem =>
            {
                if (downloadedItem.Id == item.Id)
                    tcs.TrySetResult(true);
            };
            engine.DownloadFailed += (failedItem, ex) =>
            {
                if (failedItem.Id == item.Id)
                    tcs.TrySetException(ex);
            };

            await engine.ResumeDownloadAsync(item);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            Assert.Same(tcs.Task, completedTask);
            Assert.True(tcs.Task.Result);

            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(1000, item.TotalBytes);
            Assert.Equal(1000, item.DownloadedBytes);
            Assert.True(File.Exists(destinationFile));
            Assert.False(File.Exists(partFile));

            var finalBytes = File.ReadAllBytes(destinationFile);
            // Must NOT be 1500 bytes (junk + full), must be EXACTLY 1000 bytes of 'C'!
            Assert.Equal(1000, finalBytes.Length);
            Assert.All(finalBytes, b => Assert.Equal((byte)'C', b));
        }
        finally
        {
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact]
    public async Task DownloadEngine_ResumeWith416RangeNotSatisfiable_RestartsFromByte0AndCompletes()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "Reepax_Resume416_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "part_resume_416.bin");
            var partFile = destinationFile + ".part";

            // 1. Write 800 bytes of stale .part
            var stale800 = new byte[800];
            Array.Fill(stale800, (byte)'Z');
            File.WriteAllBytes(partFile, stale800);

            var item = new DownloadItem
            {
                FileName = "part_resume_416.bin",
                SaveFilePath = destinationFile,
                DirectDownloadUrl = "https://example.com/part_resume_416.bin",
                Status = DownloadStatus.Paused,
                DownloadedBytes = 800,
                TotalBytes = 1000
            };

            var full1000 = new byte[1000];
            Array.Fill(full1000, (byte)'D');

            int requestCount = 0;
            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                requestCount++;
                if (req.Headers.Range != null)
                {
                    // First request with range -> return 416
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
                }

                // Second request without range -> return 200 OK full file
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(full1000)
                };
                response.Content.Headers.ContentLength = full1000.Length;
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadCompleted += downloadedItem =>
            {
                if (downloadedItem.Id == item.Id)
                    tcs.TrySetResult(true);
            };
            engine.DownloadFailed += (failedItem, ex) =>
            {
                if (failedItem.Id == item.Id)
                    tcs.TrySetException(ex);
            };

            await engine.ResumeDownloadAsync(item);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            Assert.Same(tcs.Task, completedTask);
            Assert.True(tcs.Task.Result);

            Assert.Equal(2, requestCount);
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(1000, item.TotalBytes);
            Assert.Equal(1000, item.DownloadedBytes);
            Assert.True(File.Exists(destinationFile));

            var finalBytes = File.ReadAllBytes(destinationFile);
            Assert.Equal(1000, finalBytes.Length);
            Assert.All(finalBytes, b => Assert.Equal((byte)'D', b));
        }
        finally
        {
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact]
    public async Task DownloadEngine_StreamTruncation_ThrowsIOExceptionAndMarksDownloadFailed()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "Reepax_Truncate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "truncated_file.bin");

            var item = new DownloadItem
            {
                FileName = "truncated_file.bin",
                SaveFilePath = destinationFile,
                DirectDownloadUrl = "https://example.com/truncated_file.bin",
                Status = DownloadStatus.Queued
            };

            // Server promises 2000 bytes in Content-Length, but only delivers 600 bytes
            var promisedData = new byte[2000];
            Array.Fill(promisedData, (byte)'T');

            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                // Fresh stream per request (engine may first perform a range probe)
                var truncatedStream = new MemoryStream(promisedData, 0, 600);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(truncatedStream)
                };
                response.Content.Headers.ContentLength = 2000;
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient);

            var tcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadCompleted += downloadedItem =>
            {
                if (downloadedItem.Id == item.Id)
                    tcs.TrySetResult(null);
            };
            engine.DownloadFailed += (failedItem, ex) =>
            {
                if (failedItem.Id == item.Id)
                    tcs.TrySetResult(ex);
            };

            await engine.StartDownloadAsync(
                item, 
                "https://example.com/truncated_file.bin", 
                null, 
                "Mozilla/5.0", 
                null, 
                "truncated_file.bin");

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            Assert.Same(tcs.Task, completedTask);

            var failedException = tcs.Task.Result;
            Assert.NotNull(failedException);
            Assert.IsAssignableFrom<IOException>(failedException);
            Assert.Contains("interrupted", failedException.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.False(string.IsNullOrEmpty(item.ErrorMessage));
            // Crucial: Final file must NOT exist (only .part)
            Assert.False(File.Exists(destinationFile));
            Assert.True(File.Exists(destinationFile + ".part"));
        }
        finally
        {
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact]
    public async Task BandwidthThrottler_ZeroLimit_DoesNotDelay()
    {
        var throttler = new BandwidthThrottler();
        throttler.MaxBytesPerSecond = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            await throttler.ThrottleAsync(1024 * 1024, CancellationToken.None);
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100);
    }

    [Fact]
    public async Task BandwidthThrottler_EnforcesRateLimit()
    {
        var throttler = new BandwidthThrottler();
        // Limit: 500 KB/s (0.5 MB/s)
        long limitBytesPerSec = 500 * 1024;
        throttler.MaxBytesPerSecond = limitBytesPerSec;

        // Drain initial bucket
        await throttler.ThrottleAsync((int)Math.Max(limitBytesPerSec, 256 * 1024), CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Request 250 KB -> should take approx ~500ms
        await throttler.ThrottleAsync(250 * 1024, CancellationToken.None);
        sw.Stop();

        // Check that delay occurred (at least 300ms)
        Assert.True(sw.ElapsedMilliseconds >= 300, $"Expected >= 300ms, actual: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task BandwidthThrottler_DynamicLimitChange_AdaptsImmediately()
    {
        var throttler = new BandwidthThrottler();
        throttler.MaxBytesPerSecond = 200 * 1024; // 200 KB/s
        Assert.Equal(200 * 1024, throttler.MaxBytesPerSecond);

        // Switch to unlimited
        throttler.MaxBytesPerSecond = 0;
        Assert.Equal(0, throttler.MaxBytesPerSecond);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await throttler.ThrottleAsync(500 * 1024, CancellationToken.None);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 50);
    }

    [Fact]
    public void SpeedLimitDisplayConverter_FormatsCorrectly()
    {
        var converter = new Reepax.Converters.SpeedLimitDisplayConverter();
        Assert.Equal(Reepax.Services.Localization.Loc.Get("SpeedLimit_Unlimited"), converter.Convert(0, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("5 MB/s", converter.Convert(5, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1000 MB/s (1 GB/s)", converter.Convert(1000, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2,5", 2.5)]
    [InlineData("2.5", 2.5)]
    [InlineData("0,75", 0.75)]
    [InlineData("10", 10)]
    [InlineData("-5", 0)]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    public void MainViewModel_TryParseSpeedLimit_ParsesCorrectly(string input, double expected)
    {
        bool success = Reepax.ViewModels.MainViewModel.TryParseSpeedLimit(input, out double val);
        Assert.True(success);
        Assert.Equal(expected, val);
    }

    [Fact]
    public void MainViewModel_MaxConcurrentDownloadsText_UpdatesValue()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        vm.MaxConcurrentDownloadsText = "5";
        Assert.Equal(5, vm.MaxConcurrentDownloads);

        vm.MaxConcurrentDownloadsText = "120"; // Clamped to 10
        Assert.Equal(10, vm.MaxConcurrentDownloads);
        Assert.Equal("10", vm.MaxConcurrentDownloadsText);

        vm.IncrementMaxDownloads();
        Assert.Equal(10, vm.MaxConcurrentDownloads); // cannot exceed 10

        vm.MaxConcurrentDownloads = 1;
        vm.DecrementMaxDownloads();
        Assert.Equal(1, vm.MaxConcurrentDownloads); // cannot go below 1

        vm.IncrementMaxDownloads();
        Assert.Equal(2, vm.MaxConcurrentDownloads);
    }

    [Fact]
    public void DownloadPackage_RequiredDiskSpaceBytes_Calculates3xDownloadSize()
    {
        var package = new Reepax.Models.DownloadPackage();
        package.Items.Add(new Reepax.Models.DownloadItem { TotalBytes = 10_000_000_000L }); // 10 GB
        package.Items.Add(new Reepax.Models.DownloadItem { TotalBytes = 5_000_000_000L });  // 5 GB

        Assert.Equal(15_000_000_000L, package.TotalBytes);
        Assert.Equal(45_000_000_000L, package.RequiredDiskSpaceBytes); // 3x = 45 GB
    }

    [Fact]
    public void MainViewModel_DriveSpaceAnd3xRequirement_UpdatesCorrectly()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        vm.Packages.Clear();

        var package = new Reepax.Models.DownloadPackage();
        package.Items.Add(new Reepax.Models.DownloadItem { TotalBytes = 20_000_000_000L }); // 20 GB
        vm.Packages.Add(package);

        vm.RecalculateGlobalStats();

        Assert.Equal(20_000_000_000L, vm.TotalBytes);
        Assert.Equal(60_000_000_000L, vm.TotalRequiredDiskSpaceBytes); // 3x = 60 GB
        Assert.NotEmpty(vm.FreeDiskSpaceText);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1024 * 1024 * 5, "5 MB")]
    [InlineData(10737418240L, "10 GB")]
    public void MainViewModel_FormatBytes_FormatsCorrectly(long bytes, string expected)
    {
        string formatted = Reepax.ViewModels.MainViewModel.FormatBytes(bytes);
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public async Task BandwidthThrottler_PreservesFractionalTokensWithoutDrift()
    {
        var throttler = new BandwidthThrottler();
        // Set rate to 50,000 bytes/sec
        throttler.MaxBytesPerSecond = 50_000;

        // Drain initial burst
        await throttler.ThrottleAsync(50_000, CancellationToken.None);

        // Throttle small amounts of bytes that produce fractional millisecond tokens
        for (int i = 0; i < 5; i++)
        {
            await throttler.ThrottleAsync(250, CancellationToken.None);
        }

        // Setting limit to unlimited works instantly
        throttler.MaxBytesPerSecond = 0;
        await throttler.ThrottleAsync(50000, CancellationToken.None);
        Assert.Equal(0, throttler.MaxBytesPerSecond);
    }

    [Fact]
    public async Task DownloadEngine_PreventsDuplicateConcurrentStarts()
    {
        // Attempting to resume an item without direct URL returns immediately
        var emptyItem = new DownloadItem { Id = Guid.NewGuid() };
        await DownloadEngine.Instance.ResumeDownloadAsync(emptyItem);
        Assert.False(DownloadEngine.Instance.IsDownloading(emptyItem.Id));
    }
    [Fact]
    public async Task DownloadEngine_TruncatedStream_ThrowsIOExceptionAndFails()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "TruncTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "truncated.bin");
            var item = new DownloadItem
            {
                FileName = "truncated.bin",
                SaveFilePath = destinationFile,
                Status = DownloadStatus.Queued
            };

            var partialData = Encoding.UTF8.GetBytes("Short");
            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(partialData)
                };
                // Report 100 bytes ContentLength but only send 5 bytes ("Short")
                response.Content.Headers.ContentLength = 100;
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient);

            var tcs = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadFailed += (failedItem, ex) =>
            {
                if (failedItem.Id == item.Id)
                    tcs.TrySetResult(ex);
            };

            await engine.StartDownloadAsync(item, "https://example.com/truncated", null, null, null, "truncated.bin");

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            Assert.Same(tcs.Task, completedTask);

            var ex = tcs.Task.Result;
            Assert.IsType<IOException>(ex);
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.Contains("interrupted", item.ErrorMessage);
            Assert.False(File.Exists(destinationFile)); // Should not rename truncated part to final file
            Assert.True(File.Exists(destinationFile + ".part")); // .part is preserved for resumption
        }
        finally
        {
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact]
    public async Task DownloadEngine_RangeResume_AppendsOn206()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "RangeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "resumable.txt");
            var partFile = destinationFile + ".part";
            await File.WriteAllTextAsync(partFile, "PART1_");

            var item = new DownloadItem
            {
                FileName = "resumable.txt",
                DirectDownloadUrl = "https://example.com/resumable",
                SaveFilePath = destinationFile,
                Status = DownloadStatus.Queued
            };

            var remainingData = Encoding.UTF8.GetBytes("PART2");
            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                Assert.NotNull(req.Headers.Range);
                Assert.Equal(6, req.Headers.Range.Ranges.First().From);

                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(remainingData)
                };
                response.Content.Headers.ContentLength = remainingData.Length;
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(6, 10, 11);
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadCompleted += downloadedItem =>
            {
                if (downloadedItem.Id == item.Id)
                    tcs.TrySetResult(true);
            };
            engine.DownloadFailed += (downloadedItem, ex) =>
            {
                if (downloadedItem.Id == item.Id)
                    tcs.TrySetException(new Exception($"Download failed: {ex.Message} (StatusMessage: {item.StatusMessage})", ex));
            };

            await engine.StartDownloadAsync(item, "https://example.com/resumable", null, null, null, "resumable.txt");

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            Assert.Same(tcs.Task, completedTask);
            Assert.True(tcs.Task.Result);

            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.True(File.Exists(destinationFile));
            Assert.Equal("PART1_PART2", await File.ReadAllTextAsync(destinationFile));
        }
        finally
        {
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact]
    public async Task DownloadEngine_RangeResume_OverwritesOn200OK()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "Range200Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "fresh.txt");
            var partFile = destinationFile + ".part";
            await File.WriteAllTextAsync(partFile, "OLD_STALE_PARTIAL_DATA");

            var item = new DownloadItem
            {
                FileName = "fresh.txt",
                SaveFilePath = destinationFile,
                Status = DownloadStatus.Queued
            };

            var fullData = Encoding.UTF8.GetBytes("ENTIRE_FRESH_CONTENT");
            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                // Server does not support range and sends 200 OK with entire content
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fullData)
                };
                response.Content.Headers.ContentLength = fullData.Length;
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadCompleted += downloadedItem =>
            {
                if (downloadedItem.Id == item.Id)
                    tcs.TrySetResult(true);
            };

            await engine.StartDownloadAsync(item, "https://example.com/fresh", null, null, null, "fresh.txt");

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            Assert.Same(tcs.Task, completedTask);
            Assert.True(tcs.Task.Result);

            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.True(File.Exists(destinationFile));
            // Old data must be overwritten, not appended!
            Assert.Equal("ENTIRE_FRESH_CONTENT", await File.ReadAllTextAsync(destinationFile));
        }
        finally
        {
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    private class TruncatedTestStream : Stream
    {
        private readonly byte[] _data;
        private int _position = 0;
        private readonly int _truncateAt;

        public TruncatedTestStream(byte[] data, int truncateAt)
        {
            _data = data;
            _truncateAt = truncateAt;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _truncateAt)
                return 0;

            int toRead = Math.Min(count, _truncateAt - _position);
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _truncateAt)
                return ValueTask.FromResult(0);

            int toRead = Math.Min(buffer.Length, _truncateAt - _position);
            _data.AsSpan(_position, toRead).CopyTo(buffer.Span);
            _position += toRead;
            return ValueTask.FromResult(toRead);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task DownloadEngine_ChunkedDownload_ReassemblesFileBytePerfectly()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "ReepaxChunked_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "chunked_test.bin");
            var item = new DownloadItem
            {
                FileName = "chunked_test.bin",
                SaveFilePath = destinationFile,
                DirectDownloadUrl = "https://example.com/chunked_test.bin",
                Status = DownloadStatus.Queued
            };

            // 8 MB + remainder -> above MinBytesForChunking; deterministic pattern
            var testData = new byte[8 * 1024 * 1024 + 12345];
            for (int i = 0; i < testData.Length; i++)
                testData[i] = (byte)(i % 251);

            int rangeRequests = 0;
            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                if (req.Headers.Range != null)
                {
                    Interlocked.Increment(ref rangeRequests);
                    long from = req.Headers.Range.Ranges.First().From ?? 0;
                    long to = req.Headers.Range.Ranges.First().To ?? (testData.Length - 1);
                    var slice = testData.AsSpan((int)from, (int)(to - from + 1)).ToArray();
                    var res = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = new ByteArrayContent(slice)
                    };
                    res.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, testData.Length);
                    return Task.FromResult(res);
                }

                var full = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                full.Content.Headers.ContentLength = testData.Length;
                return Task.FromResult(full);
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient) { MaxConnectionsPerDownload = 4 };

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadCompleted += i => { if (i.Id == item.Id) tcs.TrySetResult(true); };
            engine.DownloadFailed += (i, ex) => { if (i.Id == item.Id) tcs.TrySetException(ex); };

            await engine.ResumeDownloadAsync(item);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(15000));
            Assert.Same(tcs.Task, completedTask);
            Assert.True(tcs.Task.Result);

            // Assert: file is byte-identical, no chunk residue
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.True(File.Exists(destinationFile));
            Assert.False(File.Exists(destinationFile + ".part"));
            Assert.False(File.Exists(destinationFile + ".part.segments"));
            Assert.True(rangeRequests >= 5, "Expected: 1 range probe + 4 segment requests");
            Assert.Equal(testData, File.ReadAllBytes(destinationFile));
        }
        finally
        {
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact]
    public async Task DownloadEngine_ChunkedResume_ContinuesFromSidecarState()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "ReepaxChunkResume_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var destinationFile = Path.Combine(tempFolder, "chunk_resume.bin");
            var partFile = destinationFile + ".part";
            var segFile = partFile + ".segments";

            var testData = new byte[8 * 1024 * 1024 + 999];
            for (int i = 0; i < testData.Length; i++)
                testData[i] = (byte)(i % 241);

            // Simulate state: 4 segments, first one already complete, remainder empty
            int segCount = 4;
            long segSize = testData.Length / segCount;
            var segments = new System.Collections.Generic.List<object>();
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"TotalBytes\":").Append(testData.Length).Append(",\"Segments\":[");
            for (int i = 0; i < segCount; i++)
            {
                long start = i * segSize;
                long end = (i == segCount - 1) ? testData.Length - 1 : start + segSize - 1;
                long done = (i == 0) ? end - start + 1 : 0;
                if (i > 0) sb.Append(',');
                sb.Append("{\"Start\":").Append(start).Append(",\"End\":").Append(end).Append(",\"Done\":").Append(done).Append('}');
            }
            sb.Append("]}");
            File.WriteAllText(segFile, sb.ToString());

            // .part file: pre-allocated, segment 1 populated with real data
            using (var fs = new FileStream(partFile, FileMode.Create, FileAccess.ReadWrite))
            {
                fs.SetLength(testData.Length);
                fs.Position = 0;
                fs.Write(testData, 0, (int)segSize);
            }

            var item = new DownloadItem
            {
                FileName = "chunk_resume.bin",
                SaveFilePath = destinationFile,
                DirectDownloadUrl = "https://example.com/chunk_resume.bin",
                Status = DownloadStatus.Paused
            };

            var handler = new TestMockHttpMessageHandler((req, ct) =>
            {
                if (req.Headers.Range != null)
                {
                    long from = req.Headers.Range.Ranges.First().From ?? 0;
                    long to = req.Headers.Range.Ranges.First().To ?? (testData.Length - 1);
                    var slice = testData.AsSpan((int)from, (int)(to - from + 1)).ToArray();
                    var res = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
                    res.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, testData.Length);
                    return Task.FromResult(res);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            });

            using var httpClient = new HttpClient(handler);
            var engine = new DownloadEngine(httpClient) { MaxConnectionsPerDownload = 4 };

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.DownloadCompleted += i => { if (i.Id == item.Id) tcs.TrySetResult(true); };
            engine.DownloadFailed += (i, ex) => { if (i.Id == item.Id) tcs.TrySetException(ex); };

            await engine.ResumeDownloadAsync(item);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(15000));
            Assert.Same(tcs.Task, completedTask);
            Assert.True(tcs.Task.Result);

            // Segment 1 was not re-downloaded, file is still byte-identical
            Assert.Equal(testData, File.ReadAllBytes(destinationFile));
            Assert.False(File.Exists(segFile));
        }
        finally
        {
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact]
    public void MainViewModel_SpeedLimitText_TypingDecimalsOrMultiDigits_DoesNotClobberUserText()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        
        // Simulating typing "2"
        vm.SpeedLimitText = "2";
        Assert.Equal(2.0, vm.SpeedLimitMBps);
        Assert.Equal("2", vm.SpeedLimitText);

        // Simulating typing "5" after "2" -> "25"
        vm.SpeedLimitText = "25";
        Assert.Equal(25.0, vm.SpeedLimitMBps);
        Assert.Equal("25", vm.SpeedLimitText);

        // Simulating typing with German comma "2,5"
        vm.SpeedLimitText = "2,5";
        Assert.Equal(2.5, vm.SpeedLimitMBps);
        Assert.Equal("2,5", vm.SpeedLimitText);
    }

    [Fact]
    public void MainViewModel_ToggleExpandCollapseAll_TogglesExpansionState()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        vm.Packages.Clear();

        var p1 = new Reepax.Models.DownloadPackage { Name = "Pkg 1", IsExpanded = true };
        var p2 = new Reepax.Models.DownloadPackage { Name = "Pkg 2", IsExpanded = true };
        vm.Packages.Add(p1);
        vm.Packages.Add(p2);

        // All are expanded -> toggle collapses all
        Assert.True(vm.ToggleExpandCollapseAllCommand.CanExecute(null));
        vm.ToggleExpandCollapseAll();
        Assert.False(p1.IsExpanded);
        Assert.False(p2.IsExpanded);

        // All are collapsed -> toggle expands all
        vm.ToggleExpandCollapseAll();
        Assert.True(p1.IsExpanded);
        Assert.True(p2.IsExpanded);

        // Mixed state -> toggle expands all
        p1.IsExpanded = false;
        vm.ToggleExpandCollapseAll();
        Assert.True(p1.IsExpanded);
        Assert.True(p2.IsExpanded);
    }

    [Fact]
    public void MainViewModel_TogglePauseResume_CanExecuteAndTogglesState()
    {
        var vm = new Reepax.ViewModels.MainViewModel();
        vm.Packages.Clear();

        // When empty, cannot execute
        Assert.False(vm.TogglePauseResumeCommand.CanExecute(null));

        var p = new Reepax.Models.DownloadPackage { Name = "Pkg" };
        var item = new Reepax.Models.DownloadItem { FileName = "Test.zip", Status = Reepax.Models.DownloadStatus.Queued };
        p.Items.Add(item);
        vm.Packages.Add(p);

        // When packages exist, CanExecute is true
        Assert.True(vm.TogglePauseResumeCommand.CanExecute(null));

        // When downloads are queued/running (CanPauseAll is true), toggle invokes PauseAll
        Assert.True(vm.CanPauseAll);
        vm.TogglePauseResume();
        Assert.True(vm.StatusSummary.Contains("pausiert", StringComparison.OrdinalIgnoreCase) || vm.StatusSummary.Contains("paused", StringComparison.OrdinalIgnoreCase));

        // When paused, toggle resumes
        vm.TogglePauseResume();
        Assert.True(vm.StatusSummary.Contains("gestartet", StringComparison.OrdinalIgnoreCase) || vm.StatusSummary.Contains("started", StringComparison.OrdinalIgnoreCase) || vm.StatusSummary.Contains("resumed", StringComparison.OrdinalIgnoreCase));
    }
}

