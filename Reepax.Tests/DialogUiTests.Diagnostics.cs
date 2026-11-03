using System;
using System.IO;
using System.Windows;
using Reepax.Services.Localization;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrashDialog_RedactsExceptionDetailsAndRetainsExplicitLogLocationActions(bool saveFailed)
    {
        RunInSta(() =>
        {
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            const string path = "Z:\\Example\\TestLocation\\crash.log";
            var dialog = new CrashReportDialog(DiagnosticPrivacyTests.CreateSensitiveException(),
                new CrashLogResult { Success = !saveFailed, PrimaryLogPath = path, ErrorMessage = DiagnosticPrivacyTests.SensitiveMessage });
            try
            {
                Assert.Contains("InvalidOperationException", dialog.ErrorMessageTextBlock.Text);
                Assert.Contains("status=403", dialog.ErrorMessageTextBlock.Text);
                DiagnosticPrivacyTests.AssertSensitiveDataRemoved(dialog.ErrorMessageTextBlock.Text);
                if (saveFailed) DiagnosticPrivacyTests.AssertSensitiveDataRemoved(dialog.LogPathTextBox.Text);
                else Assert.Equal(path, dialog.LogPathTextBox.Text);
                Assert.Equal(!saveFailed, dialog.OpenFolderButton.IsEnabled);
                Assert.Equal(!saveFailed, dialog.CopyPathButton.IsEnabled);
            }
            finally
            {
                dialog.Close();
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }

    [Theory]
    [InlineData("de", false)]
    [InlineData("de", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public void CrashFallback_RedactsTheLocalizedMessageAndSavedLocation(string language, bool critical)
    {
        string previous = LocalizationService.Instance.CurrentLanguage;
        try
        {
            LocalizationService.Instance.CurrentLanguage = language;
            var error = DiagnosticPrivacyTests.CreateSensitiveException();
            foreach (var result in new[]
            {
                new CrashLogResult { Success = true, PrimaryLogPath = "Z:\\Example\\TestLocationSecret\\crash.log" },
                new CrashLogResult { LoggingDisabled = true },
                new CrashLogResult { Success = false }
            })
            {
                string message = CrashReportDialog.BuildFallbackMessage(error, result, critical);
                Assert.Contains("status=403", message);
                DiagnosticPrivacyTests.AssertSensitiveDataRemoved(message);
                Assert.DoesNotContain("TestLocationSecret", message);
                Assert.DoesNotContain("{0}", message);
                Assert.DoesNotContain("{1}", message);
            }
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = previous;
        }
    }

    [Fact]
    public void CopiedDiagnostics_ExcludeLocalDirectoriesAndRetainSystemDetails()
    {
        var viewModel = new MainViewModel();
        string? previous = SettingsService.AppDataDirectoryOverride;
        var previousWriter = viewModel.ClipboardTextWriter;
        string? copied = null;
        int copies = 0;
        try
        {
            string data = Path.Combine(Path.GetTempPath(), "TestDiagnosticLocation");
            SettingsService.AppDataDirectoryOverride = data;
            viewModel.ClipboardTextWriter = value => { copied = value; copies++; };
            viewModel.CopySystemDiagnosticInfoCommand.Execute(null);
            string text = Assert.IsType<string>(copied);

            Assert.Equal(1, copies);
            Assert.Equal(Loc.Get("About_DiagnosticCopied"), viewModel.StatusSummary);
            Assert.Contains(viewModel.AppVersion, text);
            Assert.Contains(viewModel.ArchitectureInfo, text);
            Assert.Contains(viewModel.DotNetRuntimeInfo, text);
            Assert.Contains("- AppData Path:", text);
            Assert.Contains("- Logs Path:", text);
            Assert.DoesNotContain(data, text);
            Assert.DoesNotContain("TestDiagnosticLocation", text);
            Assert.DoesNotContain(viewModel.LogsFolderPath, text);
            Assert.Equal(data, viewModel.AppDataFolderPath);
        }
        finally
        {
            SettingsService.AppDataDirectoryOverride = previous;
            viewModel.ClipboardTextWriter = previousWriter;
        }
    }
}
