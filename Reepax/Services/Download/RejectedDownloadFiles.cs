using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Reepax.Models;
using Reepax.Services.Storage;

namespace Reepax.Services.Download;

public sealed class RejectedDownloadFiles
{
    public sealed record Identity(uint Volume, ulong Index, ulong Length, long Created, long Written);

    public bool Published { get; set; }
    public Identity? Data { get; set; }
    public Identity? Checkpoint { get; set; }

    internal static RejectedDownloadFiles Capture(string? path, bool published) => new()
    {
        Published = published,
        Data = CaptureIdentity(path == null ? null : path + (published ? "" : ".part")),
        Checkpoint = published || path == null ? null : CaptureIdentity(path + ".part.segments")
    };

    private static Identity? CaptureIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            // Metadata-only access also identifies rejected files held by another reader.
            using var handle = CreateFileW(NativePath(path), 0, FileShare.ReadWrite | FileShare.Delete,
                0, 3, 0x00200000, 0);
            return handle.IsInvalid ? null : ReadIdentity(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal bool TryReset(DownloadItem item)
    {
        string? path = item.SaveFilePath;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var leases = new List<RelocationFileAccess.SourceLease>();
        try
        {
            if (Published && (!IsAbsent(path + ".part") || !IsAbsent(path + ".part.segments")))
                return false;
            if (!Protect(path + (Published ? "" : ".part"), Data, leases) ||
                (!Published && !Protect(path + ".part.segments", Checkpoint, leases)))
                return false;
            foreach (var lease in leases)
                if (!lease.TryDelete()) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
        item.RejectedFiles = null;
        item.DownloadedBytes = 0;
        item.ProgressPercentage = 0;
        item.CalculatedChecksum = null;
        item.CompletedAt = null;
        DownloadPersistenceService.Instance.RequestSave();
        return true;
    }

    private static bool Protect(string path, Identity? expected, List<RelocationFileAccess.SourceLease> leases)
    {
        if (IsAbsent(path)) return true;
        if (expected == null) return false;
        var lease = RelocationFileAccess.OpenSource(path);
        // Reject copy-only leases before deleting any associated file.
        if (!lease.CanDelete || ReadIdentity(lease.Stream.SafeFileHandle) != expected)
        {
            lease.Dispose();
            return false;
        }
        // Keep the verified object locked until deletion by handle; never delete a replacement by path.
        leases.Add(lease);
        return true;
    }

    private static bool IsAbsent(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    internal void RebindVerifiedCopy(SafeFileHandle source, SafeFileHandle destination, bool checkpoint)
    {
        var expected = checkpoint ? Checkpoint : Data;
        if (expected == null || ReadIdentity(source) != expected) return;
        var replacement = ReadIdentity(destination);
        if (replacement == null) return;
        if (checkpoint) Checkpoint = replacement;
        else Data = replacement;
    }

    private static Identity? ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) ||
            (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            return null;
        return new Identity(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            ((ulong)info.SizeHigh << 32) | info.SizeLow, info.Created, info.Written);
    }

    private static string NativePath(string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public long Created;
        public long Accessed;
        public long Written;
        public uint Volume;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Links;
        public uint IndexHigh;
        public uint IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
