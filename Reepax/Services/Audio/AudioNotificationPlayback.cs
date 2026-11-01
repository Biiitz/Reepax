using System;
using System.Collections.Generic;
using Reepax.Services.Storage;

namespace Reepax.Services.Audio;

internal interface IAudioNotificationVoice : IDisposable
{
    event Action? Opened;
    event Action? Ended;
    event Action? Failed;
    double Volume { get; set; }
    void Open(Uri source);
    void Play();
}

internal sealed class AudioNotificationPlayback(Func<IAudioNotificationVoice> createVoice, Func<long>? timestamp = null) : IDisposable
{
    private sealed record Request(string Category, Uri Source, double Volume, Func<double>? CurrentVolume = null);
    private sealed class Playback(IAudioNotificationVoice voice, Request request)
    {
        internal IAudioNotificationVoice Voice { get; } = voice;
        internal Request Request { get; } = request;
        internal double Volume = request.Volume;
        internal bool Started;
        internal Action Opened = null!;
        internal Action Ended = null!;
        internal Action Failed = null!;
    }

    private readonly Func<long> _timestamp = timestamp ?? (() => Environment.TickCount64);
    private readonly Dictionary<string, long> _lastNotificationTimes = new(StringComparer.Ordinal);
    private readonly LinkedList<Request> _pending = new();
    private Playback? _notification;
    private Playback? _preview;
    private bool _disposed;

    internal void PlayNotification(string category, Uri source, double volume, Func<double>? currentVolume = null)
    {
        if (_disposed || volume <= 0) return;
        long now = _timestamp();
        if (_lastNotificationTimes.TryGetValue(category, out long previous) && now - previous < 500) return;
        _lastNotificationTimes[category] = now;
        var request = new Request(category, source, volume, currentVolume);
        if (_notification == null) Start(request, preview: false);
        else
        {
            for (var node = _pending.First; node != null; node = node.Next)
            {
                if (node.Value.Category != category) continue;
                node.Value = request;
                return;
            }
            // Coalesce bursts instead of interrupting playback or accumulating a long queue.
            if (_pending.Count >= 2) _pending.RemoveFirst();
            _pending.AddLast(request);
        }
    }

    internal void PlayPreview(Uri source, double volume, bool updateVolumeOnlyIfPlaying)
    {
        if (_disposed) return;
        if (updateVolumeOnlyIfPlaying && _preview is { } active && active.Request.Source == source)
        {
            active.Volume = volume;
            try { active.Voice.Volume = volume; }
            catch (Exception) { Finish(active, failed: true); }
            return;
        }
        var previous = _preview;
        _preview = null;
        Release(previous);
        if (volume > 0) Start(new Request("preview", source, volume), preview: true);
    }

    private bool IsCurrent(Playback playback) => ReferenceEquals(_notification, playback) || ReferenceEquals(_preview, playback);

    private void Start(Request request, bool preview)
    {
        Playback? playback = null;
        try
        {
            double volume = request.CurrentVolume?.Invoke() ?? request.Volume;
            if (volume <= 0)
            {
                if (!preview) StartNext();
                return;
            }
            playback = new Playback(createVoice(), request);
            playback.Volume = volume;
            var current = playback;
            playback.Opened = () =>
            {
                if (!IsCurrent(current) || current.Started) return;
                try
                {
                    current.Volume = current.Request.CurrentVolume?.Invoke() ?? current.Volume;
                    if (current.Volume <= 0)
                    {
                        Finish(current, failed: false);
                        return;
                    }
                    current.Started = true;
                    current.Voice.Volume = current.Volume;
                    current.Voice.Play();
                }
                catch (Exception) { Finish(current, failed: true); }
            };
            playback.Ended = () => Finish(current, failed: false);
            playback.Failed = () => Finish(current, failed: true);
            if (preview) _preview = playback;
            else _notification = playback;
            playback.Voice.Opened += playback.Opened;
            playback.Voice.Ended += playback.Ended;
            playback.Voice.Failed += playback.Failed;
            playback.Voice.Volume = playback.Volume;
            playback.Voice.Open(request.Source);
        }
        catch (Exception)
        {
            if (playback != null) Finish(playback, failed: true);
            else
            {
                AppLogger.Warn("[Audio] Could not create an audio player.");
                if (!preview) StartNext();
            }
        }
    }

    private void Finish(Playback playback, bool failed)
    {
        if (!IsCurrent(playback)) return;
        bool notification = ReferenceEquals(_notification, playback);
        if (notification) _notification = null;
        else _preview = null;
        Release(playback);
        if (failed) AppLogger.Warn("[Audio] Audio playback failed.");
        if (notification) StartNext();
    }

    private void StartNext()
    {
        if (_disposed || _pending.First == null) return;
        var request = _pending.First.Value;
        _pending.RemoveFirst();
        Start(request, preview: false);
    }

    private static void Release(Playback? playback)
    {
        if (playback == null) return;
        playback.Voice.Opened -= playback.Opened;
        playback.Voice.Ended -= playback.Ended;
        playback.Voice.Failed -= playback.Failed;
        try { playback.Voice.Dispose(); }
        catch (Exception) { AppLogger.Warn("[Audio] Could not release an audio player."); }
    }

    public void Dispose()
    {
        _disposed = true;
        _pending.Clear();
        var notification = _notification;
        var preview = _preview;
        _notification = _preview = null;
        Release(notification);
        Release(preview);
    }
}
