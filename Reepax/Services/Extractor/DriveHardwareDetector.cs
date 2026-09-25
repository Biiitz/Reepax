using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Reepax.Services.Extractor;

public enum DriveStorageType
{
    Unknown,
    Hdd,
    SataSsd,
    NvmeM2Ssd
}

public static class DriveHardwareDetector
{
    private static readonly ConcurrentDictionary<string, DriveStorageType> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Event triggered when an asynchronous drive type detection finishes refining a drive's storage type.
    /// </summary>
    public static event Action<string, DriveStorageType>? DriveTypeDetected;

    public static DriveStorageType DetectDriveType(string? path)
    {
        string driveLetter = GetDriveLetter(path);
        if (string.IsNullOrWhiteSpace(driveLetter))
            return DriveStorageType.SataSsd;

        if (_cache.TryGetValue(driveLetter, out var cached))
            return cached;

        var detected = DetectDriveTypeInternal(driveLetter);
        _cache[driveLetter] = detected;
        return detected;
    }

    public static bool IsLowResourceRecommended(string? path)
    {
        var type = DetectDriveType(path);
        return type is DriveStorageType.Hdd or DriveStorageType.SataSsd or DriveStorageType.Unknown;
    }

    public static string GetDriveStorageDescription(string? path)
    {
        var type = DetectDriveType(path);
        var driveLetter = GetDriveLetter(path);

        return type switch
        {
            DriveStorageType.Hdd => Localization.Loc.Format("Drive_HddDescription", driveLetter),
            DriveStorageType.SataSsd => Localization.Loc.Format("Drive_SataSsdDescription", driveLetter),
            DriveStorageType.NvmeM2Ssd => Localization.Loc.Format("Drive_NvmeDescription", driveLetter),
            _ => Localization.Loc.Format("Drive_DefaultDescription", driveLetter)
        };
    }

    private static string GetDriveLetter(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var letter = root.TrimEnd('\\', '/').Trim();
                return letter;
            }
        }
        catch { }

        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\', '/').Trim();
        return !string.IsNullOrWhiteSpace(sysRoot) ? sysRoot : "C:";
    }

    private static DriveStorageType DetectDriveTypeInternal(string driveLetter)
    {
        try
        {
            var win32Result = QueryWin32StorageProperty(driveLetter);
            if (win32Result != DriveStorageType.Unknown)
            {
                return win32Result;
            }
        }
        catch { }

        // If Win32 detection did not immediately identify the type, default to SataSsd immediately
        // so that the UI thread is never blocked, and refine asynchronously in the background.
        _ = System.Threading.Tasks.Task.Run(() => RefineDriveTypeWithPowerShell(driveLetter));

        return DriveStorageType.SataSsd;
    }

    private static void RefineDriveTypeWithPowerShell(string driveLetter)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
            {
                return;
            }

            var cleanLetter = driveLetter.Trim().TrimEnd(':').Trim().TrimEnd('\\', '/').Trim();
            if (cleanLetter.Length != 1 || !((cleanLetter[0] >= 'a' && cleanLetter[0] <= 'z') || (cleanLetter[0] >= 'A' && cleanLetter[0] <= 'Z')))
            {
                return;
            }

            char validatedLetter = char.ToUpperInvariant(cleanLetter[0]);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"$p = Get-Partition -DriveLetter '{validatedLetter}' -ErrorAction SilentlyContinue | Get-Disk -ErrorAction SilentlyContinue; if ($p) {{ Write-Output ($p.BusType.ToString() + '|' + (Get-PhysicalDisk -DeviceNumber $p.Number -ErrorAction SilentlyContinue | Select-Object -ExpandProperty MediaType -ErrorAction SilentlyContinue)) }}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var readTask = process.StandardOutput.ReadToEndAsync();
                if (process.WaitForExit(3000))
                {
                    var output = readTask.GetAwaiter().GetResult().Trim();

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        var parts = output.Split('|');
                        var busType = parts[0].Trim();
                        var mediaType = parts.Length > 1 ? parts[1].Trim() : string.Empty;

                        DriveStorageType detected = DriveStorageType.SataSsd;
                        if (busType.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
                        {
                            detected = DriveStorageType.NvmeM2Ssd;
                        }
                        else if (mediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase))
                        {
                            detected = DriveStorageType.Hdd;
                        }
                        else if (busType.Equals("SATA", StringComparison.OrdinalIgnoreCase) || busType.Equals("ATAPI", StringComparison.OrdinalIgnoreCase))
                        {
                            detected = DriveStorageType.SataSsd;
                        }

                        _cache[driveLetter] = detected;
                        DriveTypeDetected?.Invoke(driveLetter, detected);
                    }
                }
                else
                {
                    try 
                    { 
                        process.Kill(entireProcessTree: true); 
                        process.WaitForExit(1000);
                    } 
                    catch { }
                }
            }
        }
        catch { }
    }

    #region Win32 P/Invoke Storage Query

    private const uint FILE_ANY_ACCESS = 0;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_ADAPTER_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public uint MaximumTransferLength;
        public uint MaximumPhysicalPages;
        public uint AlignmentMask;
        public byte AdapterUsesPio;
        public byte AdapterScansDown;
        public byte CommandQueueing;
        public byte AcceleratedTransfer;
        public byte BusType;
        public ushort BusMajorVersion;
        public ushort BusMinorVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.I1)]
        public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        uint nInBufferSize,
        out STORAGE_ADAPTER_DESCRIPTOR lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        uint nInBufferSize,
        out DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static DriveStorageType QueryWin32StorageProperty(string driveLetter)
    {
        var volumePath = $@"\\.\{driveLetter.TrimEnd('\\')}";
        var handle = CreateFile(volumePath, FILE_ANY_ACCESS, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == (IntPtr)(-1))
        {
            return DriveStorageType.Unknown;
        }

        try
        {
            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = 1, // StorageAdapterProperty
                QueryType = 0,
                AdditionalParameters = new byte[1]
            };

            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, ref query, (uint)Marshal.SizeOf(query),
                                out STORAGE_ADAPTER_DESCRIPTOR adapterDescriptor, (uint)Marshal.SizeOf(typeof(STORAGE_ADAPTER_DESCRIPTOR)),
                                out _, IntPtr.Zero))
            {
                if (adapterDescriptor.BusType == 17) // BusTypeNvme
                {
                    return DriveStorageType.NvmeM2Ssd;
                }

                var seekQuery = new STORAGE_PROPERTY_QUERY
                {
                    PropertyId = 7, // StorageDeviceSeekPenaltyProperty
                    QueryType = 0,
                    AdditionalParameters = new byte[1]
                };

                if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, ref seekQuery, (uint)Marshal.SizeOf(seekQuery),
                                    out DEVICE_SEEK_PENALTY_DESCRIPTOR seekDescriptor, (uint)Marshal.SizeOf(typeof(DEVICE_SEEK_PENALTY_DESCRIPTOR)),
                                    out _, IntPtr.Zero))
                {
                    if (seekDescriptor.IncursSeekPenalty)
                    {
                        return DriveStorageType.Hdd;
                    }
                }

                if (adapterDescriptor.BusType == 11 || adapterDescriptor.BusType == 3 || adapterDescriptor.BusType == 1)
                {
                    return DriveStorageType.SataSsd;
                }
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        return DriveStorageType.Unknown;
    }

    #endregion
}
