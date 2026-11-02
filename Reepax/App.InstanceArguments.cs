using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax;

public partial class App
{
    private readonly PendingInstanceArguments _pendingInstanceArguments = new();
    private bool _isPrimaryInstance;

    internal static bool TryForwardLaunchArguments(string[] args, Func<string[], bool> send, Action showFailure)
    {
        if (send(args.Length > 0 ? args : ["--activate"])) return true;
        showFailure();
        return false;
    }

    private static void ShowInstanceHandoffFailure()
    {
        var language = ReadInstanceFailureLanguage(SettingsService.AppDataDirectory, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        var texts = language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? Strings_de.Map : Strings_en.Map;
        MessageBox.Show(texts["Instance_HandoffFailed_Message"], texts["Instance_HandoffFailed_Title"],
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal static string ReadInstanceFailureLanguage(string directory, string fallback)
    {
        // Reading a startup label must not initialize storage, migrate data, or clean logs.
        foreach (var name in new[] { "settings.json", "settings.json.bak" })
        {
            try
            {
                var raw = DownloadPersistenceService.ReadFileWithRetry(Path.Combine(directory, name));
                if (string.IsNullOrWhiteSpace(raw)) continue;
                using var document = JsonDocument.Parse(SecureAppDataStorage.DecryptString(raw));
                if (document.RootElement.TryGetProperty("Language", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } language)
                    return language;
            }
            catch (Exception) { }
        }
        return fallback;
    }

    internal static void CompleteInstanceExit(bool primary, Action stopPrimary, Action releaseOwnership)
    {
        try { if (primary) stopPrimary(); }
        finally { releaseOwnership(); }
    }

    private void ReceiveInstanceArguments(string[] args)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            throw new InvalidOperationException("The application is shutting down.");
        _pendingInstanceArguments.Enqueue(args);
        Dispatcher.BeginInvoke(new Action(ProcessPendingInstanceArguments));
    }

    private async void ProcessPendingInstanceArguments()
    {
        // Keep accepted requests until startup has created the main view model.
        if (MainWindow is not MainWindow mainWindow || !mainWindow.IsLoaded ||
            mainWindow.DataContext is not ViewModels.MainViewModel viewModel)
            return;
        await _pendingInstanceArguments.DrainAsync(async args =>
        {
            SingleInstanceService.BringWindowToForeground(mainWindow);
            foreach (var arg in args)
            {
                if (PackageExportImportService.IsSupportedPackageFile(arg) && File.Exists(arg))
                    await PackageExportImportService.ImportAndAddAsync(arg, viewModel);
            }
        });
    }
}
