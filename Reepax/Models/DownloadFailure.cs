using System.IO;
using System.Net.Http;
using Reepax.Services.Download;
using Reepax.Services.Localization;

namespace Reepax.Models;

public enum DownloadFailureKind
{
    Unknown, HtmlResponse, ChecksumMismatch, SizeMismatch, InvalidArchive,
    Http, FileAccess, MissingFile, EmptyFile, DestinationExists
}

public sealed class DownloadFailure
{
    public DownloadFailureKind Kind { get; set; }
    public int? HttpStatusCode { get; set; }
    public long ExpectedBytes { get; set; }
    public long ActualBytes { get; set; }
    public int RetryAttempt { get; set; }
    public int RetryLimit { get; set; }
    public bool RetryBlocked { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Message => Kind switch
    {
        DownloadFailureKind.HtmlResponse => Loc.Get("Validation_HtmlErrorPageDetected"),
        DownloadFailureKind.ChecksumMismatch => Loc.Get("DownloadFailure_Checksum"),
        DownloadFailureKind.SizeMismatch => Loc.Format("Validation_FileSizeMismatch", ExpectedBytes, ActualBytes),
        DownloadFailureKind.InvalidArchive => Loc.Get("DownloadFailure_Archive"),
        DownloadFailureKind.Http when HttpStatusCode is >= 100 and <= 599 => Loc.Format("DownloadFailure_HttpStatus", HttpStatusCode.Value),
        DownloadFailureKind.Http => Loc.Get("DownloadFailure_Network"),
        DownloadFailureKind.FileAccess => Loc.Get("DownloadFailure_FileAccess"),
        DownloadFailureKind.MissingFile => Loc.Get("DownloadFailure_MissingFile"),
        DownloadFailureKind.EmptyFile => Loc.Get("Validation_FileIsEmpty"),
        DownloadFailureKind.DestinationExists => Loc.Get("Error_DownloadDestinationExists"),
        _ => Loc.Get("DownloadFailure_Unknown")
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessage => RetryBlocked ? Loc.Get("Status_IntegrityRetryBlocked")
        : RetryAttempt > 0 ? Loc.Format("Status_RetryingAfterIntegrityError", RetryAttempt, RetryLimit, Message)
        : Message;

    public static DownloadFailure FromException(Exception error) => error switch
    {
        DownloadDestinationExistsException => new() { Kind = DownloadFailureKind.DestinationExists },
        DownloadRetryBlockedException => new() { RetryBlocked = true },
        DownloadIntegrityException integrity => new() { Kind = integrity.Kind },
        HttpRequestException http => new() { Kind = DownloadFailureKind.Http, HttpStatusCode = (int?)http.StatusCode },
        FileNotFoundException or DirectoryNotFoundException => new() { Kind = DownloadFailureKind.MissingFile },
        UnauthorizedAccessException => new() { Kind = DownloadFailureKind.FileAccess },
        IOException io when (io.HResult & 0xffff) is 32 or 33 => new() { Kind = DownloadFailureKind.FileAccess },
        _ => new() { Kind = DownloadFailureKind.Unknown }
    };
}
