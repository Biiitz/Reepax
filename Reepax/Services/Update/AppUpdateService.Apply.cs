using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reepax.Services.Download;
using Reepax.Services.Storage;

namespace Reepax.Services.Update;

public partial class AppUpdateService
{
    private static int _applying;
    internal static readonly SemaphoreSlim OperationGate = new(1, 1);

    public static bool ApplyUpdate(string updatePath, string? expectedSha256 = null, string? expectedVersion = null)
    {
        if (DownloadPersistenceService.IsTestEnvironment && ApplyUpdateActionOverride != null)
        {
            ApplyUpdateActionOverride(updatePath);
            return true;
        }
        if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0) return false;
        try
        {
            string exe = Environment.ProcessPath ?? throw new IOException("Application executable is unavailable.");
            string session = Path.GetDirectoryName(updatePath)!;
            if (expectedSha256 == null) throw new InvalidDataException("The update is not verified.");
            if (expectedVersion == null || !TryParseAppVersion(expectedVersion, out _, out _, out _, out _))
                throw new InvalidDataException("The expected update version is required.");
            using (var stream = File.OpenRead(updatePath))
            {
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The update changed after verification.");
            }
            string script = GenerateInstallerUpdateScript(Environment.ProcessId, updatePath, exe, expectedSha256, expectedVersion);
            string scriptPath = Path.Combine(session, "apply_update.ps1");
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
            foreach (string flag in new[] { "helper.ready", "helper.approved", "helper.cancel" })
                File.Delete(Path.Combine(session, flag));
            using var process = StartUpdateHelper(scriptPath);
            if (process == null) throw new IOException("The update helper could not be started.");
            // The helper confirms initialization before the running application exits.
            string ready = Path.Combine(session, "helper.ready");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(ready) && !process.HasExited && timer.Elapsed < TimeSpan.FromSeconds(15))
                Thread.Sleep(50);
            if (!File.Exists(ready) || process.HasExited)
            {
                File.WriteAllText(Path.Combine(session, "helper.cancel"), "cancel");
                process.WaitForExit(2000);
                throw new IOException("The update helper did not initialize.");
            }
            File.WriteAllText(Path.Combine(session, "helper.approved"), "approved");
            PerformSafeShutdownAndExit();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[AppUpdateService] Failed to apply update", ex);
            Interlocked.Exchange(ref _applying, 0);
            return false;
        }
    }

    internal static Process? StartUpdateHelper(string path, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        // Avoid inheriting modules for a different shell version.
        info.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(info.FileName)!, "Modules");
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", path }.Concat(arguments))
            info.ArgumentList.Add(arg);
        return Process.Start(info);
    }

    private static string QuoteScript(string value) => "'" + value.Replace("'", "''") + "'";
    private static string ReadScript(string name)
    {
        using var stream = typeof(AppUpdateService).Assembly.GetManifestResourceStream("Reepax.Services.Update." + name + ".ps1")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ScriptHeader(int procId, string source, string targetExe)
    {
        string result = Path.Combine(SettingsService.AppDataDirectory, "update-result.json");
        return "$ErrorActionPreference = 'Stop'\n" +
            $"$procId = {procId}\n$source = {QuoteScript(source)}\n$exe = {QuoteScript(targetExe)}\n" +
            $"$resultPath = {QuoteScript(result)}\n$restartArgs = '--restart'\n" + ReadScript("UpdateWorkspace");
    }

    public static string GenerateInstallerUpdateScript(int procId, string setupPath, string targetExe, string? expectedSha256 = null,
        string? expectedVersion = null) => ScriptHeader(procId, setupPath, targetExe) +
        $"$expectedHash = {QuoteScript(expectedSha256 ?? "")}\n$expectedVersion = {QuoteScript(expectedVersion ?? AppCurrentVersion)}\n" +
        ReadScript("InstallerUpdate");

    internal static bool HandleInterruptedUpdate(string directory, string[] args)
    {
        string request = Path.Combine(SettingsService.AppDataDirectory, "update-startup.json");
        if (!File.Exists(request)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(request));
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("Executable").GetString(), Path.Combine(directory, "Reepax.exe"),
            StringComparison.OrdinalIgnoreCase)) return false;
        string? session = root.GetProperty("Session").GetString();
        if (args.Contains("--update-session=" + session)) return false;
        try
        {
            using var helper = Process.GetProcessById(root.GetProperty("HelperId").GetInt32());
            if (!helper.HasExited && helper.StartTime.ToUniversalTime().Ticks.ToString() == root.GetProperty("HelperStart").GetString())
                return true;
        }
        catch (ArgumentException) { }
        File.Delete(request);
        File.Delete(Path.Combine(SettingsService.AppDataDirectory, "update-startup.ready"));
        File.WriteAllText(Path.Combine(SettingsService.AppDataDirectory, "update-result.json"), "{\"Status\":\"failed\"}");
        return false;
    }

    internal static void ConfirmUpdateStartup(string directory, string[] args)
    {
        string request = Path.Combine(SettingsService.AppDataDirectory, "update-startup.json");
        if (!File.Exists(request)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(request));
        var root = document.RootElement;
        string session = root.GetProperty("Session").GetString()!;
        string executable = Path.GetFullPath(Path.Combine(directory, "Reepax.exe"));
        if (Guid.TryParseExact(session, "N", out _) && args.Contains("--update-session=" + session) &&
            root.GetProperty("Version").GetString() == AppCurrentVersion &&
            string.Equals(root.GetProperty("Executable").GetString(), executable, StringComparison.OrdinalIgnoreCase))
            File.WriteAllText(Path.Combine(SettingsService.AppDataDirectory, "update-startup.ready"), session);
    }

    internal static void ShowPreviousUpdateResult()
    {
        string path = Path.Combine(SettingsService.AppDataDirectory, "update-result.json");
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            string? status = document.RootElement.GetProperty("Status").GetString();
            File.Delete(path);
            if (status != "success")
                System.Windows.MessageBox.Show(Localization.Loc.Get("UpdateDialog_Status_PreviousFailed"),
                    Localization.Loc.Get("UpdateDialog_Title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex) { AppLogger.Warn("[AppUpdateService] Cannot read the previous update result: " + ex.GetType().Name); }
    }
}
