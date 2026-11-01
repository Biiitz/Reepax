using System.IO;
using Reepax.Services.Extractor;

namespace Reepax.Tests;

internal sealed class ArchiveRecycleTestScope : IDisposable
{
    private readonly Action<IReadOnlyCollection<string>> _previous = ArchiveExtractionService.Instance.RecyclePaths;
    private readonly DirectoryInfo _bin = Directory.CreateTempSubdirectory("ArchiveRecycle_");
    internal List<string> RecycledPaths { get; } = new();
    internal string DirectoryPath => _bin.FullName;

    internal ArchiveRecycleTestScope() => ArchiveExtractionService.Instance.RecyclePaths = Move;

    private void Move(IReadOnlyCollection<string> paths)
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        foreach (string path in paths)
        {
            if (!Path.GetFullPath(path).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected recycle test source.");
            string destination = Path.Combine(_bin.FullName, RecycledPaths.Count.ToString());
            if (Directory.Exists(path)) Directory.Move(path, destination);
            else File.Move(path, destination);
            RecycledPaths.Add(destination);
        }
    }

    public void Dispose()
    {
        ArchiveExtractionService.Instance.RecyclePaths = _previous;
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(_bin.Parent?.FullName, temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected recycle test directory.");
        _bin.Delete(recursive: true);
    }
}
