using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Reepax.Services.Storage;

internal static class VerificationFileIo
{
    private const int FileIoPriorityHintInfo = 12;
    private const int LowPriorityHint = 1;

    internal static void LowerPriority(Stream stream)
    {
        if (stream is FileStream file) LowerPriority(file.SafeFileHandle);
    }

    internal static void LowerPriority(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid || handle.IsClosed) return;
        var information = new PriorityHint { Value = LowPriorityHint };
        // This hint affects only this file handle; unsupported drivers retain normal verification.
        _ = SetFileInformationByHandle(handle, FileIoPriorityHintInfo, ref information, (uint)Marshal.SizeOf<PriorityHint>());
    }

    [StructLayout(LayoutKind.Sequential, Size = 8)]
    private struct PriorityHint { public int Value; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref PriorityHint information, uint size);
}
