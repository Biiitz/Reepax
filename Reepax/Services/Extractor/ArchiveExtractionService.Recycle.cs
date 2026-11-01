using System;
using System.Collections.Generic;
using System.IO;
using Reepax.Services.SystemIntegration;

namespace Reepax.Services.Extractor;

public partial class ArchiveExtractionService
{
    internal Action<IReadOnlyCollection<string>> RecyclePaths { get; set; } = RecycleBinService.Move;

    public static void DeleteToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        FileAttributes attributes;
        try { attributes = File.GetAttributes(fullPath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.Directory) != 0 && ViewModels.MainViewModel.IsProtectedDirectory(fullPath))
            throw new IOException("The selected directory cannot be recycled safely.");

        // The backend rejects permanent deletion; failures must reach the caller unchanged.
        Instance.RecyclePaths(new[] { fullPath });
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException("The selected path was not moved to the Recycle Bin.");
    }
}
