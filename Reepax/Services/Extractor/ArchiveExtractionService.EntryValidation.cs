using System;
using System.IO;
using System.Linq;

namespace Reepax.Services.Extractor;

public partial class ArchiveExtractionService
{
    internal static string GetSafeArchiveDestination(string target, string key)
    {
        if (Path.IsPathRooted(key) || key.Contains(':') || key.Split(new[] { '/', '\\' }).Any(part =>
            part == ".." || part.EndsWith(' ') || (part != "." && part.EndsWith('.'))))
            throw new InvalidDataException("Invalid archive destination.");
        string destination = Path.GetFullPath(Path.Combine(target, key));
        string prefix = Path.EndsInDirectorySeparator(target) ? target : target + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archive entry escapes its destination.");
        return destination;
    }
}
