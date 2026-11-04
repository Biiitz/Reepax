using System.Collections.Concurrent;
using Reepax.Services.Storage;
using Reepax.Services.SystemIntegration;

namespace Reepax.Tests;

internal static class SingleInstanceIpcConcurrencyWorker
{
    internal static async Task<int> RunAsync(int count)
    {
        try
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);
            DownloadPersistenceService.IsTestEnvironment = true;
            AppLogger.IsLoggingEnabled = false;
            ThreadPool.GetMaxThreads(out _, out int ioThreads);
            if (!ThreadPool.SetMinThreads(2, 2) || !ThreadPool.SetMaxThreads(2, ioThreads))
                throw new InvalidOperationException("The isolated worker could not limit its thread pool.");
            using var service = new SingleInstanceService("InstanceContention_" + Guid.NewGuid().ToString("N"), 1500);
            var received = new ConcurrentQueue<string>();
            service.StartIpcServer(args => { foreach (string arg in args) received.Enqueue(arg); });
            for (int round = 0; round < 2; round++)
            {
                string[] expected = Enumerable.Range(0, count).Select(index => $"sample-{round}-{index}.repx").ToArray();
                bool[] accepted = await Task.WhenAll(expected.Select(arg => service.SendArgsToPrimaryAsync([arg])));
                if (accepted.Any(result => !result)) throw new InvalidOperationException("A concurrent launch was not acknowledged.");
                string[] actual = received.ToArray();
                if (actual.Length != expected.Length || !actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
                    throw new InvalidOperationException("Concurrent arguments were lost or duplicated.");
                received.Clear();
            }
            service.Dispose();
            await service.IpcServerCompletion;
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + DiagnosticPrivacy.Sanitize(error.Message));
            return 1;
        }
    }
}
