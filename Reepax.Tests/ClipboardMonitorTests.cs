using System;
using System.Collections.Generic;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class ClipboardMonitorTests
{
    [Fact]
    public void ExtractLinksFromData_HtmlWithHref_ExtractsLinksCorrectly()
    {
        // HTML copied from browser (e.g. table or list with anchor tags)
        string html = @"
<html>
  <body>
    <a href=""https://rapidgator.net/file/123/data.part1.rar"">Part 1</a>
    <a href=""https://ddownload.com/abc/data.part2.rar"">Part 2</a>
  </body>
</html>";
        string text = "Part 1 Part 2"; // Plain text representation does not contain the URLs

        var result = ClipboardMonitorService.ExtractLinksFromData(html, text);

        Assert.Equal(2, result.Count);
        Assert.Equal("https://rapidgator.net/file/123/data.part1.rar", result[0]);
        Assert.Equal("https://ddownload.com/abc/data.part2.rar", result[1]);
    }

    [Fact]
    public void ExtractLinksFromData_PlainTextMultipleUrls_ExtractsAll()
    {
        string text = @"
Hier sind die Links:
https://rapidgator.net/file/1/part1.rar
https://rapidgator.net/file/2/part2.rar
https://rapidgator.net/file/3/part3.rar
";

        var result = ClipboardMonitorService.ExtractLinksFromData(null, text);

        Assert.Equal(3, result.Count);
        Assert.Equal("https://rapidgator.net/file/1/part1.rar", result[0]);
        Assert.Equal("https://rapidgator.net/file/2/part2.rar", result[1]);
        Assert.Equal("https://rapidgator.net/file/3/part3.rar", result[2]);
    }

    [Fact]
    public void ExtractLinksFromData_MixedHtmlAndText_DeduplicatesExactMatches()
    {
        string html = @"<a href=""https://rapidgator.net/file/10/file.rar"">Download</a>";
        string text = "Download https://rapidgator.net/file/10/file.rar";

        var result = ClipboardMonitorService.ExtractLinksFromData(html, text);

        Assert.Single(result);
        Assert.Equal("https://rapidgator.net/file/10/file.rar", result[0]);
    }

    [Fact]
    public void ExtractLinksFromData_HrefTextIndicator_ExtractsLinks()
    {
        string text = "href: https://1fichier.com/?abcdef123";

        var result = ClipboardMonitorService.ExtractLinksFromData(null, text);

        Assert.Single(result);
        Assert.Equal("https://1fichier.com/?abcdef123", result[0]);
    }

    [Fact]
    public async Task RegisterInternalCopy_OnlySuppressesTheClipboardChangeWrittenByReepax()
    {
        const string copiedLink = "https://files.example.invalid/internal.zip";
        const uint sequence = 2001;

        Assert.False(ClipboardMonitorService.IsInternalCopy(copiedLink, sequence));

        ClipboardMonitorService.RegisterInternalCopy(copiedLink, sequence);

        Assert.True(ClipboardMonitorService.IsInternalCopy(copiedLink, sequence));
        Assert.False(ClipboardMonitorService.IsInternalCopy("https://files.example.invalid/other.zip", sequence));
        var internalCopy = await ClipboardMonitorService.ReadClipboardLinksAsync(() => sequence, () => (null, copiedLink));
        var externalCopy = await ClipboardMonitorService.ReadClipboardLinksAsync(() => sequence + 1, () => (null, copiedLink));
        Assert.Empty(internalCopy.Links);
        Assert.Equal(new[] { copiedLink }, externalCopy.Links);
    }

    [Fact]
    public async Task ClipboardRead_RetriesAfterLockAndDiscardsDataFromAnUnstableChange()
    {
        const string staleLink = "https://files.example.invalid/stale.zip";
        const string currentLink = "https://files.example.invalid/current.zip";
        uint sequence = 3100;
        int reads = 0;
        (string? Html, string? Text) Read()
        {
            if (++reads == 1) throw new System.Runtime.InteropServices.COMException("Clipboard locked", unchecked((int)0x800401D0));
            if (reads != 2) return (null, currentLink);
            sequence++;
            return (null, staleLink);
        }

        var snapshot = await ClipboardMonitorService.ReadClipboardLinksAsync(() => sequence, Read);

        Assert.Equal(3, reads);
        Assert.Equal(3101u, snapshot.Sequence);
        Assert.Equal(new[] { currentLink }, snapshot.Links);
    }

    [Fact]
    public void ToggleClipboardMonitor_UpdatesStateAndTooltip()
    {
        var vm = new MainViewModel();
        bool initial = vm.EnableClipboardMonitor;

        vm.ToggleClipboardMonitorCommand.Execute(null);

        Assert.Equal(!initial, vm.EnableClipboardMonitor);
        Assert.NotEmpty(vm.ClipboardMonitorTooltip);

        // Toggle back
        vm.ToggleClipboardMonitorCommand.Execute(null);
        Assert.Equal(initial, vm.EnableClipboardMonitor);
    }

    [Fact]
    public void ExtractLinksFromData_NonFilehosterLinks_AreCompletelyIgnored()
    {
        // When user copies ordinary website URLs while surfing, clipboard grabber MUST NOT trigger
        string text = @"
https://www.google.com/search?q=csharp
https://youtube.com/watch?v=dQw4w9WgXcQ
https://github.com/torvalds/linux
https://en.wikipedia.org/wiki/Computer
https://spiegel.de/politik/nachrichten
";

        var result = ClipboardMonitorService.ExtractLinksFromData(null, text);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractLinksFromData_MixedContent_OnlyExtractsFilehosterLinks()
    {
        // Forum post or chat containing both file downloads and website/social links
        string html = @"
<div>
  Check out my website at <a href=""https://myblog.example.com"">My Blog</a> or follow me:
  <a href=""https://twitter.com/user"">Twitter</a>.
  Download release here:
  <a href=""https://rapidgator.net/file/888/data.part1.rar"">Part 1 (Rapidgator)</a>
  <a href=""https://ddownload.com/999/data.part2.rar"">Part 2 (DDownload)</a>
</div>";

        var result = ClipboardMonitorService.ExtractLinksFromData(html, null);

        Assert.Equal(2, result.Count);
        Assert.Contains("https://rapidgator.net/file/888/data.part1.rar", result);
        Assert.Contains("https://ddownload.com/999/data.part2.rar", result);
        Assert.DoesNotContain(result, u => u.Contains("myblog") || u.Contains("twitter"));
    }

    [Fact]
    public void ExtractLinksFromData_FilecryptAndDirectArchives_AreRecognized()
    {
        string text = @"
https://filecrypt.cc/Container/12345ABCDE
https://custom-server.org/downloads/setup_package.7z
";

        var result = ClipboardMonitorService.ExtractLinksFromData(null, text);

        Assert.Equal(2, result.Count);
        Assert.Contains("https://filecrypt.cc/Container/12345ABCDE", result);
        Assert.Contains("https://custom-server.org/downloads/setup_package.7z", result);
    }
}

