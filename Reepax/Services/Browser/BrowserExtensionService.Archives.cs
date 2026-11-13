using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Reepax.Services.Storage;

namespace Reepax.Services.Browser;

public partial class BrowserExtensionService
{
    private const string ArchiveWorkPrefix = ".reepax-extension-";

    private static void ExtractArchives(Dictionary<string, ExtensionSavedState> states)
    {
        string root = ExtensionsDirectory;
        if (!Directory.Exists(root)) return;
        foreach (string path in Directory.EnumerateFiles(root, "*.zip").Concat(Directory.EnumerateFiles(root, "*.crx")))
        {
            string name = Path.GetFileName(path);
            string folder = Path.GetFileNameWithoutExtension(path);
            if (!IsExtensionArchive(path) || string.IsNullOrWhiteSpace(folder) ||
                folder.StartsWith(ArchiveWorkPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            states.TryGetValue(folder, out var state);
            if (state?.RemovalPending == true) continue;

            string target = Path.Combine(root, folder);
            try
            {
                if (Path.Exists(target))
                {
                    if (state?.ArchiveHash == null && FindExtensionFolder(target) == null)
                        AppLogger.Warn("[Extensions] An existing extension folder prevents archive import.");
                    continue;
                }
                using var stream = File.OpenRead(path);
                string hash = Convert.ToHexString(SHA256.HashData(stream));
                if (state?.RemovedArchives != null && state.RemovedArchives.Any(entry =>
                    string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase) && entry.Value == hash))
                    continue;

                string workspace = Directory.CreateDirectory(Path.Combine(root, ArchiveWorkPrefix + Guid.NewGuid().ToString("N"))).FullName;
                try
                {
                    stream.Position = 0;
                    if (Path.GetExtension(name).Equals(".crx", StringComparison.OrdinalIgnoreCase))
                        ExtractCrx(stream, workspace);
                    else
                    {
                        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                        zip.ExtractToDirectory(workspace);
                    }

                    string? extensionFolder = FindExtensionFolder(workspace);
                    if (extensionFolder == null || ParseManifest(extensionFolder, Path.Combine(extensionFolder, "manifest.json")) == null)
                        throw new InvalidDataException("The extension archive does not contain a readable manifest.");

                    var imported = new ExtensionSavedState
                    {
                        IsEnabled = state?.IsEnabled ?? true,
                        ArchiveName = name,
                        ArchiveHash = hash,
                        RemovedArchives = state?.RemovedArchives
                    };
                    states[folder] = imported;
                    bool saved = false;
                    bool published = false;
                    try
                    {
                        // Record the source before the folder becomes visible to scans.
                        if (!(saved = SaveSavedStates(states))) continue;
                        Directory.Move(workspace, target);
                        published = true;
                    }
                    finally
                    {
                        if (!published)
                        {
                            if (state == null) states.Remove(folder);
                            else states[folder] = state;
                            if (saved) SaveSavedStates(states);
                        }
                    }
                }
                finally
                {
                    if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("[Extensions] Could not import an extension archive", ex);
            }
        }
    }

    private static bool IsExtensionArchive(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".crx", StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractCrx(FileStream stream, string target)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header[..4], 4, false) < 4 || !header[..4].SequenceEqual("Cr24"u8))
        {
            stream.Position = 0;
            using var regularZip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            regularZip.ExtractToDirectory(target);
            return;
        }

        stream.Position = 4;
        stream.ReadExactly(header[..8]);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header);
        long zipOffset = 12L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version == 2)
        {
            stream.ReadExactly(header[..4]);
            zipOffset += 4L + BinaryPrimitives.ReadUInt32LittleEndian(header);
        }
        else if (version != 3)
        {
            throw new InvalidDataException("Unsupported CRX version.");
        }
        if (zipOffset >= stream.Length) throw new InvalidDataException("The CRX archive does not contain ZIP data.");

        // ZIP offsets are relative to the payload, not to the CRX header.
        string zipPath = Path.Combine(Path.GetDirectoryName(target)!, ArchiveWorkPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        using var payload = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        stream.Position = zipOffset;
        stream.CopyTo(payload);
        payload.Position = 0;
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        zip.ExtractToDirectory(target);
    }

    private static Dictionary<string, string> GetRemovedArchives(BrowserExtensionInfo extension, ExtensionSavedState? state)
    {
        var removed = state?.RemovedArchives != null
            ? new Dictionary<string, string>(state.RemovedArchives, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (extension.ArchiveName != null && extension.ArchiveHash != null)
            removed[extension.ArchiveName] = extension.ArchiveHash;

        // Older imports lack provenance. Suppress archives already competing for this folder.
        foreach (string path in Directory.EnumerateFiles(ExtensionsDirectory))
        {
            if (!IsExtensionArchive(path) ||
                !string.Equals(Path.GetFileNameWithoutExtension(path), extension.FolderName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), extension.ArchiveName, StringComparison.OrdinalIgnoreCase))
                continue;
            using var stream = File.OpenRead(path);
            removed[Path.GetFileName(path)] = Convert.ToHexString(SHA256.HashData(stream));
        }
        return removed;
    }
}
