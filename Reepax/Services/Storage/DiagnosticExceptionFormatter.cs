using System;
using System.Collections.Generic;
using System.Text;

namespace Reepax.Services.Storage;

internal static class DiagnosticExceptionFormatter
{
    internal static void Append(StringBuilder output, Exception exception)
    {
        var pending = new Queue<(Exception Error, int Depth)>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue((exception, 0));
        int count = 0;
        while (pending.Count > 0 && count < 64)
        {
            var (error, depth) = pending.Dequeue();
            if (!visited.Add(error)) continue;
            count++;
            string label = depth == 0 ? "Exception:    " : $"Inner Exception [{depth}]: ";
            output.AppendLine($"{label}{error.GetType().FullName}: {error.Message}");
            output.AppendLine($"HResult: 0x{error.HResult:X8}");
            output.AppendLine("Stack Trace:");
            output.AppendLine(error.StackTrace ?? "  (No stack trace available)");
            if (error is AggregateException aggregate)
            {
                foreach (var child in aggregate.InnerExceptions) pending.Enqueue((child, depth + 1));
            }
            else if (error.InnerException != null) pending.Enqueue((error.InnerException, depth + 1));
        }
        if (pending.Count > 0) output.AppendLine("Additional exception details omitted after the diagnostic safety limit.");
    }
}
