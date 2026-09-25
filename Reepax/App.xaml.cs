using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Reepax.Converters;
using Reepax.Services;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    static App()
    {
        // Globally increase tooltip initial delay so tooltips appear after a longer delay (1000 ms)
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(1000));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(500));
        ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(8000));
    }

    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            LogCrash("DispatcherUnhandledException", e.Exception);
            MessageBox.Show($"Ein unerwarteter Fehler ist aufgetreten:\n\n{e.Exception.Message}\n\nDetails wurden in crash.log gespeichert.",
                "Reepax Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogCrash("AppDomain.UnhandledException", ex);
                MessageBox.Show($"Kritischer Anwendungsfehler:\n\n{ex.Message}\n\nDetails wurden in crash.log gespeichert.",
                    "Reepax Kritischer Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n";
            System.IO.File.AppendAllText(logPath, text);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    protected override void OnStartup(StartupEventArgs e)
    {
        // 1. Single-Instance Check via Mutex & Named Pipe
        if (!SingleInstanceService.Instance.TryAcquireOwnership())
        {
            var argsToSend = e.Args.Length > 0 ? e.Args : new[] { "--activate" };
            SingleInstanceService.Instance.SendArgsToPrimary(argsToSend);
            Shutdown();
            return;
        }

        // Primary instance: start named pipe server to receive launch parameters from subsequent instances
        SingleInstanceService.Instance.StartIpcServer(receivedArgs =>
        {
            Current?.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var mainWin = Current.MainWindow as MainWindow;
                SingleInstanceService.BringWindowToForeground(mainWin);

                if (mainWin?.DataContext is ViewModels.MainViewModel vm)
                {
                    foreach (var arg in receivedArgs)
                    {
                        if (PackageExportImportService.IsSupportedPackageFile(arg) &&
                            System.IO.File.Exists(arg))
                        {
                            await PackageExportImportService.ImportAndAddAsync(arg, vm);
                        }
                    }
                }
            }));
        });

        // Ensure file association for .repx (and .sdlr) is registered in user context asynchronously
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                FileAssociationService.EnsureAssociationRegistered();
            }
            catch { }
        });

        base.OnStartup(e);

        try
        {
            TimeBeginPeriod(1);
        }
        catch { }

        // Initialize UI localization with configured language (default: "en")
        try
        {
            Services.Localization.LocalizationService.Instance.CurrentLanguage = SettingsService.Instance.Settings.Language;
        }
        catch { }

        // ProcessExit hook for emergency shutdown
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Services.Download.QueueManager.PerformSafeShutdown(); } catch { }
        };

        // Once a hoster favicon is downloaded, refresh all hoster icon bindings
        // so the real icon replaces the placeholder badge immediately.
        HosterIconService.IconUpdated += _ => RefreshHosterIconBindingsDebounced();
    }

    private static System.Windows.Threading.DispatcherTimer? _iconRefreshDebounceTimer;

    private static void RefreshHosterIconBindingsDebounced()
    {
        if (Current?.Dispatcher == null || Current.Dispatcher.HasShutdownStarted) return;
        Current.Dispatcher.InvokeAsync(() =>
        {
            if (_iconRefreshDebounceTimer == null)
            {
                _iconRefreshDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _iconRefreshDebounceTimer.Tick += (s, e) =>
                {
                    _iconRefreshDebounceTimer.Stop();
                    RefreshHosterIconBindings();
                };
            }
            _iconRefreshDebounceTimer.Stop();
            _iconRefreshDebounceTimer.Start();
        });
    }

    private static void RefreshHosterIconBindings()
    {
        if (Current?.Windows == null) return;
        var windows = Current.Windows.Cast<Window>().ToArray();
        foreach (var window in windows)
            RefreshHosterIconBindings(window);
    }

    private static void RefreshHosterIconBindings(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
            RefreshHosterIconBindings(VisualTreeHelper.GetChild(root, i));

        if (root is Image image)
        {
            var expression = BindingOperations.GetBindingExpression(image, Image.SourceProperty);
            if (expression?.ParentBinding.Converter is HosterImageConverter)
                expression.UpdateTarget();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        try
        {
            Services.Download.QueueManager.PerformSafeShutdown();
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        try
        {
            SingleInstanceService.Instance.Dispose();
        }
        catch { }
        try
        {
            Services.Download.QueueManager.PerformSafeShutdown();
        }
        catch { }
        try
        {
            TimeEndPeriod(1);
        }
        catch { }
    }
}
