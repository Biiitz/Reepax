using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Audio;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class AudioNotificationTests
{
    [Theory]
    [InlineData("missing.mp3")]
    [InlineData("\0invalid.mp3")]
    public void UnavailableSound_UsesTheBundledDefaultWithoutThrowing(string fileName)
    {
        var resolved = AudioNotificationService.ResolvePlayableSoundPath("sounds_done", fileName);
        Assert.NotNull(resolved);
        Assert.Equal("1.mp3", Path.GetFileName(resolved));
        Assert.True(File.Exists(resolved));
        Assert.Null(AudioNotificationService.ResolvePlayableSoundPath("unavailable", fileName));
    }

    [Fact]
    public void Notifications_FinishBeforeTheNextSoundStartsAndIgnoreStaleEvents()
    {
        var voices = new List<FakeAudioVoice>();
        long now = 0;
        using var playback = CreatePlayback(voices, () => now);
        playback.PlayNotification("completion", SoundUri("first"), 0.8);
        var first = Assert.Single(voices);
        Assert.Equal(0, first.PlayCalls);
        var staleOpened = first.OpenedSnapshot;
        var staleEnded = first.EndedSnapshot;
        first.RaiseOpened();
        Assert.Equal(1, first.PlayCalls);
        now = 600;
        playback.PlayNotification("error", SoundUri("second"), 0.5);
        Assert.Single(voices);
        Assert.Equal(0, first.DisposeCalls);
        first.RaiseEnded();
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(2, voices.Count);
        var second = voices[1];
        Assert.Equal(SoundUri("second"), second.Source);
        staleOpened?.Invoke();
        staleEnded?.Invoke();
        Assert.Equal(0, second.DisposeCalls);
        second.RaiseOpened();
        second.RaiseOpened();
        Assert.Equal(1, second.PlayCalls);
        second.RaiseEnded();
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public void NotificationBursts_CoalesceWithoutDroppingErrorsOrCreatingAnUnboundedQueue()
    {
        var voices = new List<FakeAudioVoice>();
        long now = 0;
        using var playback = CreatePlayback(voices, () => now);
        playback.PlayNotification("completion", SoundUri("first"), 0.8);
        now = 100;
        playback.PlayNotification("error", SoundUri("error"), 0.6);
        for (int index = 0; index < 30; index++)
        {
            now += 600;
            playback.PlayNotification("completion", SoundUri("latest"), 0.8);
        }
        Assert.Single(voices);
        Assert.Equal(0, voices[0].DisposeCalls);
        voices[0].RaiseEnded();
        Assert.Equal(SoundUri("error"), voices[1].Source);
        voices[1].RaiseEnded();
        Assert.Equal(SoundUri("latest"), voices[2].Source);
        voices[2].RaiseEnded();
        Assert.Equal(3, voices.Count);
        Assert.All(voices, voice => Assert.Equal(1, voice.DisposeCalls));
    }

    [Fact]
    public void PreviewVolumeChanges_KeepPlaybackPositionAndDoNotInterruptNotifications()
    {
        var voices = new List<FakeAudioVoice>();
        using var playback = CreatePlayback(voices, () => 0);
        var source = SoundUri("preview");
        playback.PlayPreview(source, 0.8, updateVolumeOnlyIfPlaying: false);
        playback.PlayPreview(source, 0.3, updateVolumeOnlyIfPlaying: true);
        Assert.Single(voices);
        voices[0].RaiseOpened();
        Assert.Equal(0.3, voices[0].Volume);
        playback.PlayPreview(source, 0.2, updateVolumeOnlyIfPlaying: true);
        Assert.Equal(1, voices[0].OpenCalls);
        Assert.Equal(1, voices[0].PlayCalls);
        Assert.Equal(0.2, voices[0].Volume);
        playback.PlayNotification("completion", SoundUri("notification"), 0.6);
        voices[1].RaiseOpened();
        Assert.Equal(0, voices[0].DisposeCalls);
        playback.PlayPreview(SoundUri("replacement"), 0.4, updateVolumeOnlyIfPlaying: false);
        Assert.Equal(1, voices[0].DisposeCalls);
        Assert.Equal(0, voices[1].DisposeCalls);
        Assert.Equal(3, voices.Count);
    }

    [Fact]
    public void PlaybackFailureAndShutdown_ReleasePlayersAndPreventLatePlayback()
    {
        var voices = new List<FakeAudioVoice>();
        using var playback = CreatePlayback(voices, () => 0);
        playback.PlayNotification("completion", SoundUri("first"), 0.8);
        playback.PlayNotification("error", SoundUri("second"), 0.7);
        voices[0].RaiseFailed();
        Assert.Equal(1, voices[0].DisposeCalls);
        Assert.Equal(2, voices.Count);
        playback.PlayPreview(SoundUri("preview"), 0.5, updateVolumeOnlyIfPlaying: false);
        var lateOpened = voices[1].OpenedSnapshot;
        playback.Dispose();
        lateOpened?.Invoke();
        playback.PlayNotification("error", SoundUri("ignored"), 0.8);
        Assert.Equal(3, voices.Count);
        Assert.All(voices, voice => Assert.Equal(1, voice.DisposeCalls));
        Assert.Equal(0, voices[1].PlayCalls);
    }

    [Fact]
    public void QueuedNotifications_RespectUpdatedVolumeAndDisabledSounds()
    {
        var voices = new List<FakeAudioVoice>();
        long now = 0;
        double completionVolume = 0.8;
        double errorVolume = 0.7;
        using var playback = CreatePlayback(voices, () => now);
        playback.PlayNotification("completion", SoundUri("first"), completionVolume, () => completionVolume);
        now = 600;
        playback.PlayNotification("completion", SoundUri("muted"), completionVolume, () => completionVolume);
        playback.PlayNotification("error", SoundUri("error"), errorVolume, () => errorVolume);
        completionVolume = 0;
        errorVolume = 0.2;
        voices[0].RaiseEnded();
        Assert.Equal(2, voices.Count);
        Assert.Equal(SoundUri("error"), voices[1].Source);
        voices[1].RaiseOpened();
        Assert.Equal(0.2, voices[1].Volume);
        Assert.Equal(1, voices[1].PlayCalls);
    }

    private static Uri SoundUri(string name) => new("https://example.invalid/" + name + ".mp3");

    private static AudioNotificationPlayback CreatePlayback(List<FakeAudioVoice> voices, Func<long> timestamp) => new(() =>
    {
        var voice = new FakeAudioVoice();
        voices.Add(voice);
        return voice;
    }, timestamp);

    private sealed class FakeAudioVoice : IAudioNotificationVoice
    {
        public event Action? Opened;
        public event Action? Ended;
        public event Action? Failed;
        public double Volume { get; set; }
        internal Uri? Source;
        internal int OpenCalls;
        internal int PlayCalls;
        internal int DisposeCalls;
        internal Action? OpenedSnapshot;
        internal Action? EndedSnapshot;
        public void Open(Uri source)
        {
            Source = source;
            OpenCalls++;
            OpenedSnapshot = Opened;
            EndedSnapshot = Ended;
        }
        public void Play() => PlayCalls++;
        public void Dispose() => DisposeCalls++;
        internal void RaiseOpened() => Opened?.Invoke();
        internal void RaiseEnded() => Ended?.Invoke();
        internal void RaiseFailed() => Failed?.Invoke();
    }

    [Theory]
    [InlineData("sounds_done", "1.mp3")]
    [InlineData("sounds_done", "2.mp3")]
    [InlineData("sounds_done", "3.mp3")]
    [InlineData("sounds_done", "4.mp3")]
    [InlineData("sounds_done", "5.mp3")]
    [InlineData("sounds_done", "6.mp3")]
    [InlineData("sounds_done", "7.mp3")]
    [InlineData("sounds_done", "8.mp3")]
    [InlineData("sounds_done", "9.mp3")]
    [InlineData("sounds_done", "10.mp3")]
    [InlineData("sounds_done", "11.mp3")]
    [InlineData("sounds_done", "12.mp3")]
    [InlineData("sounds_error", "1.mp3")]
    [InlineData("sounds_error", "2.mp3")]
    [InlineData("sounds_error", "3.mp3")]
    [InlineData("sounds_error", "4.mp3")]
    [InlineData("sounds_error", "5.mp3")]
    public void AudioAssets_AllRequiredFilesExist(string folder, string fileName)
    {
        string? resolvedPath = AudioNotificationService.ResolveSoundPath(folder, fileName);
        Assert.NotNull(resolvedPath);
        Assert.True(File.Exists(resolvedPath), $"Sound file not found: {resolvedPath}");
    }

    [Theory]
    [InlineData("sounds_done", "CompletionSoundOption_Click")]
    [InlineData("sounds_error", "ErrorSoundOption_Click")]
    public void SoundCatalog_MatchesBundledAssetsMenusAndPersistedSettings(string folder, string clickHandler)
    {
        bool completion = folder == "sounds_done";
        var catalog = completion ? AppSettings.ValidCompletionSounds : AppSettings.ValidErrorSounds;
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax"));
        var files = Directory.GetFiles(Path.Combine(project, "Assets", folder), "*.mp3").Select(Path.GetFileName).ToArray();
        Assert.Equal(catalog.OrderBy(name => name, StringComparer.Ordinal), files.OrderBy(name => name, StringComparer.Ordinal));
        var view = XDocument.Load(Path.Combine(project, "Views", "SettingsView.xaml"));
        var options = view.Descendants().Where(element => element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Click") == clickHandler).Select(element => (string?)element.Attribute("CommandParameter"));
        Assert.Equal(catalog, options);
        foreach (var sound in catalog)
        {
            var settings = new AppSettings { SelectedCompletionSound = completion ? sound : "1.mp3", SelectedErrorSound = completion ? "1.mp3" : sound };
            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            AppSettings.SanitizeSoundSettings(restored);
            Assert.Equal(sound, completion ? restored.SelectedCompletionSound : restored.SelectedErrorSound);
        }
    }

    [Fact]
    public void AppSettings_AudioProperties_DefaultValues()
    {
        var settings = new AppSettings();

        Assert.False(settings.EnableCompletionSound);
        Assert.Equal("1.mp3", settings.SelectedCompletionSound);
        Assert.False(settings.EnableErrorSound);
        Assert.Equal("1.mp3", settings.SelectedErrorSound);
        Assert.Equal(80, settings.SoundVolume);
        Assert.Equal(80, settings.CompletionSoundVolume);
        Assert.Equal(80, settings.ErrorSoundVolume);
    }

    [Fact]
    public void AppSettings_AudioProperties_JsonRoundTrip()
    {
        var original = new AppSettings
        {
            EnableCompletionSound = false,
            SelectedCompletionSound = "4.mp3",
            EnableErrorSound = false,
            SelectedErrorSound = "2.mp3",
            SoundVolume = 45,
            CompletionSoundVolume = 65,
            ErrorSoundVolume = 90
        };

        string json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.False(deserialized.EnableCompletionSound);
        Assert.Equal("4.mp3", deserialized.SelectedCompletionSound);
        Assert.False(deserialized.EnableErrorSound);
        Assert.Equal("2.mp3", deserialized.SelectedErrorSound);
        Assert.Equal(65, deserialized.CompletionSoundVolume);
        Assert.Equal(90, deserialized.ErrorSoundVolume);
    }

    [Fact]
    public void AppSettings_AudioProperties_LegacySoundVolume_DeserializesToBothVolumes()
    {
        string legacyJson = "{\"SoundVolume\":45}";
        var deserialized = JsonSerializer.Deserialize<AppSettings>(legacyJson);

        Assert.NotNull(deserialized);
        Assert.Equal(45, deserialized.CompletionSoundVolume);
        Assert.Equal(45, deserialized.ErrorSoundVolume);
    }

    [Fact]
    public void AudioNotificationService_HeadlessSafety_DoesNotThrow()
    {
        var service = AudioNotificationService.Instance;

        // Calling audio playback methods in a test runner without an active WPF Application must execute safely without throwing.
        var ex1 = Record.Exception(() => service.PlayCompletionSound());
        Assert.Null(ex1);

        var ex2 = Record.Exception(() => service.PlayErrorSound());
        Assert.Null(ex2);

        var ex3 = Record.Exception(() => service.PlayPreview("sounds_done", "1.mp3", 50));
        Assert.Null(ex3);

        var ex4 = Record.Exception(() => service.PlayPreview("sounds_error", "2.mp3", 100));
        Assert.Null(ex4);
    }

    [Fact]
    public void AudioNotificationService_ResolveSoundPath_InvalidInput_ReturnsNullOrFallbackGracefully()
    {
        Assert.Null(AudioNotificationService.ResolveSoundPath("", ""));
        Assert.Null(AudioNotificationService.ResolveSoundPath("sounds_done", ""));
        Assert.Null(AudioNotificationService.ResolveSoundPath("", "1.mp3"));

        string? nonExistent = AudioNotificationService.ResolveSoundPath("non_existent_folder", "missing.mp3");
        Assert.NotNull(nonExistent);
        Assert.False(File.Exists(nonExistent));
    }

    [Theory]
    [InlineData("Settings_Card_Sounds_Title")]
    [InlineData("Settings_Card_Sounds_Subtitle")]
    [InlineData("Settings_Sound_Volume_Title")]
    [InlineData("Settings_Sound_Volume_InlineTitle")]
    [InlineData("Settings_Sound_Volume_Subtitle")]
    [InlineData("Settings_Sound_Completion_Title")]
    [InlineData("Settings_Sound_Completion_Subtitle")]
    [InlineData("Settings_Sound_Trigger_Title")]
    [InlineData("Settings_Sound_Trigger_Subtitle")]
    [InlineData("Settings_Sound_Trigger_EntirePackage")]
    [InlineData("Settings_Sound_Trigger_IndividualFiles")]
    [InlineData("Settings_Sound_Error_Title")]
    [InlineData("Settings_Sound_Error_Subtitle")]
    [InlineData("Settings_Sound_Chip_ToolTip")]
    [InlineData("Settings_Sound_Chip_Label")]
    public void Localization_AudioNotificationKeys_ExistInBothLanguages(string key)
    {
        Assert.True(Strings_de.Map.ContainsKey(key), $"Key '{key}' is missing in Strings_de");
        Assert.False(string.IsNullOrWhiteSpace(Strings_de.Map[key]), $"Value for '{key}' is empty in Strings_de");

        Assert.True(Strings_en.Map.ContainsKey(key), $"Key '{key}' is missing in Strings_en");
        Assert.False(string.IsNullOrWhiteSpace(Strings_en.Map[key]), $"Value for '{key}' is empty in Strings_en");
    }

    [Theory]
    [InlineData("1.mp3", "Sound 1")]
    [InlineData("2.mp3", "Sound 2")]
    [InlineData("3.mp3", "Sound 3")]
    [InlineData("4.mp3", "Sound 4")]
    [InlineData("5.mp3", "Sound 5")]
    [InlineData("6.mp3", "Sound 6")]
    [InlineData(null, "Sound 1")]
    [InlineData("", "Sound 1")]
    public void MainViewModel_SoundDisplayName_FormatsCorrectly(string? soundFile, string expected)
    {
        var vm = new ViewModels.MainViewModel();
        vm.SelectedCompletionSound = soundFile!;
        Assert.Equal(expected, vm.SelectedCompletionSoundDisplayName);
    }

    [Fact]
    public void MainViewModel_AreSoundEffectsEnabled_ReflectsBothSounds()
    {
        var vm = new ViewModels.MainViewModel();

        vm.EnableCompletionSound = true;
        vm.EnableErrorSound = true;
        Assert.True(vm.AreSoundEffectsEnabled);

        vm.EnableCompletionSound = false;
        vm.EnableErrorSound = true;
        Assert.True(vm.AreSoundEffectsEnabled);

        vm.EnableCompletionSound = true;
        vm.EnableErrorSound = false;
        Assert.True(vm.AreSoundEffectsEnabled);

        vm.EnableCompletionSound = false;
        vm.EnableErrorSound = false;
        Assert.False(vm.AreSoundEffectsEnabled);
    }

    [Fact]
    public void MainViewModel_SoundProperties_FiresPropertyChangedNotifications()
    {
        var vm = new ViewModels.MainViewModel();
        var triggered = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null)
                triggered.Add(e.PropertyName);
        };

        vm.SelectedCompletionSound = "3.mp3";
        Assert.Contains(nameof(vm.SelectedCompletionSoundDisplayName), triggered);

        vm.SelectedErrorSound = "2.mp3";
        Assert.Contains(nameof(vm.SelectedErrorSoundDisplayName), triggered);

        vm.EnableCompletionSound = true;
        Assert.Contains(nameof(vm.AreSoundEffectsEnabled), triggered);
    }

    [Fact]
    public void MainViewModel_CycleCompletionSound_CyclesCorrectly()
    {
        var vm = new ViewModels.MainViewModel
        {
            SelectedCompletionSound = "1.mp3"
        };

        // Forward cycling includes the newly registered sounds.
        vm.CycleCompletionSound(true);
        Assert.Equal("2.mp3", vm.SelectedCompletionSound);

        vm.CycleCompletionSound(true);
        Assert.Equal("3.mp3", vm.SelectedCompletionSound);

        vm.SelectedCompletionSound = "10.mp3";
        vm.CycleCompletionSound(true);
        Assert.Equal("11.mp3", vm.SelectedCompletionSound);
        vm.CycleCompletionSound(true);
        Assert.Equal("12.mp3", vm.SelectedCompletionSound);
        vm.CycleCompletionSound(true);
        Assert.Equal("1.mp3", vm.SelectedCompletionSound);

        // Backward cycling wraps to the final registered sound.
        vm.CycleCompletionSound(false);
        Assert.Equal("12.mp3", vm.SelectedCompletionSound);

        vm.CycleCompletionSound(false);
        Assert.Equal("11.mp3", vm.SelectedCompletionSound);
    }

    [Fact]
    public void MainViewModel_CycleErrorSound_CyclesCorrectly()
    {
        var vm = new ViewModels.MainViewModel
        {
            SelectedErrorSound = "1.mp3"
        };

        // Forward cycling includes the newly registered sounds.
        vm.CycleErrorSound(true);
        Assert.Equal("2.mp3", vm.SelectedErrorSound);

        vm.CycleErrorSound(true);
        Assert.Equal("3.mp3", vm.SelectedErrorSound);
        vm.SelectedErrorSound = "5.mp3";
        vm.CycleErrorSound(true);
        Assert.Equal("1.mp3", vm.SelectedErrorSound);

        // Backward cycling wraps to the final registered sound.
        vm.CycleErrorSound(false);
        Assert.Equal("5.mp3", vm.SelectedErrorSound);

        vm.CycleErrorSound(false);
        Assert.Equal("4.mp3", vm.SelectedErrorSound);
    }

    [Fact]
    public void MainViewModel_SelectAndPreview_UpdatesSelectedSoundAndDisplayName()
    {
        var vm = new ViewModels.MainViewModel();

        vm.SelectAndPreviewCompletionSound("7.mp3");
        Assert.Equal("7.mp3", vm.SelectedCompletionSound);
        Assert.Equal("Sound 7", vm.SelectedCompletionSoundDisplayName);

        vm.SelectAndPreviewCompletionSound("10.mp3");
        Assert.Equal("10.mp3", vm.SelectedCompletionSound);
        Assert.Equal("Sound 10", vm.SelectedCompletionSoundDisplayName);

        vm.SelectAndPreviewErrorSound("2.mp3");
        Assert.Equal("2.mp3", vm.SelectedErrorSound);
        Assert.Equal("Sound 2", vm.SelectedErrorSoundDisplayName);
    }

    [Fact]
    public void MainViewModel_VolumeProperties_ClampAndPersist()
    {
        var vm = new ViewModels.MainViewModel();

        vm.CompletionSoundVolume = 42;
        Assert.Equal(42, vm.CompletionSoundVolume);

        vm.CompletionSoundVolume = 150;
        Assert.Equal(100, vm.CompletionSoundVolume);

        vm.CompletionSoundVolume = -20;
        Assert.Equal(0, vm.CompletionSoundVolume);

        vm.ErrorSoundVolume = 65;
        Assert.Equal(65, vm.ErrorSoundVolume);

        vm.ErrorSoundVolume = 200;
        Assert.Equal(100, vm.ErrorSoundVolume);

        vm.ErrorSoundVolume = -5;
        Assert.Equal(0, vm.ErrorSoundVolume);

        // Preview volume methods must execute without throwing
        var ex1 = Record.Exception(() => vm.PreviewCompletionVolumeSound());
        Assert.Null(ex1);

        var ex2 = Record.Exception(() => vm.PreviewErrorVolumeSound());
        Assert.Null(ex2);
    }

    [Fact]
    public void AppSettings_CompletionSoundTrigger_DefaultIsEntirePackage()
    {
        var settings = new AppSettings();
        Assert.Equal(CompletionSoundTrigger.EntirePackage, settings.CompletionSoundTrigger);
    }

    [Fact]
    public void AppSettings_CompletionSoundTrigger_JsonRoundTrip()
    {
        var settings = new AppSettings
        {
            CompletionSoundTrigger = CompletionSoundTrigger.IndividualFiles
        };

        string json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(CompletionSoundTrigger.IndividualFiles, deserialized.CompletionSoundTrigger);
    }

    [Fact]
    public void AppSettings_AudioProperties_Sanitization_FixesMissingExtensionsAndClampsVolume()
    {
        var settings = new AppSettings
        {
            SelectedCompletionSound = "3",
            SelectedErrorSound = "2",
            CompletionSoundVolume = 150,
            ErrorSoundVolume = -25
        };

        AppSettings.SanitizeSoundSettings(settings);

        Assert.Equal("3.mp3", settings.SelectedCompletionSound);
        Assert.Equal("2.mp3", settings.SelectedErrorSound);
        Assert.Equal(100, settings.CompletionSoundVolume);
        Assert.Equal(0, settings.ErrorSoundVolume);

        // Fallback for invalid sound names
        settings.SelectedCompletionSound = "invalid_sound.mp3";
        settings.SelectedErrorSound = "non_existent.wav";
        AppSettings.SanitizeSoundSettings(settings);

        Assert.Equal("1.mp3", settings.SelectedCompletionSound);
        Assert.Equal("1.mp3", settings.SelectedErrorSound);
    }

    [Theory]
    [InlineData("1.mp3")]
    [InlineData("2.mp3")]
    [InlineData("3.mp3")]
    [InlineData("4.mp3")]
    [InlineData("5.mp3")]
    [InlineData("6.mp3")]
    [InlineData("7.mp3")]
    [InlineData("8.mp3")]
    [InlineData("9.mp3")]
    [InlineData("10.mp3")]
    [InlineData("11.mp3")]
    [InlineData("12.mp3")]
    public void AppSettings_AudioProperties_Sanitization_PreservesAllCompletionSounds(string sound)
    {
        var settings = new AppSettings { SelectedCompletionSound = sound };
        AppSettings.SanitizeSoundSettings(settings);
        Assert.Equal(sound, settings.SelectedCompletionSound);
    }

    [Fact]
    public void MainViewModel_CompletionSoundTrigger_TogglesAndPersists()
    {
        var vm = new ViewModels.MainViewModel();

        vm.CompletionSoundTrigger = CompletionSoundTrigger.EntirePackage;
        Assert.True(vm.IsCompletionSoundEntirePackage);
        Assert.False(vm.IsCompletionSoundIndividualFiles);

        vm.IsCompletionSoundIndividualFiles = true;
        Assert.Equal(CompletionSoundTrigger.IndividualFiles, vm.CompletionSoundTrigger);
        Assert.False(vm.IsCompletionSoundEntirePackage);
        Assert.True(vm.IsCompletionSoundIndividualFiles);

        vm.IsCompletionSoundEntirePackage = true;
        Assert.Equal(CompletionSoundTrigger.EntirePackage, vm.CompletionSoundTrigger);
        Assert.True(vm.IsCompletionSoundEntirePackage);
        Assert.False(vm.IsCompletionSoundIndividualFiles);
    }

    [Fact]
    public void AudioNotificationService_ConvertToMediaPlayerVolume_ScaleAndMonotonicity()
    {
        // 0% maps to 0.0
        Assert.Equal(0.0, AudioNotificationService.ConvertToMediaPlayerVolume(0));

        // Clamping boundaries
        Assert.Equal(0.0, AudioNotificationService.ConvertToMediaPlayerVolume(-10));
        Assert.Equal(0.99, AudioNotificationService.ConvertToMediaPlayerVolume(150));

        // 100% must map to 0.99 (wpfgfx native 99, avoiding 100% drop to 50% default volume)
        Assert.Equal(0.99, AudioNotificationService.ConvertToMediaPlayerVolume(100));

        // 99% must be strictly less than 100%
        double vol99 = AudioNotificationService.ConvertToMediaPlayerVolume(99);
        double vol100 = AudioNotificationService.ConvertToMediaPlayerVolume(100);
        Assert.True(vol100 > vol99, $"100% ({vol100}) should be strictly greater than 99% ({vol99})");

        // Monotonic non-decreasing across entire 0..100 range
        double prev = 0.0;
        for (int p = 0; p <= 100; p++)
        {
            double current = AudioNotificationService.ConvertToMediaPlayerVolume(p);
            Assert.True(current >= prev, $"Volume at {p}% ({current}) must be >= previous ({prev})");
            Assert.True(current <= 0.99, $"Volume at {p}% ({current}) must never exceed 0.99");
            prev = current;
        }
    }
}
