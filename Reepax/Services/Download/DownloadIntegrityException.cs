using System;
using System.IO;

namespace Reepax.Services.Download;

internal sealed class DownloadIntegrityException(string message, Exception innerException,
    Models.DownloadFailureKind kind = Models.DownloadFailureKind.ChecksumMismatch) : IOException(message, innerException)
{
    internal Models.DownloadFailureKind Kind { get; } = kind;
}

internal sealed class DownloadDestinationExistsException() : IOException("The download destination already exists.");

internal sealed class DownloadRetryBlockedException() : IOException("Rejected download data could not be reset safely.");
