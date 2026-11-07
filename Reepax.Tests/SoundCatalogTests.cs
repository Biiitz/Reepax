using System.IO;
using Reepax.Models;
using Reepax.Services.Audio;
using Xunit;

namespace Reepax.Tests;

public sealed class SoundCatalogTests
{
    [Fact]
    public void Discovery_RegistersNewSoundsNaturallyAndExcludesIgnoredFolders()
    {
        string root = Directory.CreateTempSubdirectory("SoundCatalog_").FullName;
        try
        {
            foreach (var name in new[] { "sounds/10.wav", "sounds/2.wav", "custom.mp3", "etc/toggle.wav", "etc/nested/ignored.mp3", "image.png" })
            {
                string path = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[] { 1 });
            }
            var catalog = new SoundCatalog(root);
            Assert.Equal(new[] { "sounds/2.wav", "sounds/10.wav", "custom.mp3" }, catalog.Sounds.Select(sound => sound.Value));
            Assert.Equal("Sound 2", catalog.Sounds[0].Text);
            Assert.Equal("custom", catalog.Sounds[2].Text);
            File.WriteAllBytes(Path.Combine(root, "sounds", "31.wav"), new byte[] { 2 });
            var reopened = new SoundCatalog(root);
            Assert.Contains(reopened.Sounds, sound => sound.Value == "sounds/31.wav");
            Assert.Equal("sounds/2.wav", reopened.SanitizeSelection("2.mp3"));
            Assert.Equal("sounds/10.wav", reopened.SanitizeSelection("10"));
            Assert.Equal("sounds/2.wav", reopened.SanitizeSelection("../custom.mp3"));
            Assert.Null(reopened.Resolve("etc/toggle.wav"));
            Assert.Null(reopened.Resolve("../custom.mp3"));
            Assert.Equal(Path.Combine(root, "sounds", "31.wav"), reopened.Resolve("sounds/31.wav"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LegacySelections_MigrateToTheSharedCatalogAndKeepIndependentVolumes()
    {
        var settings = new AppSettings { SelectedCompletionSound = "12.mp3", SelectedErrorSound = "5.mp3",
            CompletionSoundVolume = 25, ErrorSoundVolume = 75 };
        AppSettings.SanitizeSoundSettings(settings);
        Assert.Equal("sounds/12.wav", settings.SelectedCompletionSound);
        Assert.Equal("sounds/5.wav", settings.SelectedErrorSound);
        Assert.Equal(25, settings.CompletionSoundVolume);
        Assert.Equal(75, settings.ErrorSoundVolume);
        foreach (var sound in SoundCatalog.Default.Sounds)
        {
            settings.SelectedCompletionSound = settings.SelectedErrorSound = sound.Value;
            AppSettings.SanitizeSoundSettings(settings);
            Assert.Equal(sound.Value, settings.SelectedCompletionSound);
            Assert.Equal(sound.Value, settings.SelectedErrorSound);
        }
    }

    [Fact]
    public void BundledWaveFiles_AreNonSilentPcmWithPeakHeadroom()
    {
        foreach (var sound in SoundCatalog.Default.Sounds)
        {
            using var input = new BinaryReader(File.OpenRead(sound.FullPath));
            Assert.Equal("RIFF", new string(input.ReadChars(4)));
            input.ReadUInt32();
            Assert.Equal("WAVE", new string(input.ReadChars(4)));
            bool format = false, data = false;
            while (input.BaseStream.Position + 8 <= input.BaseStream.Length)
            {
                string chunk = new(input.ReadChars(4));
                uint size = input.ReadUInt32();
                long end = input.BaseStream.Position + size;
                Assert.True(end <= input.BaseStream.Length);
                if (chunk == "fmt ")
                {
                    Assert.Equal(1, input.ReadUInt16());
                    Assert.InRange(input.ReadUInt16(), 1, 2);
                    Assert.Equal(48000u, input.ReadUInt32());
                    input.ReadUInt32();
                    input.ReadUInt16();
                    Assert.Equal(16, input.ReadUInt16());
                    format = true;
                }
                if (chunk == "data")
                {
                    int peak = 0;
                    while (input.BaseStream.Position + 2 <= end) peak = Math.Max(peak, Math.Abs((int)input.ReadInt16()));
                    Assert.InRange(peak, 1, 30000);
                    data = true;
                }
                input.BaseStream.Position = end + (size & 1);
            }
            Assert.True(format && data);
        }
    }
}
