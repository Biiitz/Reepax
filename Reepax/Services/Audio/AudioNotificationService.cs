using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Reepax.Services.Storage;

namespace Reepax.Services.Audio;

/// <summary>
/// Singleton service for audio notifications (download completed, download failed, and sound previews).
/// Utilizes WPF's native MediaPlayer with volume control, running safely on the UI dispatcher.
/// </summary>
public sealed class AudioNotificationService
{
    private static readonly Lazy<AudioNotificationService> _instance =
        new(() => new AudioNotificationService());

    public static AudioNotificationService Instance => _instance.Value;

    private MediaPlayer? _mediaPlayer;
    private readonly object _lock = new();
    private DateTime _lastSoundPlayedTime = DateTime.MinValue;
    private const int MinMillisecondsBetweenSounds = 500;

    private AudioNotificationService()
    {
    }

    /// <summary>
    /// Plays the configured download completion sound if enabled in settings.
    /// </summary>
    public void PlayCompletionSound(bool isPreview = false)
    {
        var settings = SettingsService.Instance.Settings;
        if (!isPreview && !settings.EnableCompletionSound)
        {
            return;
        }

        string fileName = !string.IsNullOrWhiteSpace(settings.SelectedCompletionSound)
            ? settings.SelectedCompletionSound
            : "1.mp3";

        PlaySoundInternal("sounds_done", fileName, settings.CompletionSoundVolume, isPreview);
    }

    /// <summary>
    /// Plays the configured download failure sound if enabled in settings.
    /// </summary>
    public void PlayErrorSound(bool isPreview = false)
    {
        var settings = SettingsService.Instance.Settings;
        if (!isPreview && !settings.EnableErrorSound)
        {
            return;
        }

        string fileName = !string.IsNullOrWhiteSpace(settings.SelectedErrorSound)
            ? settings.SelectedErrorSound
            : "1.mp3";

        PlaySoundInternal("sounds_error", fileName, settings.ErrorSoundVolume, isPreview);
    }

    /// <summary>
    /// Plays a preview of a specific sound at the specified volume.
    /// </summary>
    public void PlayPreview(string folder, string fileName, int? volume = null)
    {
        int defaultVol = folder.Contains("error", StringComparison.OrdinalIgnoreCase)
            ? SettingsService.Instance.Settings.ErrorSoundVolume
            : SettingsService.Instance.Settings.CompletionSoundVolume;
        int effectiveVolume = volume ?? defaultVol;
        PlaySoundInternal(folder, fileName, effectiveVolume, isPreview: true);
    }

    /// <summary>
    /// Resolves the physical path of an audio asset from the BaseDirectory or fallback locations.
    /// </summary>
    public static string? ResolveSoundPath(string folder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", folder, fileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Reepax", "Assets", folder, fileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", folder, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Reepax", "Assets", folder, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", folder, fileName)
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
            catch
            {
                // Ignore path resolution errors for invalid candidates
            }
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", folder, fileName);
    }

    private void PlaySoundInternal(string folder, string fileName, int volumePercent, bool isPreview)
    {
        lock (_lock)
        {
            if (!isPreview)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastSoundPlayedTime).TotalMilliseconds < MinMillisecondsBetweenSounds)
                {
                    return; // Throttle consecutive event-driven sounds to avoid audio overlap
                }
                _lastSoundPlayedTime = now;
            }
        }

        string? resolvedPath = ResolveSoundPath(folder, fileName);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            AppLogger.Warn($"Audio notification file not found: folder '{folder}', file '{fileName}'");
            return;
        }

        var app = Application.Current;
        if (app?.Dispatcher == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
        {
            // Headless context or application shutting down
            return;
        }

        try
        {
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_mediaPlayer == null)
                    {
                        _mediaPlayer = new MediaPlayer();
                    }
                    else
                    {
                        _mediaPlayer.Stop();
                        _mediaPlayer.Close();
                    }

                    double clampedVolume = Math.Clamp(volumePercent / 100.0, 0.0, 1.0);
                    _mediaPlayer.Volume = clampedVolume;
                    _mediaPlayer.Open(new Uri(resolvedPath, UriKind.Absolute));
                    _mediaPlayer.Play();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to play audio notification: {ex.Message}");
                }
            }));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to dispatch audio playback: {ex.Message}");
        }
    }
}
