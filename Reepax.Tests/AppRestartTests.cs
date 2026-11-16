using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Shortcuts;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

[Collection("SharedQueue")]
public class AppRestartTests
{
    [Theory]
    [InlineData("Reepax.exe", false)]
    [InlineData("dotnet.exe", true)]
    public void RestartStartInfo_PreservesStartupOptionsAndDropsOneTimeRequests(string executable, bool hosted)
    {
        string entryPoint = hosted ? "Reepax.dll" : executable;
        var start = AppRestartService.CreateStartInfo(executable,
            [entryPoint, "example package.repx", "legacy.sdlr", "--RESTART", "--activate",
                "--update-session=example", "--legacy-update-bootstrap", "--MINIMIZED", "--autostart"]);
        using var current = Process.GetCurrentProcess();
        string[] startupOptions = ["--MINIMIZED", "--autostart", "--restart", AppRestartService.CreateRestartParentArgument(current)];
        Assert.Equal(executable, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(AppContext.BaseDirectory, start.WorkingDirectory);
        Assert.Equal(hosted ? [entryPoint, ..startupOptions] : startupOptions, start.ArgumentList);
    }

    [Fact]
    public async Task RestartWait_FollowsTheOriginalProcessBeyondTheMutexRetryWindow()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/q");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("set /p finish=");
        using var original = Process.Start(start)!;
        string argument = AppRestartService.CreateRestartParentArgument(original);
        var waiting = Task.Factory.StartNew(() => AppRestartService.WaitForRestartParent(["--restart", argument]),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            string stale = argument[..(argument.IndexOf(':') + 1)] + "1";
            await Task.Run(() => AppRestartService.WaitForRestartParent(["--restart", stale])).WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<TimeoutException>(() => waiting.WaitAsync(TimeSpan.FromMilliseconds(4200)));
            Assert.False(original.HasExited);
            await original.StandardInput.WriteLineAsync("finish");
            await original.StandardInput.FlushAsync();
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(original.HasExited);
            AppRestartService.WaitForRestartParent(["--restart", argument]);
        }
        finally
        {
            if (!original.HasExited) original.Kill();
            await original.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restart_WhenProcessStartFails_PreservesTheCurrentInstanceAndAllowsRetry(bool noProcess)
    {
        var steps = new List<string>();
        int attempts = 0;
        var queue = QueueManager.Instance;
        var package = new DownloadPackage { Name = "Restart test" };
        var active = new DownloadItem { FileName = "active.bin", Status = DownloadStatus.Downloading };
        var queued = new DownloadItem { FileName = "queued.bin", Status = DownloadStatus.Queued };
        var browser = new DownloadItem { FileName = "browser.bin", Status = DownloadStatus.InBrowser };
        foreach (var item in new[] { active, queued, browser }) package.Items.Add(item);
        var restart = new AppRestartService(start =>
        {
            Assert.Contains("--restart", start.ArgumentList);
            steps.Add("start");
            if (++attempts != 1) return new Process();
            if (noProcess) return null;
            throw new Win32Exception(5);
        }, () => steps.Add("exit"));

        queue.Packages.Add(package);
        try
        {
            Assert.False(restart.TryRestart());
            Assert.Equal(new[] { "start" }, steps);
            Assert.Equal(DownloadStatus.Downloading, active.Status);
            Assert.Equal(DownloadStatus.Queued, queued.Status);
            Assert.Equal(DownloadStatus.InBrowser, browser.Status);
            Assert.True(restart.TryRestart());
            Assert.True(restart.TryRestart());
            Assert.Equal(new[] { "start", "start", "exit" }, steps);
            Assert.All(package.Items, item => Assert.Equal(DownloadStatus.Paused, item.Status));
        }
        finally { queue.Packages.Remove(package); }
    }

    [Fact]
    public void AppRestartService_Restart_PausesAllActiveAndQueuedDownloads()
    {
        bool restartHookCalled = false;
        var restart = new AppRestartService(_ => new Process(), () => restartHookCalled = true);
        var qm = QueueManager.Instance;
        var previousPackages = qm.Packages.ToArray();

        try
        {
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
            Assert.True(restart.TryRestart());

            // Assert
            Assert.True(restartHookCalled);
            Assert.False(qm.IsRunning);
            Assert.Equal(DownloadStatus.Paused, activeItem.Status);
            Assert.Equal(DownloadStatus.Paused, queuedItem.Status);
            Assert.Equal(DownloadStatus.Paused, browserItem.Status);
        }
        finally
        {
            qm.Packages.Clear();
            foreach (var package in previousPackages) qm.Packages.Add(package);
        }
    }

    [Theory]
    [InlineData("en", "Reepax could not be restarted.")]
    [InlineData("de", "Reepax konnte nicht neu gestartet werden.")]
    public void MainViewModel_RestartApplicationCommand_ReportsTheLaunchFailure(string language, string expected)
    {
        var localization = LocalizationService.Instance;
        string previousLanguage = localization.CurrentLanguage;
        try
        {
            var vm = new MainViewModel
            {
                RestartService = new AppRestartService(_ => throw new Win32Exception(5),
                    () => Assert.Fail("A failed start must not shut down the application."))
            };
            localization.CurrentLanguage = language;
            Assert.NotNull(vm.RestartApplicationCommand);
            Assert.True(vm.RestartApplicationCommand.CanExecute(null));

            vm.RestartApplicationCommand.Execute(null);

            Assert.StartsWith(expected, vm.StatusSummary, StringComparison.Ordinal);
        }
        finally
        {
            localization.CurrentLanguage = previousLanguage;
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
                var theme = File.ReadAllText(themePath).Replace("clr-namespace:Reepax.Helpers", "clr-namespace:Reepax.Helpers;assembly=Reepax");
                dict = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(theme);
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
