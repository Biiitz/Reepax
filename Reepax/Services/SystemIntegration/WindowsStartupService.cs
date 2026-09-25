using System;
using System.IO;
using Microsoft.Win32;

namespace Reepax.Services.SystemIntegration;

public static class WindowsStartupService
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Reepax";

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            var value = key?.GetValue(AppName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetAutostart(bool enable)
    {
        try
        {
            if (Storage.DownloadPersistenceService.IsTestEnvironment)
                return true;

            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RegistryKeyPath, writable: true);
            if (key == null)
                return false;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || 
                    exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || 
                    !File.Exists(exePath))
                {
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reepax.exe");
                }

                var command = $"\"{exePath}\" --minimized";
                key.SetValue(AppName, command);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Storage.AppLogger.Error("Error updating Windows startup registry key", ex);
            return false;
        }
    }
}
