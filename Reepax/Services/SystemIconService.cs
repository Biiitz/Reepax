using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reepax.Services;

public static class SystemIconService
{
    private static readonly ConcurrentDictionary<string, ImageSource> _extensionIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _folderIcon;
    private static ImageSource? _zipFolderIcon;

    #region Win32 Shell API

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    #endregion

    /// <summary>
    /// Returns the authentic Windows system icon for any file name / extension (e.g. .zip, .rar, .7z, .mp4, .pdf, .exe).
    /// </summary>
    public static ImageSource GetIconForFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return GetFolderIcon();

        string ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".bin";

        if (_extensionIconCache.Count > 150)
            _extensionIconCache.Clear();

        return _extensionIconCache.GetOrAdd(ext, extension =>
        {
            try
            {
                var shinfo = new SHFILEINFO();
                var res = SHGetFileInfo(
                    extension,
                    FILE_ATTRIBUTE_NORMAL,
                    ref shinfo,
                    (uint)Marshal.SizeOf(shinfo),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var img = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        img.Freeze();
                        return img;
                    }
                    finally
                    {
                        DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch { }

            return CreateFallbackFileIcon();
        });
    }

    /// <summary>
    /// Returns the authentic Windows ZIP archive / compressed folder icon.
    /// </summary>
    public static ImageSource GetZipArchiveIcon()
    {
        if (_zipFolderIcon != null)
            return _zipFolderIcon;

        _zipFolderIcon = GetIconForFile("archive.zip");
        return _zipFolderIcon;
    }

    /// <summary>
    /// Returns the authentic Windows directory folder icon.
    /// </summary>
    public static ImageSource GetFolderIcon()
    {
        if (_folderIcon != null)
            return _folderIcon;

        try
        {
            var shinfo = new SHFILEINFO();
            var res = SHGetFileInfo(
                "folder",
                FILE_ATTRIBUTE_DIRECTORY,
                ref shinfo,
                (uint)Marshal.SizeOf(shinfo),
                SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

            if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    var img = Imaging.CreateBitmapSourceFromHIcon(
                        shinfo.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                    _folderIcon = img;
                    return _folderIcon;
                }
                finally
                {
                    DestroyIcon(shinfo.hIcon);
                }
            }
        }
        catch { }

        _folderIcon = CreateFallbackFileIcon();
        return _folderIcon;
    }

    private static ImageSource CreateFallbackFileIcon()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 185, 0));
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(1, 1, 14, 14));
        }
        var drawing = new DrawingImage(group);
        drawing.Freeze();
        return drawing;
    }
}
