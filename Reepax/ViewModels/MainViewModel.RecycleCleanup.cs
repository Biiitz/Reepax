using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reepax.Services.SystemIntegration;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    internal Action<IReadOnlyCollection<string>> RecyclePaths { get; set; } = RecycleBinService.Move;

    private sealed partial class CleanupSnapshot
    {
        internal void Recycle(Action<IReadOnlyCollection<string>> recycle)
        {
            EnsureRecoveredPathsConfirmed();
            if (Files.Count > 0)
                recycle(Files);
            if (Files.Any(Path.Exists))
                throw new IOException("Some selected files could not be recycled.");

            // An empty-directory removal cannot take files added after the scan.
            RemoveEmptyCleanupDirectories(Directories);
        }
    }
}
