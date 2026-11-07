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

    private AudioNotificationPlayback? _playback;

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
            : SoundCatalog.Default.DefaultSound;

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
            : SoundCatalog.Default.DefaultSound;

        PlaySoundInternal("sounds_error", fileName, settings.ErrorSoundVolume, isPreview);
    }

    /// <summary>
    /// Plays a preview of a specific sound at the specified volume.
    /// </summary>
    public void PlayPreview(string folder, string fileName, int? volume = null, bool updateVolumeOnlyIfPlaying = false)
    {
        int defaultVol = folder.Contains("error", StringComparison.OrdinalIgnoreCase)
            ? SettingsService.Instance.Settings.ErrorSoundVolume
            : SettingsService.Instance.Settings.CompletionSoundVolume;
        int effectiveVolume = volume ?? defaultVol;
        PlaySoundInternal(folder, fileName, effectiveVolume, isPreview: true, updateVolumeOnlyIfPlaying);
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

        if (folder is not ("sounds" or "sounds_done" or "sounds_error")) return null;
        return SoundCatalog.Default.Resolve(fileName);
    }

    public static double ConvertToMediaPlayerVolume(int volumePercent)
    {
        return Math.Clamp((volumePercent * 0.99) / 100.0, 0.0, 0.99);
    }

    internal static string? ResolvePlayableSoundPath(string folder, string fileName)
    {
        foreach (var candidate in new[] { fileName, SoundCatalog.Default.DefaultSound })
        {
            try
            {
                var path = ResolveSoundPath(folder, candidate);
                if (path != null && File.Exists(path)) return path;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    private void PlaySoundInternal(string folder, string fileName, int volumePercent, bool isPreview, bool updateVolumeOnlyIfPlaying = false)
    {
        string? resolvedPath = ResolvePlayableSoundPath(folder, fileName);
        if (resolvedPath == null)
        {
            AppLogger.Warn("[Audio] The configured sound asset is unavailable.");
            return;
        }

        if (DownloadPersistenceService.IsTestEnvironment)
        {
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
                    if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return;
                    if (_playback == null)
                    {
                        _playback = new AudioNotificationPlayback(() => new MediaAudioVoice());
                        app.Dispatcher.ShutdownStarted += (_, _) => _playback.Dispose();
                    }
                    var source = new Uri(resolvedPath, UriKind.Absolute);
                    double volume = ConvertToMediaPlayerVolume(volumePercent);
                    if (isPreview) _playback.PlayPreview(source, volume, updateVolumeOnlyIfPlaying);
                    else _playback.PlayNotification(folder, source, volume, () =>
                    {
                        var settings = SettingsService.Instance.Settings;
                        bool error = string.Equals(folder, "sounds_error", StringComparison.Ordinal);
                        bool enabled = error ? settings.EnableErrorSound : settings.EnableCompletionSound;
                        return enabled ? ConvertToMediaPlayerVolume(error ? settings.ErrorSoundVolume : settings.CompletionSoundVolume) : 0;
                    });
                }
                catch (Exception)
                {
                    AppLogger.Warn("[Audio] Could not play an audio notification.");
                }
            }));
        }
        catch (Exception)
        {
            AppLogger.Warn("[Audio] Could not dispatch audio playback.");
        }
    }

    private sealed class MediaAudioVoice : IAudioNotificationVoice
    {
        private readonly MediaPlayer _player = new();
        public event Action? Opened;
        public event Action? Ended;
        public event Action? Failed;
        public double Volume { get => _player.Volume; set => _player.Volume = value; }

        internal MediaAudioVoice()
        {
            _player.MediaOpened += OnOpened;
            _player.MediaEnded += OnEnded;
            _player.MediaFailed += OnFailed;
        }

        private void OnOpened(object? sender, EventArgs args) => Opened?.Invoke();
        private void OnEnded(object? sender, EventArgs args) => Ended?.Invoke();
        private void OnFailed(object? sender, ExceptionEventArgs args) => Failed?.Invoke();
        public void Open(Uri source) => _player.Open(source);
        public void Play() => _player.Play();

        public void Dispose()
        {
            _player.MediaOpened -= OnOpened;
            _player.MediaEnded -= OnEnded;
            _player.MediaFailed -= OnFailed;
            _player.Close();
        }
    }
}
