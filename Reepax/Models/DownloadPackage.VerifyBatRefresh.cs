using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Reepax.Services.Storage;

namespace Reepax.Models;

public partial class DownloadPackage
{
    private long _verifyBatScanVersion;

    private sealed record VerifyBatSearchSnapshot(long Version, string SaveDirectory, string Name, string?[] ItemPaths);

    internal async Task RefreshVerifyBatFileAsync(Func<string?>? searchOverride = null, Dispatcher? dispatcherOverride = null)
    {
        var dispatcher = dispatcherOverride;
        if (dispatcher == null && !DownloadPersistenceService.IsTestEnvironment)
            dispatcher = Application.Current?.Dispatcher;
        if (dispatcher?.Thread?.IsAlive != true || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            dispatcher = null;

        while (true)
        {
            var snapshot = await InvokeVerifyUiAsync(dispatcher, () =>
            {
                long version = Interlocked.Increment(ref _verifyBatScanVersion);
                if (IsExtracting || !CanSearchForVerifyBatFile())
                {
                    SetVerifyBatFile(null, false);
                    return null;
                }

                return new VerifyBatSearchSnapshot(version, SaveDirectory, Name,
                    Items.Select(item => item.SaveFilePath).ToArray());
            }).ConfigureAwait(false);

            if (snapshot == null)
                return;

            (string? Path, bool Exists) result;
            try
            {
                var search = searchOverride;
                searchOverride = null;
                result = await Task.Run(() =>
                {
                    var path = search == null
                        ? FindVerifyBatFilePath(snapshot.SaveDirectory, snapshot.Name, snapshot.ItemPaths)
                        : search();
                    return (path, !string.IsNullOrEmpty(path) && File.Exists(path));
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("[DownloadPackage] Verification file search failed", ex);
                result = (null, false);
            }

            bool retry = await InvokeVerifyUiAsync(dispatcher, () =>
            {
                if (snapshot.Version != Interlocked.Read(ref _verifyBatScanVersion))
                    return false;

                if (IsExtracting || !CanSearchForVerifyBatFile())
                {
                    SetVerifyBatFile(null, false);
                    return false;
                }

                if (snapshot.SaveDirectory != SaveDirectory || snapshot.Name != Name ||
                    !snapshot.ItemPaths.SequenceEqual(Items.Select(item => item.SaveFilePath)))
                {
                    SetVerifyBatFile(null, false);
                    return true;
                }

                SetVerifyBatFile(result.Path, result.Exists);
                return false;
            }).ConfigureAwait(false);

            if (!retry)
                return;
        }
    }

    private static Task<T> InvokeVerifyUiAsync<T>(Dispatcher? dispatcher, Func<T> action)
    {
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(action).Task;

        return Task.FromResult(action());
    }
}
