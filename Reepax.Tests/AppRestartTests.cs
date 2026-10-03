using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Shortcuts;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

[Collection("SharedQueue")]
public class AppRestartTests
{
    [Fact]
    public void AppRestartService_Restart_PausesAllActiveAndQueuedDownloads()
    {
        // Setup test environment
        bool restartHookCalled = false;
        AppRestartService.RestartActionOverride = () =>
        {
            restartHookCalled = true;
        };

        try
        {
            var qm = QueueManager.Instance;
            qm.Packages.Clear();

            var pkg = new DownloadPackage { Name = "Restart Test Package" };
            var activeItem = new DownloadItem
            {
                FileName = "active_download.zip",
                Status = DownloadStatus.Downloading,
                DownloadedBytes = 500,
                TotalBytes = 1000
            };
            var queuedItem = new DownloadItem
            {
                FileName = "queued_download.zip",
                Status = DownloadStatus.Queued,
                DownloadedBytes = 0,
                TotalBytes = 2000
            };
            var browserItem = new DownloadItem
            {
                FileName = "browser_download.zip",
                Status = DownloadStatus.InBrowser,
                DownloadedBytes = 0,
                TotalBytes = 3000
            };

            pkg.Items.Add(activeItem);
            pkg.Items.Add(queuedItem);
            pkg.Items.Add(browserItem);
            qm.Packages.Add(pkg);

            // Trigger restart
            AppRestartService.Restart();

            // Assert
            Assert.True(restartHookCalled);
            Assert.False(qm.IsRunning);
            Assert.Equal(DownloadStatus.Paused, activeItem.Status);
            Assert.Equal(DownloadStatus.Paused, queuedItem.Status);
            Assert.Equal(DownloadStatus.Paused, browserItem.Status);
        }
        finally
        {
            AppRestartService.RestartActionOverride = null;
        }
    }

    [Fact]
    public void MainViewModel_RestartApplicationCommand_InvokesAppRestart()
    {
        bool restartHookCalled = false;
        AppRestartService.RestartActionOverride = () =>
        {
            restartHookCalled = true;
        };

        try
        {
            var vm = new MainViewModel();
            Assert.NotNull(vm.RestartApplicationCommand);
            Assert.True(vm.RestartApplicationCommand.CanExecute(null));

            vm.RestartApplicationCommand.Execute(null);

            Assert.True(restartHookCalled);
        }
        finally
        {
            AppRestartService.RestartActionOverride = null;
        }
    }

    [Fact]
    public void KeyboardShortcutManager_RestartApp_CanBeReboundAndExported()
    {
        var manager = new KeyboardShortcutManager();
        var shortcut = manager.Shortcuts.FirstOrDefault(s => s.Id == "RestartApp");
        Assert.NotNull(shortcut);

        // Customize to Ctrl + Alt + R
        shortcut.Key = Key.R;
        shortcut.Modifiers = ModifierKeys.Control | ModifierKeys.Alt;
        Assert.True(shortcut.IsCustomized);

        var exported = manager.ExportCustomShortcuts();
        Assert.True(exported.ContainsKey("RestartApp"));
        Assert.Equal("Control+Alt+R", exported["RestartApp"]);

        // Reset
        shortcut.ResetToDefault();
        Assert.False(shortcut.IsCustomized);
        Assert.Equal(Key.R, shortcut.Key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, shortcut.Modifiers);
    }

    [Fact]
    public void ToolTip_WithLongString_WrapsCorrectly()
    {
        var thread = new Thread(() =>
        {

            var themePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Reepax\Themes\DarkTheme.xaml"));
            System.Windows.ResourceDictionary dict;
            if (File.Exists(themePath))
            {
                using var stream = File.OpenRead(themePath);
                dict = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
            }
            else
            {
                var xaml = @"
                    <ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                                        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                                        xmlns:sys=""clr-namespace:System;assembly=System.Runtime"">
                        <Style TargetType=""ToolTip"">
                            <Setter Property=""MaxWidth"" Value=""420"" />
                            <Setter Property=""Template"">
                                <Setter.Value>
                                    <ControlTemplate TargetType=""ToolTip"">
                                        <Border Background=""#222"" Padding=""10,6"">
                                            <ContentPresenter>
                                                <ContentPresenter.Resources>
                                                    <DataTemplate DataType=""{x:Type sys:String}"">
                                                        <TextBlock Text=""{Binding}"" TextWrapping=""Wrap"" />
                                                    </DataTemplate>
                                                </ContentPresenter.Resources>
                                            </ContentPresenter>
                                        </Border>
                                    </ControlTemplate>
                                </Setter.Value>
                            </Setter>
                        </Style>
                    </ResourceDictionary>";
                dict = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
            }

            var style = (System.Windows.Style)dict[typeof(System.Windows.Controls.ToolTip)];

            var tt = new System.Windows.Controls.ToolTip();
            tt.Style = style;
            tt.Content = "Automatisch den PC herunterfahren, in Ruhezustand versetzen oder die App beenden, sobald alle aktiven Downloads und Entpackvorgänge abgeschlossen sind.";
            tt.ApplyTemplate();
            tt.Measure(new System.Windows.Size(420, double.PositiveInfinity));

            // Test string content wrapping: single line is <= 25px, multi-line wrapped text is > 30px
            Assert.True(tt.DesiredSize.Height > 30, $"Height was {tt.DesiredSize.Height}, expected > 30 for wrapped multi-line text");

            // Test UIElement content
            var tt2 = new System.Windows.Controls.ToolTip();
            tt2.Style = style;
            var button = new System.Windows.Controls.Button { Content = "Test Button" };
            tt2.Content = button;
            tt2.ApplyTemplate();
            tt2.Measure(new System.Windows.Size(420, double.PositiveInfinity));

            var border = (System.Windows.Controls.Border)System.Windows.Media.VisualTreeHelper.GetChild(tt2, 0);
            var cp = (System.Windows.Controls.ContentPresenter)border.Child;
            var visualChild = System.Windows.Media.VisualTreeHelper.GetChild(cp, 0);
            Assert.IsType<System.Windows.Controls.Button>(visualChild);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
