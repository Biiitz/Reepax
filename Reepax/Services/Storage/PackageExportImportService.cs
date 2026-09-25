using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Reepax.Models;
using Reepax.Services.Extractor;
using Reepax.Services.Localization;
using Reepax.ViewModels;

namespace Reepax.Services.Storage;

public class ReepaxPackageItemDto
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("originalUrl")]
    public string OriginalUrl { get; set; } = string.Empty;

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("hosterName")]
    public string HosterName { get; set; } = "Unknown";

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;
}

public class ReepaxPackageDto
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "repx";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("subDirectory")]
    public string? SubDirectory { get; set; }

    [JsonPropertyName("autoExtractArchives")]
    public bool AutoExtractArchives { get; set; }

    [JsonPropertyName("lowResourceExtraction")]
    public bool LowResourceExtraction { get; set; }

    [JsonPropertyName("deleteArchiveAfterExtraction")]
    public bool DeleteArchiveAfterExtraction { get; set; }

    [JsonPropertyName("moveArchiveToRecycleBin")]
    public bool MoveArchiveToRecycleBin { get; set; }

    [JsonPropertyName("autoResolveHostLinks")]
    public bool AutoResolveHostLinks { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonPropertyName("items")]
    public List<ReepaxPackageItemDto> Items { get; set; } = new();
}

/// <summary>
/// Service for exporting and importing download packages as .repx files.
/// Additionally supports reading legacy .sdlr files for backwards compatibility.
/// Guarantees that only pure metadata (links, names, sizes, settings) are stored
/// without transferring download progress or runtime state.
/// </summary>
public static class PackageExportImportService
{
    public const string FileExtension = ".repx";
    public const string LegacyFileExtension = ".sdlr";
    public const string FormatIdentifier = "repx";
    public const string LegacyFormatIdentifier = "sdlr";

    public static bool IsSupportedPackageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(LegacyFileExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a clean DTO from a package, free of progress, speed, or runtime state.
    /// </summary>
    public static ReepaxPackageDto CreateDto(DownloadPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var dto = new ReepaxPackageDto
        {
            Format = FormatIdentifier,
            Version = 1,
            Name = package.Name,
            SubDirectory = !string.IsNullOrWhiteSpace(package.SaveDirectory) ? Path.GetFileName(package.SaveDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : null,
            AutoExtractArchives = package.AutoExtractArchives,
            LowResourceExtraction = package.LowResourceExtraction,
            DeleteArchiveAfterExtraction = package.DeleteArchiveAfterExtraction,
            MoveArchiveToRecycleBin = package.MoveArchiveToRecycleBin,
            AutoResolveHostLinks = package.AutoResolveHostLinks,
            Items = new List<ReepaxPackageItemDto>()
        };

        foreach (var item in package.Items)
        {
            dto.Items.Add(new ReepaxPackageItemDto
            {
                FileName = item.FileName,
                OriginalUrl = item.OriginalUrl,
                TotalBytes = item.TotalBytes,
                HosterName = item.HosterName,
                IsEnabled = item.IsEnabled
            });
        }

        return dto;
    }

    /// <summary>
    /// Serializes package as formatted JSON string.
    /// </summary>
    public static string ExportToJson(DownloadPackage package)
    {
        var dto = CreateDto(package);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>
    /// Saves package to a file on disk.
    /// </summary>
    public static void ExportToFile(DownloadPackage package, string filePath)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));

        var json = ExportToJson(package);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Reads a package file and builds an unstarted DownloadPackage (0% progress).
    /// </summary>
    public static DownloadPackage ImportFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Package file not found: {filePath}", filePath);

        var json = File.ReadAllText(filePath);
        return ImportFromJson(json);
    }

    /// <summary>
    /// Parses JSON content and builds an unstarted DownloadPackage (0% progress).
    /// </summary>
    public static DownloadPackage ImportFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Package file content is empty.");

        var dto = JsonSerializer.Deserialize<ReepaxPackageDto>(json, JsonOptions);
        if (dto == null)
            throw new InvalidDataException("Failed to parse Reepax package data.");

        if (!string.Equals(dto.Format, FormatIdentifier, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(dto.Format, LegacyFormatIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Warn($"Package format identifier '{dto.Format}' is not standard, attempting compatibility load.");
        }

        var pkgName = !string.IsNullOrWhiteSpace(dto.Name) ? dto.Name : "Imported_Package";
        var pkg = new DownloadPackage
        {
            Id = Guid.NewGuid(),
            Name = pkgName,
            AutoExtractArchives = dto.AutoExtractArchives,
            LowResourceExtraction = dto.LowResourceExtraction,
            DeleteArchiveAfterExtraction = dto.DeleteArchiveAfterExtraction,
            MoveArchiveToRecycleBin = dto.MoveArchiveToRecycleBin,
            AutoResolveHostLinks = ResolveHostLinksOption(dto),
            Status = DownloadStatus.Queued,
            StatusMessage = Loc.Get("Status_Queued"),
            DownloadedBytes = 0,
            ProgressPercentage = 0.0,
            SpeedBytesPerSecond = 0,
            RemainingSeconds = 0,
            CompletedItemsCount = 0,
            IsNewlyCompleted = false,
            HasCompletedNotified = false,
            StartedAt = null,
            CompletedAt = null
        };

        // Zielverzeichnis berechnen
        var baseDownloadDir = SettingsService.Instance.Settings.DefaultDownloadDirectory;
        if (string.IsNullOrWhiteSpace(baseDownloadDir))
        {
            baseDownloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        var subDir = !string.IsNullOrWhiteSpace(dto.SubDirectory)
            ? PackageGrouper.MakeSafeDirectoryName(dto.SubDirectory)
            : PackageGrouper.MakeSafeDirectoryName(pkg.Name);

        pkg.SaveDirectory = Path.Combine(baseDownloadDir, subDir);

        if (dto.Items != null)
        {
            foreach (var itemDto in dto.Items)
            {
                var rawFileName = !string.IsNullOrWhiteSpace(itemDto.FileName)
                    ? itemDto.FileName
                    : (!string.IsNullOrWhiteSpace(itemDto.OriginalUrl) ? Path.GetFileName(itemDto.OriginalUrl) : "file.bin");
                var safeFileName = PackageGrouper.MakeSafeFileName(rawFileName ?? "file.bin");

                var hoster = !string.IsNullOrWhiteSpace(itemDto.HosterName) && itemDto.HosterName != "Unknown"
                    ? itemDto.HosterName
                    : (!string.IsNullOrWhiteSpace(itemDto.OriginalUrl) ? HosterInfo.DetectHoster(itemDto.OriginalUrl).DisplayName : "Unknown");

                var item = new DownloadItem
                {
                    Id = Guid.NewGuid(),
                    PackageId = pkg.Id,
                    OriginalUrl = itemDto.OriginalUrl ?? string.Empty,
                    FileName = safeFileName,
                    HosterName = hoster,
                    TotalBytes = Math.Max(0, itemDto.TotalBytes),
                    DownloadedBytes = 0,
                    ProgressPercentage = 0.0,
                    SpeedBytesPerSecond = 0,
                    RemainingSeconds = 0,
                    Status = DownloadStatus.Queued,
                    StatusMessage = Loc.Get("Status_Queued"),
                    IsEnabled = itemDto.IsEnabled,
                    SaveFilePath = Path.Combine(pkg.SaveDirectory, safeFileName)
                };

                pkg.Items.Add(item);
            }
        }

        pkg.EnsureNextTaskSteps();
        pkg.RecalculateAggregates();
        return pkg;
    }

    /// <summary>
    /// Imports a package asynchronously and adds it to MainViewModel (including UI thread handling).
    /// </summary>
    public static async Task<DownloadPackage?> ImportAndAddAsync(string filePath, MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            var pkg = await Task.Run(() => ImportFromFile(filePath));
            if (pkg == null) return null;

            void AddToUi()
            {
                viewModel.Packages.Add(pkg);
                viewModel.RecalculateGlobalStats();
                viewModel.StatusSummary = Loc.Format("Status_PackageImported", pkg.Name);

                // If file sizes are still 0, resolve asynchronously
                if (pkg.Items.Any(i => i.TotalBytes <= 0))
                {
                    _ = viewModel.ResolvePackageSizesAsync(new[] { pkg });
                }
            }

            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                await Application.Current.Dispatcher.InvokeAsync(AddToUi);
            }
            else
            {
                AddToUi();
            }

            return pkg;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to import package from {filePath}", ex);
            viewModel.StatusSummary = Loc.Format("Dialog_ImportErrorMessage", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Opens the Save dialog and exports the specified package.
    /// </summary>
    public static bool ExportWithDialog(DownloadPackage package, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (package.Status == DownloadStatus.Completed)
        {
            if (!DownloadPersistenceService.IsTestEnvironment)
            {
                var targetOwner = owner ?? Application.Current?.MainWindow;
                if (targetOwner != null)
                {
                    MessageBox.Show(
                        targetOwner,
                        Loc.Get("Dialog_ExportAlreadyCompletedMessage"),
                        Loc.Get("Dialog_ExportPackageTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        Loc.Get("Dialog_ExportAlreadyCompletedMessage"),
                        Loc.Get("Dialog_ExportPackageTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            return false;
        }

        var defaultName = PackageGrouper.MakeSafeFileName(package.Name);
        if (string.IsNullOrWhiteSpace(defaultName) || defaultName == "file.bin")
            defaultName = "Package";

        var saveDialog = new SaveFileDialog
        {
            Title = Loc.Get("Dialog_ExportPackageTitle"),
            Filter = Loc.Get("Dialog_PackageFilter"),
            FileName = $"{defaultName}.repx",
            DefaultExt = ".repx",
            AddExtension = true
        };

        var result = owner != null ? saveDialog.ShowDialog(owner) : saveDialog.ShowDialog();
        if (result == true)
        {
            try
            {
                ExportToFile(package, saveDialog.FileName);
                AppLogger.Info($"Exported package '{package.Name}' to '{saveDialog.FileName}'");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to export package to {saveDialog.FileName}", ex);
                if (!DownloadPersistenceService.IsTestEnvironment)
                {
                    var targetOwner = owner ?? Application.Current?.MainWindow;
                    if (targetOwner != null)
                    {
                        MessageBox.Show(
                            targetOwner,
                            Loc.Format("Dialog_ExportErrorMessage", ex.Message),
                            Loc.Get("Dialog_ExportPackageTitle"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    else
                    {
                        MessageBox.Show(
                            Loc.Format("Dialog_ExportErrorMessage", ex.Message),
                            Loc.Get("Dialog_ExportPackageTitle"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
                return false;
            }
        }

        return false;
    }

    private static bool ResolveHostLinksOption(ReepaxPackageDto dto)
    {
        return dto.AutoResolveHostLinks;
    }
}
