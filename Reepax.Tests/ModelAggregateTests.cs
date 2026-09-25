using System;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class ModelAggregateTests
{
    [Fact]
    public void DownloadPackage_RecalculatesAggregatesProperly()
    {
        // Arrange
        var package = new DownloadPackage { Name = "Test Package" };
        var item1 = new DownloadItem { TotalBytes = 100_000_000, DownloadedBytes = 50_000_000, SpeedBytesPerSecond = 5_000_000, Status = DownloadStatus.Downloading };
        var item2 = new DownloadItem { TotalBytes = 100_000_000, DownloadedBytes = 100_000_000, SpeedBytesPerSecond = 0, Status = DownloadStatus.Completed };

        // Act
        package.Items.Add(item1);
        package.Items.Add(item2);

        // Assert
        Assert.Equal(200_000_000, package.TotalBytes);
        Assert.Equal(150_000_000, package.DownloadedBytes);
        Assert.Equal(75.0, package.ProgressPercentage);
        Assert.Equal(5_000_000, package.SpeedBytesPerSecond);
        Assert.Equal(1, package.CompletedItemsCount);
        Assert.Equal(2, package.TotalItemsCount);
        Assert.Equal(DownloadStatus.Downloading, package.Status);

        // Now complete item 1 as well
        item1.DownloadedBytes = 100_000_000;
        item1.SpeedBytesPerSecond = 0;
        item1.Status = DownloadStatus.Completed;

        Assert.Equal(200_000_000, package.DownloadedBytes);
        Assert.Equal(100.0, package.ProgressPercentage);
        Assert.Equal(2, package.CompletedItemsCount);
        Assert.Equal(DownloadStatus.Completed, package.Status);
    }

    [Fact]
    public void DownloadPackage_NextTaskProperties_ReflectSelectedOptionsCorrectly()
    {
        var package = new DownloadPackage { Name = "Test Package" };

        // 1. Initially disabled
        Assert.False(package.HasNextTasks);
        Assert.Equal(string.Empty, package.NextTaskSummary);
        Assert.Equal(string.Empty, package.NextTaskTooltip);

        // 2. Auto-Extract enabled
        package.AutoExtractArchives = true;
        Assert.True(package.HasNextTasks);
        Assert.Equal($"➔ {Loc.Get("NextTask_Extract")}", package.NextTaskSummary);
        Assert.Contains(Loc.Get("NextTask_TooltipExtractNormal"), package.NextTaskTooltip);

        // 3. Auto-Extract + PC-schonend
        package.LowResourceExtraction = true;
        Assert.Equal($"➔ {Loc.Get("NextTask_ExtractLowResource")}", package.NextTaskSummary);
        Assert.Contains(Loc.Get("NextTask_TooltipExtractLowResource"), package.NextTaskTooltip);

        // 4. Auto-Extract + Delete Archive
        package.LowResourceExtraction = false;
        package.DeleteArchiveAfterExtraction = true;
        Assert.Equal($"➔ {Loc.Get("NextTask_Extract")} & {Loc.Get("NextTask_DeleteArchive")}", package.NextTaskSummary);
        Assert.Contains(Loc.Get("NextTask_TooltipDeleteArchive"), package.NextTaskTooltip);

        // 5. Auto-Extract + Recycle Bin
        package.DeleteArchiveAfterExtraction = false;
        package.MoveArchiveToRecycleBin = true;
        Assert.Equal($"➔ {Loc.Get("NextTask_Extract")} & {Loc.Get("NextTask_MoveToRecycleBin")}", package.NextTaskSummary);
        Assert.Contains(Loc.Get("NextTask_TooltipRecycleArchive"), package.NextTaskTooltip);

        // 6. Turn off AutoExtract -> all empty
        package.AutoExtractArchives = false;
        Assert.False(package.HasNextTasks);
        Assert.Equal(string.Empty, package.NextTaskSummary);
        Assert.Equal(string.Empty, package.NextTaskTooltip);
    }

    [Fact]
    public void DownloadPackage_Duration_And_AverageSpeed_CalculatedAccurately()
    {
        var package = new DownloadPackage { Name = "Duration Test Package" };
        var start = DateTime.Now.AddMinutes(-5);
        var end = DateTime.Now;

        var item = new DownloadItem
        {
            FileName = "game.part1.rar",
            TotalBytes = 100 * 1024 * 1024,
            DownloadedBytes = 100 * 1024 * 1024,
            StartedAt = start,
            CompletedAt = end,
            Status = DownloadStatus.Completed
        };

        package.Items.Add(item);
        package.StartedAt = start;
        package.CompletedAt = end;
        package.RecalculateAggregates();

        // 5 minutes duration
        Assert.Equal(5, (int)package.Duration.TotalMinutes);
        Assert.Contains("5m", package.DurationFormatted);

        // 100 MB in 300 seconds = ~0.33 MB/s
        Assert.True(package.AverageSpeedBytesPerSecond > 0);
        Assert.Contains("KB/s", package.AverageSpeedFormatted);
    }

    [Fact]
    public void DownloadPackage_UsedHosters_And_DeselectedItems_TrackedAccurately()
    {
        var package = new DownloadPackage { Name = "Hosters Test" };
        var item1 = new DownloadItem { FileName = "part1.rar", HosterName = "Rapidgator", IsEnabled = true };
        var item2 = new DownloadItem { FileName = "part2.rar", HosterName = "DDownload", IsEnabled = false };
        var item3 = new DownloadItem { FileName = "part3.rar", HosterName = "Rapidgator", IsEnabled = true };

        package.Items.Add(item1);
        package.Items.Add(item2);
        package.Items.Add(item3);
        package.RecalculateAggregates();

        Assert.Equal(1, package.DeselectedItemsCount);
        Assert.Equal(2, package.UsedHosters.Count);
        Assert.Contains("Rapidgator", package.UsedHosters);
        Assert.Contains("DDownload", package.UsedHosters);
    }

    [Fact]
    public void DownloadPackage_WhenPackageDisabled_SetsStatusToPausedAndMessageToUebersprungen()
    {
        var package = new DownloadPackage { Name = "Disabled Package", IsEnabled = false };
        var item1 = new DownloadItem { FileName = "part1.rar", Status = DownloadStatus.Downloading, SpeedBytesPerSecond = 1000 };
        package.Items.Add(item1);
        package.RecalculateAggregates();

        Assert.Equal(DownloadStatus.Paused, package.Status);
        Assert.Equal(Loc.Get("Status_Skipped"), package.StatusMessage);
        Assert.Equal(0, package.SpeedBytesPerSecond);
        Assert.Equal(0, package.RemainingSeconds);
    }

    [Fact]
    public void DownloadPackage_WhenAllItemsDisabled_SetsStatusToPausedAndMessageToUebersprungen()
    {
        var package = new DownloadPackage { Name = "All Disabled Items Package", IsEnabled = true };
        var item1 = new DownloadItem { FileName = "part1.rar", IsEnabled = false, Status = DownloadStatus.Paused };
        var item2 = new DownloadItem { FileName = "part2.rar", IsEnabled = false, Status = DownloadStatus.Paused };
        package.Items.Add(item1);
        package.Items.Add(item2);
        package.RecalculateAggregates();

        Assert.Equal(DownloadStatus.Paused, package.Status);
        Assert.Equal(Loc.Get("Status_Skipped"), package.StatusMessage);
        Assert.Equal(0, package.SpeedBytesPerSecond);
    }

    [Fact]
    public void DownloadPackage_WhenAllEnabledItemsCompleted_SetsStatusToCompleted()
    {
        var package = new DownloadPackage { Name = "Partially Disabled Package", IsEnabled = true };
        var item1 = new DownloadItem { FileName = "part1.rar", IsEnabled = true, Status = DownloadStatus.Completed, TotalBytes = 100, DownloadedBytes = 100 };
        var item2 = new DownloadItem { FileName = "part2.rar", IsEnabled = false, Status = DownloadStatus.Paused, TotalBytes = 100, DownloadedBytes = 0 };
        package.Items.Add(item1);
        package.Items.Add(item2);
        package.RecalculateAggregates();

        Assert.Equal(DownloadStatus.Completed, package.Status);
        Assert.Equal(Loc.Get("Status_Completed"), package.StatusMessage);
    }

    [Fact]
    public void DownloadPackage_ItemIsEnabledChanged_TriggersRecalculateAggregates()
    {
        var package = new DownloadPackage { Name = "Toggle Item Package" };
        var item1 = new DownloadItem { FileName = "part1.rar", IsEnabled = true, Status = DownloadStatus.Downloading, SpeedBytesPerSecond = 5000 };
        package.Items.Add(item1);
        package.RecalculateAggregates();
        Assert.Equal(DownloadStatus.Downloading, package.Status);
        Assert.Equal(5000, package.SpeedBytesPerSecond);

        // Act - disable item
        item1.IsEnabled = false;

        // Assert - aggregate updated automatically
        Assert.Equal(DownloadStatus.Paused, package.Status);
        Assert.Equal(Loc.Get("Status_Skipped"), package.StatusMessage);
    }

    [Fact]
    public void DownloadItem_NotifyPropertyChangedFor_ComputedPropertiesTrigger()
    {
        var item = new DownloadItem();
        var changedProperties = new System.Collections.Generic.List<string>();
        item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null)
                changedProperties.Add(e.PropertyName);
        };

        item.StartedAt = DateTime.Now.AddMinutes(-10);
        Assert.Contains(nameof(DownloadItem.Duration), changedProperties);
        Assert.Contains(nameof(DownloadItem.DurationFormatted), changedProperties);
        Assert.Contains(nameof(DownloadItem.AverageSpeedBytesPerSecond), changedProperties);
        Assert.Contains(nameof(DownloadItem.AverageSpeedFormatted), changedProperties);

        changedProperties.Clear();
        item.DownloadedBytes = 1000;
        Assert.Contains(nameof(DownloadItem.AverageSpeedBytesPerSecond), changedProperties);
        Assert.Contains(nameof(DownloadItem.AverageSpeedFormatted), changedProperties);

        changedProperties.Clear();
        item.SpeedBytesPerSecond = 500;
        Assert.Contains(nameof(DownloadItem.AverageSpeedBytesPerSecond), changedProperties);
        Assert.Contains(nameof(DownloadItem.AverageSpeedFormatted), changedProperties);
    }

    [Fact]
    public void DownloadPackage_NotifyPropertyChangedFor_ComputedPropertiesTrigger()
    {
        var package = new DownloadPackage();
        var changedProperties = new System.Collections.Generic.List<string>();
        package.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null)
                changedProperties.Add(e.PropertyName);
        };

        package.TotalBytes = 5000;
        Assert.Contains(nameof(DownloadPackage.RequiredDiskSpaceBytes), changedProperties);

        changedProperties.Clear();
        package.StartedAt = DateTime.Now.AddMinutes(-5);
        Assert.Contains(nameof(DownloadPackage.Duration), changedProperties);
        Assert.Contains(nameof(DownloadPackage.DurationFormatted), changedProperties);
        Assert.Contains(nameof(DownloadPackage.AverageSpeedBytesPerSecond), changedProperties);
        Assert.Contains(nameof(DownloadPackage.AverageSpeedFormatted), changedProperties);
    }

    [Theory]
    [InlineData("http://.rapidgator.net/file/123", "rapidgator.net", "Rapidgator")]
    [InlineData("http://...example.com/file", "example.com", "Example")]
    [InlineData("https://.somedomain.org/download", "somedomain.org", "Somedomain")]
    [InlineData("http://....", "unknown", "Link")]
    [InlineData("http://.a.com", "a.com", "A")]
    [InlineData("", "generic", "Web")]
    public void HosterInfo_DetectHoster_GuardsAgainstLeadingDotsAndMalformedHosts(string url, string expectedDomain, string expectedDisplayName)
    {
        var info = HosterInfo.DetectHoster(url);
        Assert.NotNull(info);
        Assert.Equal(expectedDomain, info.Domain);
        Assert.Equal(expectedDisplayName, info.DisplayName);
    }

    [Fact]
    public void DownloadPackage_RemainingSeconds_IsNeverLowerThanActiveItemRemainingSeconds()
    {
        // Scenario: Item 1 has 50 MB remaining at 5 MB/s (10s ETA)
        // Scenario: Item 2 has 100 MB remaining at 0.2 MB/s (500s ETA)
        // Total speed = 5.2 MB/s, total remaining = 150 MB.
        // Naive ETA would be ~28.8s, but Item 2 needs 500s. Package cannot finish before Item 2!
        var package = new DownloadPackage { Name = "ETA Test Package" };
        var item1 = new DownloadItem
        {
            FileName = "part1.rar",
            TotalBytes = 100_000_000,
            DownloadedBytes = 50_000_000,
            SpeedBytesPerSecond = 5_000_000,
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        item1.RemainingSeconds = 10; // 50MB / 5MB/s = 10s

        var item2 = new DownloadItem
        {
            FileName = "part2.rar",
            TotalBytes = 100_000_000,
            DownloadedBytes = 0,
            SpeedBytesPerSecond = 200_000,
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        item2.RemainingSeconds = 500; // 100MB / 0.2MB/s = 500s

        package.Items.Add(item1);
        package.Items.Add(item2);
        package.RecalculateAggregates();

        // Package ETA must be at least the slowest active item (500s)
        Assert.True(package.RemainingSeconds >= 500, $"Expected >= 500, but was {package.RemainingSeconds}");
        Assert.Equal(500, package.RemainingSeconds);
    }

    [Fact]
    public void DownloadPackage_RemainingSeconds_ExcludesDisabledItems()
    {
        var package = new DownloadPackage { Name = "ETA Disabled Item Package" };
        var activeItem = new DownloadItem
        {
            FileName = "part1.rar",
            TotalBytes = 10_000_000,
            DownloadedBytes = 0,
            SpeedBytesPerSecond = 1_000_000,
            Status = DownloadStatus.Downloading,
            IsEnabled = true
        };
        activeItem.RemainingSeconds = 10;

        var disabledItem = new DownloadItem
        {
            FileName = "part2.rar",
            TotalBytes = 5_000_000_000, // 5 GB
            DownloadedBytes = 0,
            SpeedBytesPerSecond = 0,
            Status = DownloadStatus.Paused,
            IsEnabled = false
        };

        package.Items.Add(activeItem);
        package.Items.Add(disabledItem);
        package.RecalculateAggregates();

        // Only activeItem is enabled, so ETA should be 10s (10MB / 1MB/s), NOT 5010s
        Assert.Equal(10, package.RemainingSeconds);
    }

    [Fact]
    public void DownloadPackage_RemainingSeconds_IsZeroWhenAllItemsCompleted()
    {
        var package = new DownloadPackage { Name = "Completed ETA Package" };
        var item1 = new DownloadItem
        {
            FileName = "part1.rar",
            TotalBytes = 50_000_000,
            DownloadedBytes = 50_000_000,
            SpeedBytesPerSecond = 0,
            Status = DownloadStatus.Completed,
            IsEnabled = true
        };

        package.Items.Add(item1);
        package.RecalculateAggregates();

        Assert.Equal(0, package.RemainingSeconds);
        Assert.Equal(DownloadStatus.Completed, package.Status);
    }

    [Fact]
    public void DownloadPackage_WhenItemsDeselected_ProgressReaches100PercentWhenEnabledItemsComplete()
    {
        // 10 items (100 MB each = 1000 MB total)
        // 3 items deselected (skipped)
        // 7 items enabled and completed (700 MB)
        var package = new DownloadPackage { Name = "Deselected Items Test Package" };
        for (int i = 1; i <= 10; i++)
        {
            var isEnabled = i <= 7;
            package.Items.Add(new DownloadItem
            {
                FileName = $"part{i}.rar",
                TotalBytes = 100_000_000,
                DownloadedBytes = isEnabled ? 100_000_000 : 0,
                Status = isEnabled ? DownloadStatus.Completed : DownloadStatus.Paused,
                IsEnabled = isEnabled
            });
        }

        package.RecalculateAggregates();

        Assert.Equal(DownloadStatus.Completed, package.Status);
        Assert.Equal(100.0, package.ProgressPercentage);
        Assert.Equal(700_000_000, package.TotalBytes);
        Assert.Equal(700_000_000, package.DownloadedBytes);
        Assert.Equal(7, package.CompletedItemsCount);
        Assert.Equal(7, package.EnabledItemsCount);
        Assert.Equal(3, package.DeselectedItemsCount);
        Assert.Equal(10, package.TotalItemsCount);
        Assert.Contains("7 / 7", package.ItemsCountSummary);
        Assert.Contains("3", package.ItemsCountSummary);
    }

    [Fact]
    public void DownloadPackage_WhenItemsDeselected_MidDownloadProgressIsCalculatedFromEnabledItems()
    {
        // 10 items (100 MB each = 1000 MB total)
        // 3 items deselected (300 MB)
        // 7 enabled items (700 MB enabled total)
        // 350 MB downloaded across enabled items -> must be 50.0% progress (NOT 35.0%)
        var package = new DownloadPackage { Name = "Deselected Mid Download Package" };
        for (int i = 1; i <= 10; i++)
        {
            var isEnabled = i <= 7;
            package.Items.Add(new DownloadItem
            {
                FileName = $"part{i}.rar",
                TotalBytes = 100_000_000,
                DownloadedBytes = isEnabled && i <= 3 ? 100_000_000 : (isEnabled && i == 4 ? 50_000_000 : 0),
                Status = isEnabled && i <= 3 ? DownloadStatus.Completed : (isEnabled && i == 4 ? DownloadStatus.Downloading : DownloadStatus.Paused),
                IsEnabled = isEnabled
            });
        }

        package.RecalculateAggregates();

        Assert.Equal(700_000_000, package.TotalBytes);
        Assert.Equal(350_000_000, package.DownloadedBytes);
        Assert.Equal(50.0, package.ProgressPercentage);
        Assert.Equal(3, package.CompletedItemsCount);
        Assert.Equal(7, package.EnabledItemsCount);
        Assert.Equal(3, package.DeselectedItemsCount);
    }

    [Fact]
    public void DownloadPackage_ItemsCountSummary_DisplaysCorrectly()
    {
        var package = new DownloadPackage { Name = "Summary Test" };
        var item1 = new DownloadItem { FileName = "part1.rar", IsEnabled = true, Status = DownloadStatus.Completed };
        var item2 = new DownloadItem { FileName = "part2.rar", IsEnabled = true, Status = DownloadStatus.Downloading };
        package.Items.Add(item1);
        package.Items.Add(item2);

        // Test German localization
        LocalizationService.Instance.CurrentLanguage = "de";
        package.RecalculateAggregates();
        Assert.Contains("1 / 2 Dateien", package.ItemsCountSummary);
        Assert.DoesNotContain("abgewählt", package.ItemsCountSummary);

        // Deselect item 2: verify localized German summary output
        item2.IsEnabled = false;
        package.RecalculateAggregates();
        Assert.Contains("1 / 1 Dateien (1 abgewählt)", package.ItemsCountSummary);

        // Test English localization
        LocalizationService.Instance.CurrentLanguage = "en";
        package.RecalculateAggregates();
        Assert.Contains("1 / 1 Files (1 deselected)", package.ItemsCountSummary);
    }

    [Fact]
    public void DownloadPackage_CanEditPackage_ReflectsPackageStateAccurately()
    {
        var package = new DownloadPackage { Name = "Edit Test" };
        var item1 = new DownloadItem { FileName = "part1.rar", IsEnabled = true, Status = DownloadStatus.Downloading };
        var item2 = new DownloadItem { FileName = "part2.rar", IsEnabled = true, Status = DownloadStatus.Queued };
        package.Items.Add(item1);
        package.Items.Add(item2);
        package.RecalculateAggregates();

        // 1. While downloading and not all completed -> editable
        Assert.True(package.CanEditPackage);

        // 2. Only item1 completed, item2 still queued -> still editable
        item1.Status = DownloadStatus.Completed;
        package.RecalculateAggregates();
        Assert.True(package.CanEditPackage);

        // 3. All items completed -> not editable anymore (download finished)
        item2.Status = DownloadStatus.Completed;
        package.RecalculateAggregates();
        Assert.False(package.CanEditPackage);

        // 4. Package status explicitly set to Completed -> not editable
        var pkg2 = new DownloadPackage { Name = "Completed Pkg", Status = DownloadStatus.Completed };
        Assert.False(pkg2.CanEditPackage);
    }

    [Fact]
    public void HosterIconService_ExtractDomain_ResolvesLegacyAndDisplayName()
    {
        var legacyToken = "fuckingfast";
        var expectedDomain = FastHostResolver.CanonicalDomain;

        // Legacy name
        Assert.Equal(expectedDomain, HosterIconService.ExtractDomain(legacyToken));

        // Display name
        Assert.Equal(expectedDomain, HosterIconService.ExtractDomain("FastHost"));
        Assert.Equal(expectedDomain, HosterIconService.ExtractDomain("fasthost"));

        // Direct canonical domain
        Assert.Equal(expectedDomain, HosterIconService.ExtractDomain(expectedDomain));

        // URL format
        Assert.Equal(expectedDomain, HosterIconService.ExtractDomain($"https://dl.{expectedDomain}/dl/test"));

        // Other known hosters
        Assert.Equal("rapidgator.net", HosterIconService.ExtractDomain("Rapidgator"));
        Assert.Equal("rapidgator.net", HosterIconService.ExtractDomain("rg.to"));
        Assert.Equal("ddownload.com", HosterIconService.ExtractDomain("DDownload"));
        Assert.Equal("ddownload.com", HosterIconService.ExtractDomain("ddl.to"));
    }

    [Fact]
    public void HosterImageConverter_ResolvesItemAndStringInputs()
    {
        var converter = new Reepax.Converters.HosterImageConverter();
        var legacyToken = "fuckingfast";

        // With DownloadItem (legacy name)
        var item1 = new DownloadItem { HosterName = legacyToken };
        var icon1 = converter.Convert(item1, typeof(System.Windows.Media.ImageSource), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(icon1);

        // With DownloadItem (display name)
        var item2 = new DownloadItem { HosterName = "FastHost" };
        var icon2 = converter.Convert(item2, typeof(System.Windows.Media.ImageSource), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(icon2);

        // With string legacy
        var icon3 = converter.Convert(legacyToken, typeof(System.Windows.Media.ImageSource), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(icon3);

        // With string display name
        var icon4 = converter.Convert("FastHost", typeof(System.Windows.Media.ImageSource), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(icon4);

        // With empty hoster but original URL
        var item3 = new DownloadItem { HosterName = "Unknown", OriginalUrl = "https://rapidgator.net/file/123" };
        var icon5 = converter.Convert(item3, typeof(System.Windows.Media.ImageSource), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(icon5);
    }
}
