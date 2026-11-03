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
    [Fact]
    public void CrashDialog_RedactsExceptionDetailsAndRetainsExplicitLogLocationActions()
    {
        RunInSta(() =>
        {
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            const string path = "Z:\\Example\\TestLocation\\crash.log";
            var dialog = new CrashReportDialog(new InvalidOperationException(
                "Request https://files.example.test/TestDialogSecret/archive failed status=403"),
                new CrashLogResult { Success = true, PrimaryLogPath = path });
            try
            {
                Assert.Contains("InvalidOperationException", dialog.ErrorMessageTextBlock.Text);
                Assert.Contains("status=403", dialog.ErrorMessageTextBlock.Text);
                Assert.DoesNotContain("TestDialogSecret", dialog.ErrorMessageTextBlock.Text);
                Assert.Equal(path, dialog.LogPathTextBox.Text);
                Assert.True(dialog.OpenFolderButton.IsEnabled);
                Assert.True(dialog.CopyPathButton.IsEnabled);
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
            var error = new IOException("Request https://files.example.test/archive?token=TestFallbackSecret failed status=403");
            foreach (var result in new[]
            {
                new CrashLogResult { Success = true, PrimaryLogPath = "Z:\\Example\\TestLocationSecret\\crash.log" },
                new CrashLogResult { LoggingDisabled = true },
                new CrashLogResult { Success = false }
            })
            {
                string message = CrashReportDialog.BuildFallbackMessage(error, result, critical);
                Assert.Contains("status=403", message);
                Assert.DoesNotContain("TestFallbackSecret", message);
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
        try
        {
            string data = Path.Combine(Path.GetTempPath(), "TestDiagnosticLocation");
            SettingsService.AppDataDirectoryOverride = data;
            string text = viewModel.BuildSystemDiagnosticInfo();

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
        }
    }
}
