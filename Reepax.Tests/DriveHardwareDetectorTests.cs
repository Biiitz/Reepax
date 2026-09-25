using System;
using Reepax.Services.Extractor;
using Xunit;

namespace Reepax.Tests;

public class DriveHardwareDetectorTests
{
    [Theory]
    [InlineData(@"C:\invalid:path")]
    [InlineData(@"D:\path;with;semicolons")]
    [InlineData(@"E:\path&with&ampersand")]
    [InlineData(@"C:\path'with'quotes")]
    [InlineData(@"\\?\C:\foo\bar")]
    [InlineData(@"\\192.168.1.1\share\data")]
    [InlineData(@"../../../../../../data/sample.txt")]
    [InlineData("\0\r\n\t")]
    [InlineData(":::")]
    [InlineData("123:")]
    [InlineData("$#@!")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DetectDriveType_WithInvalidOrUnusualPaths_HandlesSafelyAndReturnsValidType(string? testPath)
    {
        var type = DriveHardwareDetector.DetectDriveType(testPath!);
        Assert.True(Enum.IsDefined(typeof(DriveStorageType), type));
    }

    [Theory]
    [InlineData(@"C:\invalid:path")]
    [InlineData(@"D:\path;with;semicolons")]
    [InlineData(@"E:\path&with&ampersand")]
    [InlineData(@"C:\path'with'quotes")]
    [InlineData("")]
    [InlineData(null)]
    public void IsLowResourceRecommended_WithInvalidOrUnusualPaths_ReturnsBoolSafely(string? testPath)
    {
        var isRecommended = DriveHardwareDetector.IsLowResourceRecommended(testPath!);
        // Result must be a valid boolean without throwing
        Assert.True(isRecommended == true || isRecommended == false);
    }

    [Theory]
    [InlineData(@"C:\invalid:path")]
    [InlineData(@"D:\path;with;semicolons")]
    [InlineData(@"E:\path&with&ampersand")]
    [InlineData("")]
    [InlineData(null)]
    public void GetDriveStorageDescription_WithInvalidOrUnusualPaths_ReturnsSafeString(string? testPath)
    {
        var description = DriveHardwareDetector.GetDriveStorageDescription(testPath!);
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Theory]
    [InlineData(@"C:\Downloads")]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    [InlineData("c:")]
    [InlineData(@"c:\test\subfolder")]
    public void DetectDriveType_ValidStandardPaths_ReturnsStorageType(string path)
    {
        var type = DriveHardwareDetector.DetectDriveType(path);
        Assert.True(Enum.IsDefined(typeof(DriveStorageType), type));
    }
}
