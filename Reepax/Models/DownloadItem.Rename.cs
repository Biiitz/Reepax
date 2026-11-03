using System;
using System.Collections.Generic;
using System.IO;
using Reepax.Services.Download;
using Reepax.Services.Localization;
using Reepax.Services.Storage;

namespace Reepax.Models;

public partial class DownloadItem
{
    internal Action<string, string> RenameFile { get; set; } = File.Move;

    internal bool IsFileRenameBusy => IsUnavailableForProcessing || IsVerifyingIntegrity ||
        Status is DownloadStatus.Downloading or DownloadStatus.InBrowser or DownloadStatus.InBrowserSlot1 or
            DownloadStatus.InBrowserSlot2 or DownloadStatus.SolvingCaptcha or DownloadStatus.WaitingForBrowser ||
        DownloadEngine.Instance.IsDownloading(Id);

    public bool Rename(string newName, bool isUserAction = true)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return false;
        string name = newName.Trim();
        if (name is "." or ".." || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        if (isUserAction && IsFileRenameBusy)
            return false;

        string? target = SaveFilePath;
        try
        {
            string? directory = Path.GetDirectoryName(SaveFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                target = Path.Combine(directory, name);
            if (!string.IsNullOrWhiteSpace(SaveFilePath) && !string.IsNullOrWhiteSpace(target) &&
                !string.Equals(SaveFilePath, target, StringComparison.Ordinal) && !TryRenameFiles(SaveFilePath, target))
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLogger.Warn("[DownloadItem] File rename could not be prepared.");
            return false;
        }

        // Publish the new identity only after every associated file has moved.
        SaveFilePath = target;
        if (isUserAction) IsCustomName = true;
        FileName = name;
        DownloadPersistenceService.Instance.RequestSave();
        return true;
    }

    private bool TryRenameFiles(string source, string target)
    {
        var moves = new List<(string Source, string Target)>();
        bool caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        foreach (string suffix in new[] { ".part", ".part.segments", "" })
        {
            string from = source + suffix;
            string to = target + suffix;
            if ((!caseOnly && Path.Exists(to)) || Directory.Exists(from))
                return false;
            if (File.Exists(from)) moves.Add((from, to));
        }

        int moved = 0;
        try
        {
            // Keep the completed file at its original location until sidecars are ready.
            foreach (var move in moves)
            {
                RenameFile(move.Source, move.Target);
                moved++;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bool restored = true;
            for (int index = moved - 1; index >= 0; index--)
            {
                try { RenameFile(moves[index].Target, moves[index].Source); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    restored = false;
                }
            }
            if (!restored)
            {
                // Preserve both locations and prevent partially restored data from appearing verified.
                Status = DownloadStatus.Failed;
                CalculatedChecksum = null;
                ErrorMessage = Loc.Get("Status_ItemRenameRecoveryRequired");
                StatusMessage = ErrorMessage;
                DownloadPersistenceService.Instance.RequestSave();
            }
            AppLogger.Warn(restored ? "[DownloadItem] File rename failed; original paths were restored."
                : "[DownloadItem] File rename rollback was incomplete; all remaining files were preserved.");
            return false;
        }
    }
}
