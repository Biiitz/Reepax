using System;
using System.IO;

namespace Reepax.Services.Download;

internal sealed class DownloadIntegrityException(string message, Exception innerException) : IOException(message, innerException);
