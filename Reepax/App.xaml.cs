using System;
using System.Windows;
using System.Windows.Controls;
using Reepax.Services.Localization;
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
            var result = CrashLogService.LogCrash("DispatcherUnhandledException", e.Exception);
            Views.CrashReportDialog.ShowModal(e.Exception, result, isCritical: false);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                var result = CrashLogService.LogCrash("AppDomain.UnhandledException", ex);
                Views.CrashReportDialog.ShowModal(ex, result, isCritical: true);
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            CrashLogService.LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Manual restarts wait for the previous process to finish saving before taking ownership.
        bool isRestart = e.Args.Any(a => string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase));
        if (isRestart) AppRestartService.WaitForRestartParent(e.Args);
        if (!SingleInstanceService.Instance.TryAcquireOwnership(isRestart ? 4000 : 0))
        {
            TryForwardLaunchArguments(e.Args, args => SingleInstanceService.Instance.SendArgsToPrimary(args),
                ShowInstanceHandoffFailure);
            Shutdown();
            return;
        }

        try
        {
            if (Services.Update.AppUpdateService.HandleInterruptedUpdate(AppContext.BaseDirectory, e.Args))
            {
                Shutdown();
                return;
            }
        }
        catch (Exception)
        {
            MessageBox.Show(Services.Localization.Loc.Get("UpdateDialog_Status_PreviousFailed"), Services.Localization.Loc.Get("UpdateDialog_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _isPrimaryInstance = true;
        // Primary instance: start named pipe server to receive launch parameters from subsequent instances
        SingleInstanceService.Instance.StartIpcServer(ReceiveInstanceArguments);

        // Register file associations without blocking startup.
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

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (MainWindow?.IsLoaded != true) return;
            ProcessPendingInstanceArguments();
            try
            {
                Services.Update.AppUpdateService.ConfirmUpdateStartup(AppContext.BaseDirectory, e.Args);
                Services.Update.AppUpdateService.ShowPreviousUpdateResult();
            }
            catch (Exception ex) { AppLogger.Warn("[Update] Startup confirmation failed: " + ex.GetType().Name); }
        }));

        // ProcessExit hook for emergency shutdown
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Services.Download.QueueManager.PerformSafeShutdown(); } catch { }
        };

        // Register archive password prompt dialog handler for password-protected extraction
        Services.Extractor.ArchiveExtractionService.PasswordRetryPromptHandler = async (archiveName, retry, cancellationToken) =>
        {
            if (Current?.Dispatcher != null && !Current.Dispatcher.CheckAccess())
            {
                return await Current.Dispatcher.InvokeAsync(() => Views.PasswordPromptDialog.ShowDialog(archiveName,
                    retry: retry, cancellationToken: cancellationToken));
            }
            return Views.PasswordPromptDialog.ShowDialog(archiveName, retry: retry, cancellationToken: cancellationToken);
        };
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (!_isPrimaryInstance)
        {
            base.OnSessionEnding(e);
            return;
        }
        if (MainWindow is MainWindow mw)
        {
            mw.ForceExit();
        }
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
            CompleteInstanceExit(_isPrimaryInstance, () =>
            {
                try { Services.Download.QueueManager.PerformSafeShutdown(); }
                catch { }
                try { TimeEndPeriod(1); }
                catch { }
            }, () => SingleInstanceService.Instance.Dispose());
        }
        catch { }
    }
}
