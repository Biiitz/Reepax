using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Reepax.Services.Storage;

namespace Reepax.Services.SystemIntegration;

/// <summary>
/// Registers the file association for .repx (and .sdlr) in the current user registry (HKCU),
/// enabling double-clicking in Windows Explorer to open the app and import the package.
/// </summary>
public static class FileAssociationService
{
    private const string Extension = ".repx";
    private const string LegacyExtension = ".sdlr";
    private const string ProgId = "Reepax.Package";
    private const string FileDescription = "Reepax Package";

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_FLUSH = 0x1000;

    /// <summary>
    /// Ensures that file associations for Reepax are registered in HKCU.
    /// </summary>
    public static void EnsureAssociationRegistered()
    {
        if (DownloadPersistenceService.IsTestEnvironment)
            return;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) ||
                exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(exePath))
            {
                exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reepax.exe");
            }

            if (!File.Exists(exePath))
                return;

            var expectedCmd = $"\"{exePath}\" \"%1\"";

            // Early check: if already registered to this exact executable, no need to touch registry or notify shell
            try
            {
                using var existingCmdKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command");
                var currentCmd = existingCmdKey?.GetValue(string.Empty) as string;
                if (string.Equals(currentCmd, expectedCmd, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch { }

            // 1. HKCU\Software\Classes\.repx
            using (var extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Extension))
            {
                extKey?.SetValue(string.Empty, ProgId);
                extKey?.SetValue("Content Type", "application/x-reepax-package");
            }

            // 1b. Legacy .sdlr -> Reepax.Package
            using (var legacyExtKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + LegacyExtension))
            {
                legacyExtKey?.SetValue(string.Empty, ProgId);
                legacyExtKey?.SetValue("Content Type", "application/x-reepax-package");
            }

            // 2. HKCU\Software\Classes\Reepax.Package
            using (var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                if (progIdKey != null)
                {
                    progIdKey.SetValue(string.Empty, FileDescription);

                    // DefaultIcon
                    using (var iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey?.SetValue(string.Empty, $"\"{exePath}\",0");
                    }

                    // shell\open\command
                    using (var cmdKey = progIdKey.CreateSubKey(@"shell\open\command"))
                    {
                        cmdKey?.SetValue(string.Empty, expectedCmd);
                    }
                }
            }

            // Notify Windows Shell (without SHCNF_FLUSH to avoid Explorer freeze)
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            AppLogger.Debug("Registered .repx and .sdlr file association in HKCU.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to register file association: {ex.Message}");
        }
    }
}
