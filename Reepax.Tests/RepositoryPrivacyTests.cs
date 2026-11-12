using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Reepax.Services.AdBlock.Native;
using SharpCompress.Archives;
using SharpCompress.Readers;
using Xunit;

namespace Reepax.Tests;

public sealed class RepositoryPrivacyTests
{
    private static readonly Regex PersonalPath = new(
        @"(?i)(?:[a-z]:[\\/]+users[\\/]+|/(?:users|home)/[^/\s]+)", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("reepax_adblock.dll")]
    [InlineData("runtimes/win-x64/native/reepax_adblock.dll")]
    public void ShippedNativeBinary_DoesNotExposePersonalBuildPaths(string relativePath)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, relativePath));
        Assert.False(PersonalPath.IsMatch(Encoding.Latin1.GetString(bytes)), "Native binary contains a personal source path.");
        Assert.False(PersonalPath.IsMatch(Encoding.Unicode.GetString(bytes)), "Native binary contains a personal source path.");
    }

    [Fact]
    public void NativeEngine_LoadsAndAppliesRulesWithoutFallback()
    {
        Assert.True(AdBlockRustNative.IsAvailable, "The shipped native engine could not be loaded.");
        IntPtr engine = AdBlockRustNative.CreateFromRules("||ads.example.test^$script");
        Assert.NotEqual(IntPtr.Zero, engine);
        try
        {
            Assert.Equal(1, AdBlockRustNative.CheckNetwork(engine, "https://ads.example.test/banner.js", "https://example.test", "script"));
            Assert.Equal(0, AdBlockRustNative.CheckNetwork(engine, "https://example.test/content.js", "https://example.test", "script"));
        }
        finally { AdBlockRustNative.Free(engine); }
    }

    [Fact]
    public void ArchiveFixtures_DoNotContainPersonalPathsInNamesOrPayloads()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string directory = Path.Combine(root, "Reepax.Tests", "Fixtures");
        string[] files = new[] { "Archives", "DecoderArchives" }
            .SelectMany(name => Directory.GetFiles(Path.Combine(directory, name), "*", SearchOption.AllDirectories))
            .Where(path => !path.Contains(".part", StringComparison.Ordinal) || path.EndsWith(".part1.rar", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            using var archive = ArchiveFactory.OpenArchive(file, new ReaderOptions { Password = " Fixture Key " });
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                Assert.False(PersonalPath.IsMatch(entry.Key ?? ""), "Archive entry contains a personal path.");
                using var source = entry.OpenEntryStream();
                using var output = new MemoryStream();
                source.CopyTo(output);
                byte[] payload = output.ToArray();
                Assert.False(PersonalPath.IsMatch(Encoding.UTF8.GetString(payload)), "Archive payload contains a personal path.");
                Assert.False(PersonalPath.IsMatch(Encoding.Unicode.GetString(payload)), "Archive payload contains a personal path.");
            }
        }
    }

    [Fact]
    public void UpdateHelperFixtures_DoNotContainPersonalPaths()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string[] files = Directory.GetFiles(Path.Combine(root, "Reepax.Tests", "Fixtures", "Updates"), "*.ps1");
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            Assert.False(PersonalPath.IsMatch(Path.GetRelativePath(root, file)), "Update fixture contains a personal path.");
            Assert.False(PersonalPath.IsMatch(File.ReadAllText(file)), "Update fixture contains a personal source path.");
        }
    }
}
