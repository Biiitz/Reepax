using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Reepax.Services.Storage;

internal static class RelocationFileAccess
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint ReadFlags = (uint)(FileOptions.Asynchronous | FileOptions.SequentialScan) | OpenReparsePoint;
    private const int FileDispositionInfo = 4;

    internal sealed class SourceLease(FileStream stream, bool canDelete) : IDisposable
    {
        internal FileStream Stream { get; } = stream;
        internal bool CanDelete => canDelete;

        internal bool TryDelete()
        {
            if (!canDelete) return false;
            var disposition = new FileDisposition { DeleteFile = 1 };
            return SetFileInformationByHandle(Stream.SafeFileHandle, FileDispositionInfo, ref disposition, 1);
        }

        public void Dispose() => Stream.Dispose();
    }

    internal static SourceLease OpenSource(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var nativePath = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath
            : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
        var handle = CreateFileW(nativePath, GenericRead | DeleteAccess, FileShare.Read, 0, OpenExisting, ReadFlags, 0);
        bool canDelete = !handle.IsInvalid;
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is not (5 or 32)) throw Failure("The source file could not be protected for relocation.", error);
            // Existing readers may allow copying but not deletion; preserve that cleanup-warning path.
            handle = CreateFileW(nativePath, GenericRead, FileShare.Read, 0, OpenExisting, ReadFlags, 0);
            if (handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw Failure("The source file could not be protected for relocation.", error);
            }
        }
        try { return new SourceLease(new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: true), canDelete); }
        catch { handle.Dispose(); throw; }
    }

    private static IOException Failure(string message, int error) => new(message, new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte DeleteFile; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDisposition information, uint size);
}
