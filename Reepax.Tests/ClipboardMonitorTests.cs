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
    <a href=""https://rapidgator.net/file/123/game.part1.rar"">Part 1</a>
    <a href=""https://ddownload.com/abc/game.part2.rar"">Part 2</a>
  </body>
</html>";
        string text = "Part 1 Part 2"; // Plain text representation does not contain the URLs

        var result = ClipboardMonitorService.ExtractLinksFromData(html, text);

        Assert.Equal(2, result.Count);
        Assert.Equal("https://rapidgator.net/file/123/game.part1.rar", result[0]);
        Assert.Equal("https://ddownload.com/abc/game.part2.rar", result[1]);
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
    public void RegisterInternalCopy_PreventsSelfTriggering()
    {
        string copiedLink = "https://rapidgator.net/file/9999/internal.rar";

        Assert.False(ClipboardMonitorService.IsInternalCopy(copiedLink));

        ClipboardMonitorService.RegisterInternalCopy(copiedLink);

        Assert.True(ClipboardMonitorService.IsInternalCopy(copiedLink));
        Assert.False(ClipboardMonitorService.IsInternalCopy("https://rapidgator.net/file/9999/other.rar"));
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
  <a href=""https://rapidgator.net/file/888/game.part1.rar"">Part 1 (Rapidgator)</a>
  <a href=""https://ddownload.com/999/game.part2.rar"">Part 2 (DDownload)</a>
</div>";

        var result = ClipboardMonitorService.ExtractLinksFromData(html, null);

        Assert.Equal(2, result.Count);
        Assert.Contains("https://rapidgator.net/file/888/game.part1.rar", result);
        Assert.Contains("https://ddownload.com/999/game.part2.rar", result);
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

