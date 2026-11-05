using System.Globalization;
using System.IO;
using Reepax.Converters;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public sealed class DownloadOverviewTests
{
    private static DownloadItem File(long size, long downloaded, DownloadStatus status) => new()
    {
        FileName = "sample.bin", TotalBytes = size, DownloadedBytes = downloaded, Status = status,
        CreatedAt = new DateTime(2026, 1, 2), HosterName = "example.invalid"
    };
    private static DownloadPackage Package(string name, params DownloadItem[] items)
    {
        var package = new DownloadPackage { Name = name, AutoExtractArchives = false, AutoPar2Repair = false };
        foreach (var item in items) package.Items.Add(item);
        return package;
    }

    [Fact]
    public void Overview_ScopesDeduplicateClippedPackagesAndMixedSelections()
    {
        var first = File(100, 30, DownloadStatus.Paused);
        var second = File(200, 200, DownloadStatus.Completed);
        var other = File(300, 0, DownloadStatus.Queued);
        var root = Package("Sample", first);
        var child = Package("Child", second);
        child.ParentPackageId = root.Id;
        root.ClippedPackages.Add(child);
        var unrelated = Package("Other", other);
        var all = new[] { root, child, unrelated };
        DownloadOverviewSnapshot Capture(DownloadOverviewScope scope, bool disabled = false) =>
            DownloadOverviewSnapshot.Capture(all, new[] { root }, scope, disabled);
        Assert.Equal("3", Capture(DownloadOverviewScope.All).Format("Files", 1));
        Assert.Equal("3", Capture(DownloadOverviewScope.All).Format("Packages", 1));
        Assert.Equal("2", Capture(DownloadOverviewScope.Filtered).Format("Files", 1));
        root.IsSelected = child.IsSelected = second.IsSelected = true;
        Assert.Equal("2", Capture(DownloadOverviewScope.Selected).Format("Files", 1));
        Assert.Equal(BytesToHumanReadableConverter.FormatBytes(230), Capture(DownloadOverviewScope.Selected).Format("Downloaded", 1));
        root.IsSelected = child.IsSelected = false;
        Assert.Equal("1", Capture(DownloadOverviewScope.Selected).Format("Packages", 1));
        Assert.Equal("1", Capture(DownloadOverviewScope.Selected).Format("Files", 1));
        second.IsEnabled = false;
        Assert.Equal("0", Capture(DownloadOverviewScope.Selected).Format("Files", 1));
        Assert.Equal("1", Capture(DownloadOverviewScope.Selected, true).Format("Disabled", 1));
        root.IsEnabled = false;
        first.IsSelected = true;
        Assert.Equal("2", Capture(DownloadOverviewScope.Selected, true).Format("Disabled", 1));
        second.IsSelected = first.IsSelected = false;
        Assert.Equal("0", Capture(DownloadOverviewScope.Selected).Format("Files", 1));
        Assert.Equal("—", Capture(DownloadOverviewScope.Selected).Format("Eta", 1));
        Assert.Equal(30, first.DownloadedBytes);
        Assert.Equal(DownloadStatus.Paused, first.Status);
        Assert.Null(first.SaveFilePath);
    }

    [Fact]
    public void Overview_DoesNotClaimUnknownSizesOrPendingVerificationAsSuccess()
    {
        var active = File(1000, 250, DownloadStatus.Downloading);
        active.SpeedBytesPerSecond = 100;
        active.ElapsedDurationMs = 2500;
        var waiting = File(0, 0, DownloadStatus.WaitingForBrowser);
        var completed = File(100, 100, DownloadStatus.Completed);
        completed.ExpectedChecksum = "sha256:" + new string('a', 64);
        completed.CalculatedChecksum = new string('A', 64);
        completed.IsVerifyingIntegrity = true;
        var package = Package("Sample", active, waiting, completed);
        package.RequiresArchivePassword = true;
        DownloadOverviewSnapshot Capture() => DownloadOverviewSnapshot.Capture(new[] { package }, new[] { package }, DownloadOverviewScope.All, false);
        var snapshot = Capture();
        Assert.Equal("1", snapshot.Format("Active", 1));
        Assert.Equal("1", snapshot.Format("Waiting", 1));
        Assert.Equal("1", snapshot.Format("UnknownSizes", 1));
        Assert.Equal("0", snapshot.Format("Completed", 1));
        Assert.Equal("0", snapshot.Format("Verified", 1));
        Assert.Equal("1", snapshot.Format("Verifying", 1));
        Assert.Equal("1", snapshot.Format("Passwords", 1));
        Assert.StartsWith("≥ ", snapshot.Format("Size", 1));
        Assert.StartsWith("≥ ", snapshot.Format("Remaining", 1));
        Assert.Equal("—", snapshot.Format("Progress", 1));
        Assert.Equal("—", snapshot.Format("Eta", 1));
        completed.IsVerifyingIntegrity = false;
        Assert.Equal("1", Capture().Format("Verified", 1));
        Assert.Equal("1", Capture().Format("Completed", 1));
        waiting.Status = DownloadStatus.Completed;
        active.Status = DownloadStatus.Completed;
        active.DownloadedBytes = 1000;
        var finished = Capture();
        Assert.Equal("0", finished.Format("UnknownSizes", 1));
        Assert.Equal("100 %", finished.Format("Progress", 1));
        Assert.Equal("00:00", finished.Format("Eta", 1));
        Assert.Equal("3", finished.Format("Completed", 1));
        Assert.Equal("1", finished.Format("Passwords", 1));
    }

    [Fact]
    public void Overview_HasAnEstimateOnlyForKnownUnblockedTransfers()
    {
        var active = File(1000, 250, DownloadStatus.Downloading);
        active.SpeedBytesPerSecond = 100;
        active.ElapsedDurationMs = 2500;
        var package = Package("Sample", active);
        DownloadOverviewSnapshot Capture() => DownloadOverviewSnapshot.Capture(new[] { package }, new[] { package }, DownloadOverviewScope.All, false);
        var snapshot = Capture();
        Assert.Equal((25d).ToString("0.00", CultureInfo.CurrentCulture) + " %", snapshot.Format("Progress", 1));
        Assert.StartsWith("≈ ", snapshot.Format("Eta", 1));
        var originalSpeed = snapshot.Format("Speed", 1);
        active.Status = DownloadStatus.Paused;
        Assert.Equal("—", Capture().Format("Eta", 1));
        Assert.NotEqual(originalSpeed, Capture().Format("Speed", 1));
        active.Status = DownloadStatus.Failed;
        Assert.Equal("1", Capture().Format("Failed", 1));
        Assert.Equal(2500, active.ElapsedDurationMs);
        Assert.Equal(250, active.DownloadedBytes);
    }

    [Fact]
    public void Overview_PersistsPresentationChoicesAndBoundsRefreshWork()
    {
        string directory = Path.Combine(Path.GetTempPath(), "overview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storage = new SettingsService(Path.Combine(directory, "settings.json"));
        int reads = 0;
        var items = new List<DownloadPackage> { Package("Sample", File(100, 0, DownloadStatus.Queued)) };
        IEnumerable<DownloadPackage> Read() { reads++; return items; }
        var model = new DownloadOverviewViewModel(storage, Read, () => items);
        try
        {
            Assert.True(model.IsShown);
            Assert.Equal(DownloadOverviewSettings.DefaultMetrics, model.VisibleMetrics.Select(metric => metric.Id));
            model.Refresh(true);
            Assert.Equal(0, reads);
            model.SetPresentationActive(true);
            Assert.Equal(1, reads);
            model.Refresh();
            Assert.Equal(1, reads);
            model.Scope = DownloadOverviewScope.Selected;
            Assert.Equal(2, reads);
            model.IsShown = false;
            model.Refresh(true);
            Assert.Equal(2, reads);
            model.IsShown = true;
            Assert.Equal(3, reads);
            model.SetPresentationActive(false);
            model.Scope = DownloadOverviewScope.Filtered;
            Assert.Equal(3, reads);
            model.SelectAllMetricsCommand.Execute(null);
            Assert.Equal(model.Metrics.Count, model.VisibleMetrics.Count);
            model.ResetMetricsCommand.Execute(null);
            Assert.Equal(DownloadOverviewSettings.DefaultMetrics, model.VisibleMetrics.Select(metric => metric.Id));
            foreach (var metric in model.Metrics) metric.IsVisible = false;
            Assert.False(model.HasVisibleMetrics);
            model.PanelHeight = double.NaN;
            Assert.Equal(DownloadOverviewSettings.DefaultHeight, model.PanelHeight);
            model.PanelHeight = -1;
            Assert.Equal(72, model.PanelHeight);
            model.PanelHeight = 9999;
            Assert.Equal(360, model.PanelHeight);
            model.Scope = (DownloadOverviewScope)999;
            Assert.Equal(DownloadOverviewScope.Filtered, model.Scope);
            model.PanelHeight = 224;
            model.IsExpanded = false;
            model.IncludeDisabled = true;
            model.HideCommand.Execute(null);
            storage.SaveSettings();
            var reopened = new SettingsService(Path.Combine(directory, "settings.json"));
            var restored = new DownloadOverviewViewModel(reopened, Read, Read);
            Assert.False(restored.IsShown);
            Assert.False(restored.IsExpanded);
            Assert.Equal(224, restored.PanelHeight);
            Assert.Equal(DownloadOverviewScope.Filtered, restored.Scope);
            Assert.True(restored.IncludeDisabled);
            Assert.Empty(restored.VisibleMetrics);
            restored.ResetMetricsCommand.Execute(null);
            reopened.SaveSettings();
        }
        finally
        {
            storage.SaveSettings();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Overview_SwapsPositionsWithoutDuplicatesAndRestoresTheOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "overview-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var storage = new SettingsService(Path.Combine(directory, "settings.json"));
        var model = new DownloadOverviewViewModel(storage, () => Array.Empty<DownloadPackage>(), () => Array.Empty<DownloadPackage>());
        try
        {
            var original = model.VisibleMetrics.ToArray();
            Assert.True(model.SwapMetrics(original[0], original[^1]));
            Assert.Same(original[^1], model.VisibleMetrics[0]);
            Assert.Same(original[0], model.VisibleMetrics[^1]);
            Assert.Equal(original.Skip(1).SkipLast(1), model.VisibleMetrics.Skip(1).SkipLast(1));
            Assert.Equal(original.Length, model.VisibleMetrics.Distinct().Count());
            original[^1].IsVisible = false;
            Assert.False(model.SwapMetrics(original[0], original[^1]));
            original[^1].IsVisible = true;
            Assert.Same(original[^1], model.VisibleMetrics[0]);
            Assert.False(model.SwapMetrics(original[0], original[0]));
            var order = model.VisibleMetrics.Select(metric => metric.Id).ToArray();
            storage.SaveSettings();
            var reopened = new SettingsService(Path.Combine(directory, "settings.json"));
            reopened.Settings.DownloadOverview.MetricOrder.Insert(0, "RemovedMetric");
            reopened.Settings.DownloadOverview.MetricOrder.Add(order[0]);
            var restored = new DownloadOverviewViewModel(reopened, () => Array.Empty<DownloadPackage>(), () => Array.Empty<DownloadPackage>());
            Assert.Equal(order, restored.VisibleMetrics.Select(metric => metric.Id));
            restored.SelectAllMetricsCommand.Execute(null);
            Assert.Equal(restored.Metrics.Count, restored.VisibleMetrics.Distinct().Count());
            restored.ResetMetricsCommand.Execute(null);
            Assert.Equal(DownloadOverviewSettings.DefaultMetrics, restored.VisibleMetrics.Select(metric => metric.Id));
            reopened.SaveSettings();
        }
        finally
        {
            storage.SaveSettings();
            Directory.Delete(directory, true);
        }
    }
}
