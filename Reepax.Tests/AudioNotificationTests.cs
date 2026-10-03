using System;
using System.IO;
using System.Text.Json;
using Reepax.Models;
using Reepax.Services.Audio;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class AudioNotificationTests
{
    [Theory]
    [InlineData("sounds_done", "1.mp3")]
    [InlineData("sounds_done", "2.mp3")]
    [InlineData("sounds_done", "3.mp3")]
    [InlineData("sounds_done", "4.mp3")]
    [InlineData("sounds_done", "5.mp3")]
    [InlineData("sounds_done", "6.mp3")]
    [InlineData("sounds_error", "1.mp3")]
    [InlineData("sounds_error", "2.mp3")]
    [InlineData("sounds_error", "3.mp3")]
    public void AudioAssets_AllRequiredFilesExist(string folder, string fileName)
    {
        string? resolvedPath = AudioNotificationService.ResolveSoundPath(folder, fileName);
        Assert.NotNull(resolvedPath);
        Assert.True(File.Exists(resolvedPath), $"Sound file not found: {resolvedPath}");
    }

    [Fact]
    public void AppSettings_AudioProperties_DefaultValues()
    {
        var settings = new AppSettings();

        Assert.True(settings.EnableCompletionSound);
        Assert.Equal("1.mp3", settings.SelectedCompletionSound);
        Assert.True(settings.EnableErrorSound);
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

        vm.EnableCompletionSound = false;
        Assert.Contains(nameof(vm.AreSoundEffectsEnabled), triggered);
    }

    [Fact]
    public void MainViewModel_CycleCompletionSound_CyclesCorrectly()
    {
        var vm = new ViewModels.MainViewModel
        {
            SelectedCompletionSound = "1.mp3"
        };

        // Forward cycling: 1 -> 2 -> 3 -> 4 -> 5 -> 6 -> 1
        vm.CycleCompletionSound(true);
        Assert.Equal("2.mp3", vm.SelectedCompletionSound);

        vm.CycleCompletionSound(true);
        Assert.Equal("3.mp3", vm.SelectedCompletionSound);

        vm.SelectedCompletionSound = "6.mp3";
        vm.CycleCompletionSound(true);
        Assert.Equal("1.mp3", vm.SelectedCompletionSound);

        // Backward cycling: 1 -> 6 -> 5
        vm.CycleCompletionSound(false);
        Assert.Equal("6.mp3", vm.SelectedCompletionSound);

        vm.CycleCompletionSound(false);
        Assert.Equal("5.mp3", vm.SelectedCompletionSound);
    }

    [Fact]
    public void MainViewModel_CycleErrorSound_CyclesCorrectly()
    {
        var vm = new ViewModels.MainViewModel
        {
            SelectedErrorSound = "1.mp3"
        };

        // Forward cycling: 1 -> 2 -> 3 -> 1
        vm.CycleErrorSound(true);
        Assert.Equal("2.mp3", vm.SelectedErrorSound);

        vm.CycleErrorSound(true);
        Assert.Equal("3.mp3", vm.SelectedErrorSound);

        vm.CycleErrorSound(true);
        Assert.Equal("1.mp3", vm.SelectedErrorSound);

        // Backward cycling: 1 -> 3 -> 2
        vm.CycleErrorSound(false);
        Assert.Equal("3.mp3", vm.SelectedErrorSound);

        vm.CycleErrorSound(false);
        Assert.Equal("2.mp3", vm.SelectedErrorSound);
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
}
