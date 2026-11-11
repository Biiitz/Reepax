using System.IO;
using Microsoft.Win32;
using Reepax.Services.Update;

namespace Reepax.Tests;

public sealed class ManagedInstallationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registration_UpdatesOnlyTheMatchingInstallerAndHidesOnlyItsDuplicate(bool matching)
    {
        string keyName = @"Software\Reepax_RegistrationTest_" + Guid.NewGuid().ToString("N");
        string installation = Path.Combine(Path.GetTempPath(), "example-installation");
        try
        {
            using var entries = Registry.CurrentUser.CreateSubKey(keyName);
            using var installer = entries.CreateSubKey(ManagedInstallation.InstallerRegistryId);
            installer.SetValue("InstallLocation", installation + Path.DirectorySeparatorChar);
            installer.SetValue("DisplayVersion", "26.11.1");
            installer.SetValue("UninstallString", "existing-uninstaller");
            using var duplicate = entries.CreateSubKey(AppUpdateService.PackageId);
            duplicate.SetValue("InstallLocation", matching ? installation : installation + "-other");

            ManagedInstallation.RefreshRegistration(entries, installation, "26.11.2");

            Assert.Equal("26.11.2", installer.GetValue("DisplayVersion"));
            Assert.Equal("existing-uninstaller", installer.GetValue("UninstallString"));
            Assert.Equal(matching ? 1 : null, duplicate.GetValue("SystemComponent"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Cleanup_DeletesOnlyListedPayloadAndRejectsPathsOutsideTheApplication()
    {
        string root = Directory.CreateTempSubdirectory("ManagedInstall_").FullName;
        try
        {
            string current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
            string installed = Path.Combine(current, "updated.bin");
            string foreign = Path.Combine(current, "personal.bin");
            string outside = Path.Combine(root, "settings.bin");
            File.WriteAllBytes(installed, new byte[] { 1, 2 });
            File.WriteAllBytes(foreign, new byte[] { 3, 4 });
            File.WriteAllBytes(outside, new byte[] { 5, 6 });
            File.WriteAllLines(Path.Combine(current, "update-files.txt"), new[] { "updated.bin", "../settings.bin", outside });

            ManagedInstallation.RemoveInstalledPayloadFiles(current);

            Assert.False(File.Exists(installed));
            Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(foreign));
            Assert.Equal(new byte[] { 5, 6 }, File.ReadAllBytes(outside));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
