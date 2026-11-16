using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reepax.Services;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;

namespace Reepax.Tests;

public sealed class HosterIconTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("HosterIcons_").FullName;

    [Fact]
    public void ImportedHoster_OnEmptyCacheUsesTheSameBadgeWithoutWritingAnIcon()
    {
        string icons = Directory.CreateDirectory(Path.Combine(_root, "Icons")).FullName;
        var service = new HosterIconService(icons);
        var package = PackageExportImportService.ImportFromJson("""
            {
              "format": "repx",
              "name": "Example package",
              "items": [
                {
                  "fileName": "sample.bin",
                  "hosterName": "downloads.example.test",
                  "originalUrl": "https://downloads.example.test/sample.bin",
                  "totalBytes": 16
                }
              ]
            }
            """);
        var item = Assert.Single(package.Items);

        var badge = Assert.IsType<DrawingImage>(service.GetIcon(item.HosterName));
        Assert.True(badge.IsFrozen);
        Assert.NotSame(service.GetIcon(null), badge);
        Assert.Same(badge, service.GetIcon(item.OriginalUrl));
        Assert.Same(badge, service.GetIcon(item.HosterName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(icons));
    }

    [Fact]
    public void ExistingLocalIcon_PreservesImageBytesAndSupportsTheDisplayName()
    {
        string icons = Directory.CreateDirectory(Path.Combine(_root, "Icons")).FullName;
        byte[] pixel = [11, 22, 33, 255];
        string imagePath = Path.Combine(icons, "fasthost.png");
        WriteIcon(imagePath, pixel);
        byte[] originalFile = File.ReadAllBytes(imagePath);

        var service = new HosterIconService(icons);
        var icon = Assert.IsAssignableFrom<BitmapSource>(service.GetIcon("FastHost"));
        var actual = new byte[4];
        new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0).CopyPixels(actual, 4, 0);

        Assert.Equal(pixel, actual);
        Assert.True(icon.IsFrozen);
        Assert.Same(icon, service.GetIcon(FastHostResolver.CanonicalDomain));
        Assert.Equal(originalFile, File.ReadAllBytes(imagePath));
        Assert.Single(Directory.EnumerateFileSystemEntries(icons));
    }

    [Fact]
    public void HosterNameCannotLoadAnImageOutsideTheIconCache()
    {
        string icons = Directory.CreateDirectory(Path.Combine(_root, "Icons")).FullName;
        WriteIcon(Path.Combine(_root, "outside.png"), [11, 22, 33, 255]);
        var service = new HosterIconService(icons);

        var unknown = service.GetIcon(null);
        Assert.Same(unknown, service.GetIcon(Path.Combine("..", "outside")));
        Assert.Same(unknown, service.GetIcon(Path.Combine(_root, "outside")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(icons));
    }

    private static void WriteIcon(string path, byte[] pixel)
    {
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixel, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
