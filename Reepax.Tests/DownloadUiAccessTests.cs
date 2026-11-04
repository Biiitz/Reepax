using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Reepax.Models;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Fact]
    public void AddLinksCommand_RejectsSettingsAndBusyStatesAndRecoversInDownloads()
    {
        var model = new MainViewModel();
        int requests = 0, changes = 0;
        model.RequestShowAddLinksDialog += (_, _) => requests++;
        model.OpenAddLinksCommand.CanExecuteChanged += (_, _) => changes++;
        foreach (var state in new[]
        {
            (Tab: AppMainTab.Settings, Busy: false, Removing: false),
            (Tab: AppMainTab.Downloads, Busy: true, Removing: false),
            (Tab: AppMainTab.Downloads, Busy: false, Removing: true)
        })
        {
            model.SelectedMainTab = state.Tab;
            model.IsBusy = state.Busy;
            model.IsRemovalInProgress = state.Removing;
            Assert.False(model.CanOpenAddLinks);
            Assert.False(model.OpenAddLinksCommand.CanExecute(null));
            model.OpenAddLinksCommand.Execute(null);
            model.OpenAddLinks();
            Assert.Equal(0, requests);
        }
        model.SelectedMainTab = AppMainTab.Downloads;
        model.IsBusy = model.IsRemovalInProgress = false;
        Assert.True(model.OpenAddLinksCommand.CanExecute(null));
        model.OpenAddLinksCommand.Execute(null);
        Assert.Equal(1, requests);
        Assert.True(changes >= 3);
    }

    [Fact]
    public void IdentityColumns_NormalizeOldLayoutsAndCannotBeHidden()
    {
        var settings = SettingsService.Instance.Settings;
        bool previous = settings.ShowColStatus;
        bool previousName = settings.ShowColName;
        try
        {
            settings.ShowColStatus = false;
            settings.ShowColName = false;
            var model = new MainViewModel();
            Assert.True(model.ShowColStatus);
            Assert.True(settings.ShowColStatus);
            Assert.True(model.ShowColName);
            Assert.True(settings.ShowColName);
            model.ShowColName = false;
            model.ColWidthName = 251;
            Assert.True(model.ShowColName);
            Assert.Equal(251, model.ActualColWidthName);
            Assert.Equal(251, model.GridColWidthName.Value);
            Assert.True(model.ShowDividerName);
            model.ShowColStatus = false;
            model.ColWidthStatus = 201;
            Assert.True(model.ShowColStatus);
            Assert.Equal(201, model.ActualColWidthStatus);
            Assert.Equal(201, model.GridColWidthStatus.Value);
            Assert.True(model.ShowDividerStatus);
            Assert.True(model.IsColumnVisible("Status"));
        }
        finally { settings.ShowColStatus = previous; settings.ShowColName = previousName; }
    }

    [Fact]
    public void ErrorStatusCell_ExposesSanitizedDetailsAndClearsThemWhenReused()
    {
        RunInSta(() =>
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri("/Reepax;component/Themes/DownloadStatus.xaml", UriKind.Relative)
            };
            var style = (Style)dictionary["DownloadStatusCellStyle"];
            foreach (object model in new object[]
            {
                new DownloadItem { Status = DownloadStatus.Failed, StatusMessage = "HTTP error https://example.invalid/private-token?key=sample-secret" },
                new DownloadPackage { Status = DownloadStatus.Failed, StatusMessage = "HTTP error https://example.invalid/private-token?key=sample-secret" }
            })
            {
                var cell = new Border { Style = style, DataContext = model };
                using var source = new HwndSource(new HwndSourceParameters("Status details test")
                { Width = 200, Height = 30, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
                source.RootVisual = cell;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                cell.Measure(new Size(200, 30));
                Assert.True(cell.Focusable);
                Assert.True(ToolTipService.GetShowsToolTipOnKeyboardFocus(cell));
                string help = System.Windows.Automation.AutomationProperties.GetHelpText(cell);
                Assert.Contains("HTTP error", help);
                Assert.DoesNotContain("private-token", help);
                Assert.DoesNotContain("sample-secret", help);
                Assert.IsType<ToolTip>(cell.ToolTip);
                Assert.Equal(2, style.Triggers.OfType<MultiDataTrigger>().Count());
                cell.DataContext = new DownloadItem { Status = DownloadStatus.Completed, StatusMessage = "Completed" };
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                cell.Measure(new Size(200, 30));
                Assert.False(cell.Focusable);
                Assert.Null(cell.ToolTip);
                Assert.True(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(cell)));
                source.RootVisual = null;
            }
        });
    }
}
