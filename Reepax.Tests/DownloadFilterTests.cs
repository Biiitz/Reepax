using System;
using System.ComponentModel;
using System.Linq;
using Reepax.Models;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

[Collection("SharedQueue")]
public class DownloadFilterTests : IDisposable
{
    private readonly MainViewModel _viewModel;

    public DownloadFilterTests()
    {
        _viewModel = new MainViewModel();
        _viewModel.Packages.Clear();
        _viewModel.SearchFilterText = string.Empty;
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.All;
        _viewModel.ClearColumnSort();
    }

    public void Dispose()
    {
        _viewModel.Packages.Clear();
        _viewModel.SearchFilterText = string.Empty;
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.All;
        _viewModel.ClearColumnSort();
    }

    [Fact]
    public void SearchFilter_ByPackageName_FiltersCorrectly()
    {
        // Arrange
        var pkg1 = new DownloadPackage { Name = "Cyberpunk 2077 Update" };
        var pkg2 = new DownloadPackage { Name = "Witcher 3 Complete" };
        var pkg3 = new DownloadPackage { Name = "Elden Ring DLC" };

        _viewModel.Packages.Add(pkg1);
        _viewModel.Packages.Add(pkg2);
        _viewModel.Packages.Add(pkg3);
        _viewModel.RefreshRootPackages();

        Assert.Equal(3, _viewModel.RootPackages.Count);

        // Act - case-insensitive substring
        _viewModel.SearchFilterText = "cyber";

        // Assert
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkg1.Id, _viewModel.RootPackages[0].Id);

        // Act - match different package
        _viewModel.SearchFilterText = "RING";

        // Assert
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkg3.Id, _viewModel.RootPackages[0].Id);
    }

    [Fact]
    public void SearchFilter_ByItemFileName_MatchesParentPackage()
    {
        // Arrange
        var pkg1 = new DownloadPackage { Name = "Linux Distro Collection" };
        pkg1.Items.Add(new DownloadItem { FileName = "ubuntu-24.04-desktop-amd64.iso" });

        var pkg2 = new DownloadPackage { Name = "Software Tools" };
        pkg2.Items.Add(new DownloadItem { FileName = "archlinux-latest-x86_64.iso" });

        _viewModel.Packages.Add(pkg1);
        _viewModel.Packages.Add(pkg2);
        _viewModel.RefreshRootPackages();

        // Act
        _viewModel.SearchFilterText = "ubuntu";

        // Assert
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkg1.Id, _viewModel.RootPackages[0].Id);
    }

    [Fact]
    public void SearchFilter_ByItemHoster_MatchesParentPackage()
    {
        // Arrange
        var pkg1 = new DownloadPackage { Name = "Package A" };
        pkg1.Items.Add(new DownloadItem { FileName = "data1.bin", HosterName = "Rapidgator" });

        var pkg2 = new DownloadPackage { Name = "Package B" };
        pkg2.Items.Add(new DownloadItem { FileName = "data2.bin", HosterName = "1Fichier" });

        _viewModel.Packages.Add(pkg1);
        _viewModel.Packages.Add(pkg2);
        _viewModel.RefreshRootPackages();

        // Act
        _viewModel.SearchFilterText = "rapidgator";

        // Assert
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkg1.Id, _viewModel.RootPackages[0].Id);

        // Act
        _viewModel.SearchFilterText = "fichier";

        // Assert
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkg2.Id, _viewModel.RootPackages[0].Id);
    }

    [Fact]
    public void SearchFilter_ByItemUrl_MatchesParentPackage()
    {
        // Arrange
        var pkg1 = new DownloadPackage { Name = "Archive One" };
        pkg1.Items.Add(new DownloadItem { FileName = "part1.rar", OriginalUrl = "https://example.com/download/archive_001.rar" });

        var pkg2 = new DownloadPackage { Name = "Archive Two" };
        pkg2.Items.Add(new DownloadItem { FileName = "part2.rar", OriginalUrl = "https://mirror.net/files/different.zip" });

        _viewModel.Packages.Add(pkg1);
        _viewModel.Packages.Add(pkg2);
        _viewModel.RefreshRootPackages();

        // Act
        _viewModel.SearchFilterText = "example.com";

        // Assert
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkg1.Id, _viewModel.RootPackages[0].Id);
    }

    [Fact]
    public void StatusFilter_All_ReturnsAllRootPackages()
    {
        // Arrange
        var pkg1 = new DownloadPackage { Name = "Running Package", Status = DownloadStatus.Downloading };
        var pkg2 = new DownloadPackage { Name = "Paused Package", Status = DownloadStatus.Paused };
        var pkg3 = new DownloadPackage { Name = "Completed Package", Status = DownloadStatus.Completed };
        var pkg4 = new DownloadPackage { Name = "Failed Package", Status = DownloadStatus.Failed };

        _viewModel.Packages.Add(pkg1);
        _viewModel.Packages.Add(pkg2);
        _viewModel.Packages.Add(pkg3);
        _viewModel.Packages.Add(pkg4);
        _viewModel.RefreshRootPackages();

        // Act
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.All;

        // Assert
        Assert.Equal(4, _viewModel.RootPackages.Count);
    }

    [Fact]
    public void StatusFilter_Running_MatchesDownloadingAndExtractingAndRepairing()
    {
        // Arrange
        var pkgDownloading = new DownloadPackage { Name = "Pkg 1", Status = DownloadStatus.Downloading };
        var pkgExtracting = new DownloadPackage { Name = "Pkg 2", StatusMessage = "Extracting..." };
        var pkgRepairing = new DownloadPackage { Name = "Pkg 3", StatusMessage = "PAR2: Repairing (45%)" };
        var pkgVerifying = new DownloadPackage { Name = "Pkg 4", StatusMessage = "Verifying checksum" };
        var pkgItemDownloading = new DownloadPackage { Name = "Pkg 5" };
        pkgItemDownloading.Items.Add(new DownloadItem { Status = DownloadStatus.Downloading });
        var pkgPaused = new DownloadPackage { Name = "Pkg 6", Status = DownloadStatus.Paused };

        _viewModel.Packages.Add(pkgDownloading);
        _viewModel.Packages.Add(pkgExtracting);
        _viewModel.Packages.Add(pkgRepairing);
        _viewModel.Packages.Add(pkgVerifying);
        _viewModel.Packages.Add(pkgItemDownloading);
        _viewModel.Packages.Add(pkgPaused);
        _viewModel.RefreshRootPackages();

        // Act
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.Running;

        // Assert
        Assert.Equal(5, _viewModel.RootPackages.Count);
        Assert.DoesNotContain(pkgPaused, _viewModel.RootPackages);
        Assert.Contains(pkgDownloading, _viewModel.RootPackages);
        Assert.Contains(pkgExtracting, _viewModel.RootPackages);
        Assert.Contains(pkgRepairing, _viewModel.RootPackages);
        Assert.Contains(pkgVerifying, _viewModel.RootPackages);
        Assert.Contains(pkgItemDownloading, _viewModel.RootPackages);
    }

    [Fact]
    public void StatusFilter_Paused_MatchesPausedAndStopped()
    {
        // Arrange
        var pkgPaused = new DownloadPackage { Name = "Pkg 1", Status = DownloadStatus.Paused };
        var pkgStopped = new DownloadPackage { Name = "Pkg 2", StatusMessage = "Stopped" };
        var pkgItemPaused = new DownloadPackage { Name = "Pkg 3" };
        pkgItemPaused.Items.Add(new DownloadItem { Status = DownloadStatus.Paused });
        var pkgDownloading = new DownloadPackage { Name = "Pkg 4", Status = DownloadStatus.Downloading };

        _viewModel.Packages.Add(pkgPaused);
        _viewModel.Packages.Add(pkgStopped);
        _viewModel.Packages.Add(pkgItemPaused);
        _viewModel.Packages.Add(pkgDownloading);
        _viewModel.RefreshRootPackages();

        // Act
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.Paused;

        // Assert
        Assert.Equal(3, _viewModel.RootPackages.Count);
        Assert.DoesNotContain(pkgDownloading, _viewModel.RootPackages);
        Assert.Contains(pkgPaused, _viewModel.RootPackages);
        Assert.Contains(pkgStopped, _viewModel.RootPackages);
        Assert.Contains(pkgItemPaused, _viewModel.RootPackages);
    }

    [Fact]
    public void StatusFilter_Completed_MatchesCompletedAndExtracted()
    {
        // Arrange
        var pkgCompleted = new DownloadPackage { Name = "Pkg 1", Status = DownloadStatus.Completed };
        var pkgExtracted = new DownloadPackage { Name = "Pkg 2", StatusMessage = "Completed & Extracted" };
        var pkgRunning = new DownloadPackage { Name = "Pkg 3", Status = DownloadStatus.Downloading };
        var pkgFailed = new DownloadPackage { Name = "Pkg 4", Status = DownloadStatus.Failed };

        _viewModel.Packages.Add(pkgCompleted);
        _viewModel.Packages.Add(pkgExtracted);
        _viewModel.Packages.Add(pkgRunning);
        _viewModel.Packages.Add(pkgFailed);
        _viewModel.RefreshRootPackages();

        // Act
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.Completed;

        // Assert
        Assert.Equal(2, _viewModel.RootPackages.Count);
        Assert.Contains(pkgCompleted, _viewModel.RootPackages);
        Assert.Contains(pkgExtracted, _viewModel.RootPackages);
        Assert.DoesNotContain(pkgRunning, _viewModel.RootPackages);
        Assert.DoesNotContain(pkgFailed, _viewModel.RootPackages);
    }

    [Fact]
    public void StatusFilter_Failed_MatchesFailedAndErrorAndExtractionFailed()
    {
        // Arrange
        var pkgFailed = new DownloadPackage { Name = "Pkg 1", Status = DownloadStatus.Failed };
        var pkgErrorMsg = new DownloadPackage { Name = "Pkg 2", StatusMessage = "Error occurred" };
        var pkgExtractFailed = new DownloadPackage { Name = "Pkg 3", StatusMessage = "ExtractionFailed: CRC mismatch" };
        var pkgItemFailed = new DownloadPackage { Name = "Pkg 4" };
        pkgItemFailed.Items.Add(new DownloadItem { Status = DownloadStatus.Failed });
        var pkgCompleted = new DownloadPackage { Name = "Pkg 5", Status = DownloadStatus.Completed };

        _viewModel.Packages.Add(pkgFailed);
        _viewModel.Packages.Add(pkgErrorMsg);
        _viewModel.Packages.Add(pkgExtractFailed);
        _viewModel.Packages.Add(pkgItemFailed);
        _viewModel.Packages.Add(pkgCompleted);
        _viewModel.RefreshRootPackages();

        // Act
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.Failed;

        // Assert
        Assert.Equal(4, _viewModel.RootPackages.Count);
        Assert.DoesNotContain(pkgCompleted, _viewModel.RootPackages);
        Assert.Contains(pkgFailed, _viewModel.RootPackages);
        Assert.Contains(pkgErrorMsg, _viewModel.RootPackages);
        Assert.Contains(pkgExtractFailed, _viewModel.RootPackages);
        Assert.Contains(pkgItemFailed, _viewModel.RootPackages);
    }

    [Fact]
    public void ClearSearchFilterCommand_ResetsSearchTextAndRestoresAllPackages()
    {
        // Arrange
        var pkg1 = new DownloadPackage { Name = "Game Alpha" };
        var pkg2 = new DownloadPackage { Name = "Game Beta" };
        _viewModel.Packages.Add(pkg1);
        _viewModel.Packages.Add(pkg2);
        _viewModel.RefreshRootPackages();

        _viewModel.SearchFilterText = "Alpha";
        Assert.Single(_viewModel.RootPackages);

        // Act
        _viewModel.ClearSearchFilterCommand.Execute(null);

        // Assert
        Assert.Equal(string.Empty, _viewModel.SearchFilterText);
        Assert.False(_viewModel.HasSearchFilterText);
        Assert.Equal(2, _viewModel.RootPackages.Count);
    }

    [Fact]
    public void SetStatusFilterCommand_UpdatesSelectedStatusFilter_AndFiltersList()
    {
        // Arrange
        var pkgRunning = new DownloadPackage { Name = "Running", Status = DownloadStatus.Downloading };
        var pkgPaused = new DownloadPackage { Name = "Paused", Status = DownloadStatus.Paused };
        _viewModel.Packages.Add(pkgRunning);
        _viewModel.Packages.Add(pkgPaused);
        _viewModel.RefreshRootPackages();

        // Act - with Enum parameter
        _viewModel.SetStatusFilterCommand.Execute(DownloadStatusFilter.Running);

        // Assert
        Assert.Equal(DownloadStatusFilter.Running, _viewModel.SelectedStatusFilter);
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkgRunning.Id, _viewModel.RootPackages[0].Id);

        // Act - with String parameter
        _viewModel.SetStatusFilterCommand.Execute("Paused");

        // Assert
        Assert.Equal(DownloadStatusFilter.Paused, _viewModel.SelectedStatusFilter);
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkgPaused.Id, _viewModel.RootPackages[0].Id);
    }

    [Fact]
    public void CombinedFilter_SearchAndStatus_MustSatisfyBothConditions()
    {
        // Arrange
        var pkg1 = new DownloadPackage { Name = "Adventure Quest", Status = DownloadStatus.Downloading };
        var pkg2 = new DownloadPackage { Name = "Adventure Quest DLC", Status = DownloadStatus.Completed };
        var pkg3 = new DownloadPackage { Name = "Space Shooter", Status = DownloadStatus.Downloading };
        var pkg4 = new DownloadPackage { Name = "Space Shooter", Status = DownloadStatus.Paused };

        _viewModel.Packages.Add(pkg1);
        _viewModel.Packages.Add(pkg2);
        _viewModel.Packages.Add(pkg3);
        _viewModel.Packages.Add(pkg4);
        _viewModel.RefreshRootPackages();

        // Act: Search for "Adventure" + Status "Downloading"
        _viewModel.SearchFilterText = "Adventure";
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.Running;

        // Assert: Only pkg1 matches both
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkg1.Id, _viewModel.RootPackages[0].Id);

        // Act: Change Status to "Completed"
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.Completed;

        // Assert: Only pkg2 matches both
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkg2.Id, _viewModel.RootPackages[0].Id);

        // Act: Clear search, keep status "Completed"
        _viewModel.ClearSearchFilterCommand.Execute(null);

        // Assert: Only pkg2 is completed in the queue
        Assert.Single(_viewModel.RootPackages);
        Assert.Equal(pkg2.Id, _viewModel.RootPackages[0].Id);

        // Act: Reset status to All
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.All;

        // Assert: All 4 restored
        Assert.Equal(4, _viewModel.RootPackages.Count);
    }

    [Fact]
    public void Sorting_WithActiveFilter_MaintainsFilteredSubsetAndCorrectSortOrder()
    {
        // Arrange
        var pkgB = new DownloadPackage { Name = "Beta Update", Status = DownloadStatus.Downloading };
        var pkgA = new DownloadPackage { Name = "Alpha Update", Status = DownloadStatus.Downloading };
        var pkgC = new DownloadPackage { Name = "Gamma Update", Status = DownloadStatus.Paused };

        _viewModel.Packages.Add(pkgB);
        _viewModel.Packages.Add(pkgA);
        _viewModel.Packages.Add(pkgC);
        _viewModel.RefreshRootPackages();

        // Act - Filter to running
        _viewModel.SelectedStatusFilter = DownloadStatusFilter.Running;
        Assert.Equal(2, _viewModel.RootPackages.Count);

        // Sort by Name Ascending
        _viewModel.ToggleColumnSort("Name");
        Assert.Equal(ListSortDirection.Ascending, _viewModel.SortDirection);

        // Assert
        Assert.Equal(2, _viewModel.RootPackages.Count);
        Assert.Equal("Alpha Update", _viewModel.RootPackages[0].Name);
        Assert.Equal("Beta Update", _viewModel.RootPackages[1].Name);

        // Sort by Name Descending
        _viewModel.ToggleColumnSort("Name");
        Assert.Equal(ListSortDirection.Descending, _viewModel.SortDirection);

        // Assert
        Assert.Equal(2, _viewModel.RootPackages.Count);
        Assert.Equal("Beta Update", _viewModel.RootPackages[0].Name);
        Assert.Equal("Alpha Update", _viewModel.RootPackages[1].Name);
    }
}
