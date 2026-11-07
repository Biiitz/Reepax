using System.IO;
using Reepax.Services.Storage;

namespace Reepax.Services.Audio;

public sealed record SoundOption(string Value, string Text, string FullPath);

public sealed class SoundCatalog
{
    public static SoundCatalog Default { get; } = new(Path.Combine(AppContext.BaseDirectory, "Assets"));
    public IReadOnlyList<SoundOption> Sounds { get; }
    public string DefaultSound => Sounds.FirstOrDefault()?.Value ?? "1.wav";

    internal SoundCatalog(string directory)
    {
        var sounds = new List<SoundOption>();
        Collect(directory, directory, sounds);
        Sounds = sounds.OrderBy(sound => int.TryParse(Path.GetFileNameWithoutExtension(sound.Value), out var number) ? number : int.MaxValue)
            .ThenBy(sound => sound.Value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Collect(string root, string directory, List<SoundOption> sounds)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!new[] { ".wav", ".mp3", ".m4a", ".wma" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) ||
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                string value = Path.GetRelativePath(root, file).Replace('\\', '/');
                string name = Path.GetFileNameWithoutExtension(file);
                string text = int.TryParse(name, out _) ? "Sound " + name : name;
                sounds.Add(new SoundOption(value, text, Path.GetFullPath(file)));
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (new[] { "etc", "sounds_done", "sounds_error" }.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                Collect(root, child, sounds);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("[Audio] Some sound assets could not be listed.");
        }
    }

    public string SanitizeSelection(string? value)
    {
        var normalized = value?.Trim().Replace('\\', '/');
        var exact = Sounds.FirstOrDefault(sound => string.Equals(sound.Value, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Value;
        // Migrate selections from the removed category folders and numbered MP3 catalog.
        if (!string.IsNullOrWhiteSpace(normalized) && !normalized.Contains('/') && !normalized.Contains(':'))
        {
            var stem = Path.GetFileNameWithoutExtension(normalized);
            var match = Sounds.FirstOrDefault(sound => string.Equals(Path.GetFileNameWithoutExtension(sound.Value), stem, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Value;
        }
        return DefaultSound;
    }

    internal string? Resolve(string? value) => Sounds.FirstOrDefault(sound =>
        string.Equals(sound.Value, value?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))?.FullPath;
}
