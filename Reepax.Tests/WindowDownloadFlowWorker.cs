using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using Reepax.Helpers;
using System.Windows.Markup;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;
using Reepax.Models;
using Reepax.Services.Browser;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

internal static partial class WindowDownloadFlowWorker
{
    internal static int Run(string root, string language, bool columnsOnly = false, bool clipboardOnly = false,
        bool filtersOnly = false, bool shortcutsOnly = false, string? packageRestartStage = null)
    {
        int result = 1;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await ExecuteAsync(root, language, columnsOnly, clipboardOnly, filtersOnly, shortcutsOnly, packageRestartStage).WaitAsync(TimeSpan.FromSeconds(75)); result = 0; }
                catch (Exception error) { Console.Error.WriteLine(DiagnosticPrivacy.Sanitize(error.ToString())); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static async Task ExecuteAsync(string root, string language, bool columnsOnly, bool clipboardOnly,
        bool filtersOnly, bool shortcutsOnly, string? packageRestartStage)
    {
        root = Path.GetFullPath(root);
        if (!Path.GetFileName(root).StartsWith("WindowFlow_", StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            language is not ("en" or "de")) throw new InvalidOperationException("Unexpected window test configuration.");
        DownloadPersistenceService.IsTestEnvironment = true;
        SettingsService.AppDataDirectoryOverride = Path.Combine(root, "profile");
        AppLogger.LogsDirectory = Path.Combine(root, "logs");
        AppLogger.IsLoggingEnabled = false;
        var settings = SettingsService.Instance.Settings;
        settings.Language = language;
        settings.DefaultDownloadDirectory = Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName;
        settings.AutoPar2Repair = settings.EnableCompletionNotifications = settings.EnableCompletionSound = settings.EnableErrorSound = false;
        settings.EnableAdBlocker = language == "de";
        settings.EnableClipboardMonitor = settings.MinimizeToTrayOnClose = false;
        settings.PostDownloadAction = PostDownloadAction.None;
        if (columnsOnly && language == "de") settings.ThemeId = Reepax.Services.ThemeCatalog.MacOSId;
        settings.EnableChecksumVerification = true;
        settings.MaxAutoRetryOnCorruption = 0;
        // Load production resources and windows without registering file associations or owning the user's app instance.
        var app = new Application();
        var markup = XDocument.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "App.xaml")));
        var resources = new XElement(markup.Descendants().First(element => element.Name.LocalName == "ResourceDictionary"));
        foreach (var element in resources.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName.StartsWith("clr-namespace:") && !element.Name.NamespaceName.Contains(";assembly="))
                element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Reepax");
            if (element.Attribute("Source") is { } sourceAttribute)
                sourceAttribute.Value = "pack://application:,,,/Reepax;component/" + sourceAttribute.Value;
        }
        foreach (var attribute in markup.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:") ? attribute.Value + ";assembly=Reepax" : attribute.Value);
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LocalizationService.Instance.CurrentLanguage = language;
        if (packageRestartStage != null)
        {
            await CheckPackageRestartAsync(root, packageRestartStage);
            return;
        }
        await using var source = new BrowserHandoffSource();
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        var model = (MainViewModel)main.DataContext;
        var queue = QueueManager.Instance;
        CoreWebView2Environment? environment = null;
        try
        {
            main.Show();
            await IdleAsync();
            if (shortcutsOnly)
            {
                await CheckWindowShortcutsAsync(main, model, root);
                Console.WriteLine("Window shortcuts passed");
                return;
            }
            if (filtersOnly)
            {
                environment = await CheckFilterUpdatesAsync(language);
                Console.WriteLine("Browser filter updates passed");
                return;
            }
            if (clipboardOnly)
            {
                CheckClipboardHandoffs(main, app);
                Console.WriteLine("Clipboard handoff passed");
                return;
            }
            if (columnsOnly)
            {
                await CheckColumnMenuAsync(main);
                Console.WriteLine("Column menu placement passed");
                return;
            }
            var power = Assert.IsType<SingleChoiceDropdown>(main.FindName("PostDownloadActionButton"));
            Assert.Equal(38, power.ActualWidth);
            Assert.Equal(34, power.ActualHeight);
            Assert.IsType<System.Windows.Shapes.Path>(power.Content);
            var filter = Assert.IsType<SingleChoiceDropdown>(main.FindName("StatusFilterButton"));
            Assert.Equal(41, filter.ActualHeight);
            Assert.Equal(new Thickness(0), filter.Margin);
            Assert.Equal(new Thickness(0, 0, 1, 0), filter.BorderThickness);
            Assert.Equal(2, Descendants<System.Windows.Shapes.Path>(filter).Count());
            filter.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Assert.Equal(DownloadStatusFilter.All, model.SelectedStatusFilter);
            Invoke(filter);
            await UntilAsync(() => filter.IsDropdownOpen);
            Assert.True(filter.DropdownPopup.AllowsTransparency);
            var paused = Descendants<Button>(filter.DropdownPopup.Child).Single(button => Equals(button.CommandParameter, DownloadStatusFilter.Paused));
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
            paused.RaiseEvent(press);
            Assert.False(press.Handled);
            Assert.True(filter.IsDropdownOpen);
            Invoke(paused);
            await UntilAsync(() => model.SelectedStatusFilter == DownloadStatusFilter.Paused && !filter.IsDropdownOpen);
            Assert.Contains(Descendants<TextBlock>(filter), text => text.Text == model.SelectedStatusFilterText);
            model.SelectedStatusFilter = DownloadStatusFilter.All;
            Invoke((Button)main.FindName("SettingsTabButton"));
            await UntilAsync(() => model.SelectedMainTab == AppMainTab.Settings);
            var add = (Button)main.FindName("AddLinksButton");
            Assert.False(add.IsEnabled);
            var downloads = Descendants<Button>(main).Single(button => ReferenceEquals(button.Command, model.SwitchToDownloadsTabCommand));
            Invoke(downloads);
            await UntilAsync(() => add.IsEnabled);
            var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogWatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            dialogWatcher.Tick += (_, _) =>
            {
                var dialog = app.Windows.OfType<AddLinksDialog>().SingleOrDefault();
                if (dialog == null) return;
                dialogWatcher.Stop();
                try
                {
                    // A pasted link list exercises deduplication and the regular package path.
                    dialog.LinksTextBox.Text = source.Url("/manual") + Environment.NewLine + source.Url("/manual");
                    dialog.PackageNameTextBox.Text = "Window sample";
                    dialog.DownloadPathTextBox.Text = settings.DefaultDownloadDirectory;
                    dialog.AutoExtractCheckBox.IsChecked = true;
                    Invoke(dialog.AddButton);
                    filled.TrySetResult();
                }
                catch (Exception error) { dialog.Close(); filled.TrySetException(error); }
            };
            dialogWatcher.Start();
            try { Invoke(add); await filled.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { dialogWatcher.Stop(); }
            await UntilAsync(() => model.Packages.Count == 1 && !model.IsAnalyzingMetadata);
            var package = Assert.Single(model.Packages);
            Console.WriteLine("Package added");
            Assert.True(package.AutoExtractArchives);
            Invoke(Descendants<Button>(main).Single(button => ReferenceEquals(button.Command, model.StartAllCommand)));
            await UntilAsync(() => app.Windows.OfType<BrowserWindow>().Any(window => window.WebView.CoreWebView2?.Source.EndsWith("/manual") == true));
            var browser = app.Windows.OfType<BrowserWindow>().Single();
            Console.WriteLine("Browser ready");
            if (settings.EnableAdBlocker) Assert.True(Reepax.Services.AdBlock.AdBlockRustEngine.Instance.IsNativeAvailable);
            environment = browser.WebView.CoreWebView2.Environment;
            using (var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while (await browser.WebView.ExecuteScriptAsync("document.readyState === 'complete' && document.querySelector('button') !== null") != "true")
                    await Task.Delay(20, ready.Token);
            // Drive Chromium's input pipeline instead of calling the DOM click handler.
            var point = JsonSerializer.Deserialize<PointPosition>(await browser.WebView.ExecuteScriptAsync("(()=>{const r=document.querySelector('button').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()"))!;
            await browser.WebView.ExecuteScriptAsync("document.querySelector('button').addEventListener('click',()=>window.testClicked=true)");
            await browser.WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = point.x, y = point.y }));
            foreach (string type in new[] { "mousePressed", "mouseReleased" })
                await browser.WebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = point.x, y = point.y, button = "left", clickCount = 1 }));
            await UntilAsync(() => package.IsExtracted && package.HasCompletedNotified && !package.IsExtracting);
            var item = Assert.Single(package.Items);
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.Equal(source.Bytes, File.ReadAllBytes(item.SaveFilePath!));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(source.Bytes)).ToLowerInvariant(), item.CalculatedChecksum);
            Assert.Equal(Enumerable.Range(0, 8192).Select(index => (byte)(index * 31)).ToArray(), File.ReadAllBytes(Path.Combine(package.SaveDirectory, "example.bin")));
            Assert.Equal(Loc.Get("Status_CompletedAndExtracted"), package.StatusMessage);
            Assert.Empty(app.Windows.OfType<BrowserWindow>());
            Assert.Equal(1, source.Requests.Count(request => request.Path == "/file" && request.Browser && request.Method == "GET"));
            Assert.Contains(Descendants<TextBlock>(main), text => text.Text == package.StatusMessage);
            Console.WriteLine("Window download flow passed");
        }
        finally
        {
            Console.WriteLine("Final state: " + string.Join(",", model.Packages.SelectMany(package => package.Items).Select(item => item.Status + ":" + item.StatusMessage)));
            Console.WriteLine("Requests: " + string.Join(",", source.Requests.Select(request => request.Method + " " + request.Path)));
            foreach (var activeBrowser in app.Windows.OfType<BrowserWindow>())
                if (activeBrowser.WebView.CoreWebView2 != null)
                    Console.WriteLine("Click observed: " + await activeBrowser.WebView.ExecuteScriptAsync("window.testClicked===true"));
            queue.StopQueue();
            await DownloadEngine.Instance.PauseAllAsync();
            queue.BrowserHost?.CloseAllWindows();
            foreach (var dialog in app.Windows.OfType<AddLinksDialog>().ToArray()) dialog.Close();
            main.Close();
            if (environment != null)
            {
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Exit(object? sender, CoreWebView2BrowserProcessExitedEventArgs args) => exited.TrySetResult();
                environment.BrowserProcessExited += Exit;
                try { if (environment.GetProcessInfos().Count > 0) await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { environment.BrowserProcessExited -= Exit; }
            }
        }
    }

    private static async Task CheckColumnMenuAsync(MainWindow main)
    {
        var gear = (Button)main.FindName("ColumnConfigButton");
        var header = (Border)main.FindName("ColumnHeadersBorder");
        var menu = header.ContextMenu;
        var gearMenu = gear.ContextMenu;
        void OpenHeaderContextMenu()
        {
            // Exercise WPF's context-menu ownership lifecycle without moving the user's pointer.
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(header), Environment.TickCount, Key.Apps)
                { RoutedEvent = Keyboard.KeyUpEvent, Source = header });
        }
        void CheckGearPlacement()
        {
            var topLeft = gearMenu.PointToScreen(new Point());
            var anchorRight = gear.PointToScreen(new Point(gear.ActualWidth, gear.ActualHeight));
            var menuRight = gearMenu.PointToScreen(new Point(gearMenu.ActualWidth, 0));
            Assert.InRange(Math.Abs(menuRight.X - anchorRight.X), 0, 3);
            Assert.True(topLeft.X >= main.PointToScreen(new Point()).X,
                $"Column menu moved outside the window: {topLeft.X}, expected near {anchorRight.X}.");
        }
        void CheckVerticalPlacement(ContextMenu popup)
        {
            var surface = Descendants<MorphingPopupBorder>(popup).Single();
            Assert.Null(surface.Effect);
            Assert.Equal(new Thickness(0), surface.Margin);
            double bottom = header.PointToScreen(new Point(0, header.ActualHeight)).Y;
            Assert.InRange(Math.Abs(surface.PointToScreen(new Point()).Y - bottom), 0, 1);
        }
        try
        {
            Invoke(gear);
            await UntilAsync(() => gearMenu.IsOpen && gearMenu.ActualWidth > 0);
            CheckGearPlacement();
            CheckVerticalPlacement(gearMenu);
            gearMenu.IsOpen = false;
            await UntilAsync(() => !gearMenu.IsOpen);
            OpenHeaderContextMenu();
            await UntilAsync(() => menu.IsOpen);
            CheckVerticalPlacement(menu);
            void OpenAt(double anchor)
            {
                var request = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, new object[] { header, true, anchor, 5.0 }, null)!;
                header.RaiseEvent(request);
                Assert.True(request.Handled);
            }
            await UntilAsync(() => !Descendants<MorphingPopupBorder>(menu).Single().IsMorphing);
            int reopenCount = 0;
            menu.Opened += (_, _) => reopenCount++;
            foreach (double anchor in new[] { 160.0, 360.0, 220.0 })
            {
                OpenAt(anchor);
                await IdleAsync();
                double expected = header.PointToScreen(new Point(anchor, 0)).X;
                Assert.InRange(Math.Abs(menu.PointToScreen(new Point()).X - expected), 0, 1);
                CheckVerticalPlacement(menu);
                Assert.False(Descendants<MorphingPopupBorder>(menu).Single().IsMorphing);
            }
            Assert.Equal(0, reopenCount);
            foreach (double anchor in new[] { 320.0, 180.0, 380.0 })
            {
                menu.IsOpen = false;
                OpenAt(anchor);
                await UntilAsync(() => menu.IsOpen && !Descendants<MorphingPopupBorder>(menu).Single().IsMorphing);
                Assert.InRange(Math.Abs(menu.PointToScreen(new Point()).X - header.PointToScreen(new Point(anchor, 0)).X), 0, 1);
                CheckVerticalPlacement(menu);
            }
            Invoke(gear);
            await UntilAsync(() => gearMenu.IsOpen);
            Assert.False(menu.IsOpen);
            CheckGearPlacement();
            gearMenu.IsOpen = false;
            await UntilAsync(() => !gearMenu.IsOpen);
            OpenHeaderContextMenu();
            await UntilAsync(() => menu.IsOpen);
            menu.IsOpen = false;
            await UntilAsync(() => !menu.IsOpen);
            Invoke(gear);
            await UntilAsync(() => gearMenu.IsOpen);
            CheckGearPlacement();
            Assert.False(((MenuItem)gearMenu.Items[0]).IsEnabled);
            Assert.False(((MenuItem)gearMenu.Items[7]).IsEnabled);
            var model = (MainViewModel)main.DataContext;
            bool original = model.ShowColHoster;
            var toggle = (IToggleProvider)new MenuItemAutomationPeer((MenuItem)gearMenu.Items[1])
                .GetPattern(PatternInterface.Toggle);
            toggle.Toggle();
            await UntilAsync(() => model.ShowColHoster != original);
            Assert.True(gearMenu.IsOpen);
            gearMenu.IsOpen = false;
            await UntilAsync(() => !gearMenu.IsOpen);
            OpenHeaderContextMenu();
            await UntilAsync(() => menu.IsOpen);
            Assert.Equal(!original, ((MenuItem)menu.Items[1]).IsChecked);
            Assert.False(((MenuItem)menu.Items[0]).IsEnabled);
            Assert.False(((MenuItem)menu.Items[7]).IsEnabled);
            ((IToggleProvider)new MenuItemAutomationPeer((MenuItem)menu.Items[1])
                .GetPattern(PatternInterface.Toggle)).Toggle();
            await UntilAsync(() => model.ShowColHoster == original);
            Assert.True(menu.IsOpen);
        }
        finally { menu.IsOpen = gearMenu.IsOpen = false; }
    }

    private static void CheckClipboardHandoffs(MainWindow main, Application app)
    {
        var model = (MainViewModel)main.DataContext;
        bool previousTestEnvironment = Reepax.Services.SystemIntegration.ClipboardMonitorService.IsTestEnvironment;
        Reepax.Services.SystemIntegration.ClipboardMonitorService.IsTestEnvironment = true;
        using var monitor = (Reepax.Services.SystemIntegration.ClipboardMonitorService)Activator.CreateInstance(
            typeof(Reepax.Services.SystemIntegration.ClipboardMonitorService), nonPublic: true)!;
        var handler = typeof(MainWindow).GetMethod("OnClipboardLinksDetected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        int deliveries = 0;
        bool Deliver(List<string> urls)
        {
            deliveries++;
            return (bool?)handler.Invoke(main, new object[] { urls }) == true;
        }
        try
        {
            monitor.Start(new IntPtr(1), urls => Deliver(urls));
            for (int state = 0; state < 4; state++)
            {
                model.EnableClipboardMonitor = state != 3;
                model.SelectedMainTab = state == 0 ? AppMainTab.Settings : AppMainTab.Downloads;
                model.IsBusy = state == 1;
                model.IsRemovalInProgress = state == 2;
                string first = $"https://files.example.invalid/sample-{state}.zip";
                string second = $"https://files.example.invalid/extra-{state}.zip";
                var urls = Reepax.Services.SystemIntegration.ClipboardMonitorService.ExtractLinksFromData(null, first);
                Assert.Equal(new[] { first }, urls);
                int before = deliveries;
                monitor.DispatchDetectedLinks(urls);
                Assert.Equal(before + 1, deliveries);
                Assert.Empty(app.Windows.OfType<AddLinksDialog>());
                model.SelectedMainTab = AppMainTab.Downloads;
                model.IsBusy = model.IsRemovalInProgress = false;
                model.EnableClipboardMonitor = true;
                AddLinksDialog? observed = null;
                Exception? failure = null;
                var watcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                watcher.Tick += (_, _) =>
                {
                    var dialog = app.Windows.OfType<AddLinksDialog>().SingleOrDefault();
                    if (dialog?.IsVisible != true) return;
                    watcher.Stop();
                    observed = dialog;
                    try
                    {
                        Assert.Same(main, dialog.Owner);
                        Assert.Equal(first, dialog.LinksTextBox.Text);
                        monitor.DispatchDetectedLinks(new() { first });
                        Assert.Equal(before + 2, deliveries);
                        monitor.DispatchDetectedLinks(new() { second });
                        monitor.DispatchDetectedLinks(new() { second });
                        Assert.Equal(before + 3, deliveries);
                        Assert.Equal(first + Environment.NewLine + second, dialog.LinksTextBox.Text);
                        Assert.Single(app.Windows.OfType<AddLinksDialog>());
                    }
                    catch (Exception error) { failure = error; }
                    finally { dialog.Close(); }
                };
                watcher.Start();
                try { monitor.DispatchDetectedLinks(new(urls)); }
                finally { watcher.Stop(); }
                if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.NotNull(observed);
                Assert.Empty(app.Windows.OfType<AddLinksDialog>());
                monitor.DispatchDetectedLinks(new() { second });
                Assert.Equal(before + 3, deliveries);
                Assert.Empty(model.Packages);
            }
        }
        finally
        {
            model.EnableClipboardMonitor = false;
            Reepax.Services.SystemIntegration.ClipboardMonitorService.IsTestEnvironment = previousTestEnvironment;
        }
    }

    private sealed record PointPosition(double x, double y);
    private static void Invoke(Button button)
    {
        Assert.True(button.IsEnabled);
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) { await Task.Delay(20, limit.Token); }
        await IdleAsync();
    }
    private static async Task IdleAsync() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
