namespace Reepax.Models;

public partial class DownloadItem
{
    public DownloadFailure? FailureDetails { get; set; }

    internal void SetDownloadFailure(DownloadFailure failure)
    {
        FailureDetails = failure;
        LastVerificationError = failure.Message;
        StatusMessage = failure.StatusMessage;
        ErrorMessage = failure.Message;
    }

    internal void ClearDownloadFailure()
    {
        FailureDetails = null;
        ErrorMessage = null;
        LastVerificationError = null;
    }

    internal void RefreshDownloadFailure()
    {
        if (FailureDetails == null) return;
        ErrorMessage = FailureDetails.Message;
        if (Status == DownloadStatus.Failed ||
            (Status == DownloadStatus.Queued && FailureDetails.RetryAttempt > 0))
            StatusMessage = FailureDetails.StatusMessage;
    }

    internal void RestoreDownloadFailure(DownloadFailure? failure)
    {
        if (Status == DownloadStatus.Completed)
        {
            ClearDownloadFailure();
            return;
        }
        if (failure == null && !string.IsNullOrWhiteSpace(ErrorMessage))
            failure = new DownloadFailure();
        FailureDetails = failure;
        if (Status == DownloadStatus.Failed)
            SetDownloadFailure(failure ?? new DownloadFailure());
        else if (failure != null)
            ErrorMessage = failure.Message;
    }
}
