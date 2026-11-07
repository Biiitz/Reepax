using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Reepax.Models;
using Reepax.Services.Download;
using Reepax.Services.Verification;
using Xunit;

namespace Reepax.Tests;

public sealed class VerificationIoPriorityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("VerificationIo_").FullName;

    [Theory]
    [InlineData("SHA256", false)]
    [InlineData("SHA256", true)]
    [InlineData("SHA1", true)]
    [InlineData("MD5", true)]
    [InlineData("CRC32", true)]
    [InlineData("Default", true)]
    public async Task FileVerification_UsesLowPriorityReadsAndPreservesResults(string algorithm, bool cancellable)
    {
        byte[] bytes = Encoding.ASCII.GetBytes("123456789");
        string path = Path.Combine(_root, "payload.bin");
        File.WriteAllBytes(path, bytes);
        string unrelated = Path.Combine(_root, "unrelated.bin");
        using var writer = new FileStream(unrelated, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        writer.Write(bytes);
        int writerPriority = ReadPriority(writer.SafeFileHandle);
        var observedPriorities = new List<int>();
        var service = new ChecksumVerificationService(file => new ObservingFileStream(file, observedPriorities));
        string expected = algorithm switch
        {
            "SHA1" => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
            "MD5" => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(),
            "CRC32" => "cbf43926",
            _ => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        };
        var item = new DownloadItem
        {
            FileName = "payload.bin", SaveFilePath = path, TotalBytes = bytes.Length,
            ExpectedChecksum = algorithm == "Default" ? null : expected
        };
        using var cancellation = new CancellationTokenSource();

        var result = await service.VerifyFileIntegrityAsync(item, cancellable ? cancellation.Token : default);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal(expected, result.CalculatedChecksum);
        Assert.Equal(algorithm == "Default" ? "SHA256" : algorithm, result.Algorithm);
        Assert.NotEmpty(observedPriorities);
        Assert.True(observedPriorities.All(priority => priority == 1), "Verification reads did not use low I/O priority.");
        Assert.Equal(writerPriority, ReadPriority(writer.SafeFileHandle));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        writer.Position = 0;
        var actual = new byte[bytes.Length];
        writer.ReadExactly(actual);
        Assert.Equal(bytes, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrittenByteVerification_LowersOnlyTheFinishingFilePriorityAndStillDetectsDamage(bool damage)
    {
        byte[] bytes = Enumerable.Range(0, 128 * 1024).Select(index => (byte)(index % 251)).ToArray();
        string path = Path.Combine(_root, "partial.bin");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        file.Write(bytes);
        using var integrity = new LocalDownloadIntegrity();
        integrity.Append(bytes);
        if (damage) RandomAccess.Write(file.SafeFileHandle, new byte[] { 255 }, 17);
        file.Flush(flushToDisk: true);
        using var writer = new FileStream(Path.Combine(_root, "active.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        int writerPriority = ReadPriority(writer.SafeFileHandle);

        if (damage) Assert.Throws<InvalidDataException>(() => integrity.VerifyFile(file.SafeFileHandle, 0, default));
        else integrity.VerifyFile(file.SafeFileHandle, 0, default);

        Assert.Equal(1, ReadPriority(file.SafeFileHandle));
        Assert.Equal(writerPriority, ReadPriority(writer.SafeFileHandle));
        writer.Write(bytes);
        writer.Position = 0;
        var actual = new byte[bytes.Length];
        writer.ReadExactly(actual);
        Assert.Equal(bytes, actual);
    }

    private sealed class ObservingFileStream(string path, List<int> priorities)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            priorities.Add(ReadPriority(SafeFileHandle));
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            priorities.Add(ReadPriority(SafeFileHandle));
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            priorities.Add(ReadPriority(SafeFileHandle));
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }

    private static int ReadPriority(SafeFileHandle handle)
    {
        int status = NtQueryInformationFile(handle, out _, out int priority, sizeof(int), 43);
        Assert.Equal(0, status);
        return priority;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public nint Status; public nuint Information; }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status,
        out int information, int length, int informationClass);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
