using System;
using System.Globalization;
using System.Windows;
using Reepax.Converters;
using Reepax.Models;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class ValueConvertersTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(5, "5 MB/s")]
    [InlineData(1000, "1000 MB/s (1 GB/s)")]
    [InlineData(2500, "2500 MB/s (2.5 GB/s)")]
    [InlineData(-10, null)]
    public void SpeedLimitDisplayConverter_HandlesIntegers(int limit, string? expected)
    {
        var converter = new SpeedLimitDisplayConverter();
        var result = converter.Convert(limit, typeof(string), null, CultureInfo.InvariantCulture);
        var expectedString = expected ?? Loc.Get("SpeedLimit_Unlimited");
        Assert.Equal(expectedString, result);
    }

    [Theory]
    [InlineData(0.0, null)]
    [InlineData(2.5, "2.5 MB/s")]
    [InlineData(0.75, "0.75 MB/s")]
    [InlineData(1500.5, "1500.5 MB/s (1.5 GB/s)")]
    [InlineData(-5.5, null)]
    public void SpeedLimitDisplayConverter_HandlesDoubles(double limit, string? expected)
    {
        var converter = new SpeedLimitDisplayConverter();
        var result = converter.Convert(limit, typeof(string), null, CultureInfo.InvariantCulture);
        var expectedString = expected ?? Loc.Get("SpeedLimit_Unlimited");
        Assert.Equal(expectedString, result);
    }

    [Fact]
    public void SpeedLimitDisplayConverter_HandlesNaN_Infinity_And_Null()
    {
        var converter = new SpeedLimitDisplayConverter();
        var unlimitedStr = Loc.Get("SpeedLimit_Unlimited");

        Assert.Equal(unlimitedStr, converter.Convert(double.NaN, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(unlimitedStr, converter.Convert(double.PositiveInfinity, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(unlimitedStr, converter.Convert(double.NegativeInfinity, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(unlimitedStr, converter.Convert(float.NaN, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(unlimitedStr, converter.Convert(float.PositiveInfinity, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(unlimitedStr, converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SpeedLimitDisplayConverter_ConvertBack_ParsesDoubleAndInt()
    {
        var converter = new SpeedLimitDisplayConverter();

        var doubleResult = converter.ConvertBack("2.5 MB/s", typeof(double), null, CultureInfo.InvariantCulture);
        Assert.Equal(2.5, doubleResult);

        var intResult = converter.ConvertBack("5 MB/s", typeof(int), null, CultureInfo.InvariantCulture);
        Assert.Equal(5, intResult);

        Assert.Equal(0, converter.ConvertBack(null, typeof(int), null, CultureInfo.InvariantCulture));
        Assert.Equal(0, converter.ConvertBack("no-digits-here", typeof(int), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(500.0, "500 B/s")]
    [InlineData(1024.0 * 500, "500 KB/s")]
    [InlineData(1024.0 * 1024 * 12.5, "12.5 MB/s")]
    [InlineData(1024.0 * 1024 * 1024 * 2.0, "2 GB/s")]
    public void SpeedToHumanReadableConverter_FormatsValidSpeeds(double speed, string expected)
    {
        var converter = new SpeedToHumanReadableConverter();
        var result = converter.Convert(speed, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SpeedToHumanReadableConverter_HandlesNaN_Infinity_Null_And_Zero()
    {
        var converter = new SpeedToHumanReadableConverter();

        Assert.Equal("0 KB/s", converter.Convert(0.0, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("0 KB/s", converter.Convert(-500.0, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("0 KB/s", converter.Convert(double.NaN, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("0 KB/s", converter.Convert(double.PositiveInfinity, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("0 KB/s", converter.Convert(double.NegativeInfinity, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("0 KB/s", converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("0 KB/s", converter.Convert("invalid-string", typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(45.0, "00:45")]
    [InlineData(125.0, "02:05")]
    [InlineData(3665.0, "01:01:05")]
    [InlineData(90000.0, "25:00:00")]
    public void SecondsToEtaConverter_FormatsDurations(double seconds, string expected)
    {
        var converter = new SecondsToEtaConverter();
        var result = converter.Convert(seconds, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SecondsToEtaConverter_HandlesNaN_Infinity_Null_And_Zero()
    {
        var converter = new SecondsToEtaConverter();

        Assert.Equal("--:--", converter.Convert(0.0, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("--:--", converter.Convert(-10.0, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("--:--", converter.Convert(double.NaN, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("--:--", converter.Convert(double.PositiveInfinity, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("--:--", converter.Convert(double.NegativeInfinity, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("--:--", converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("--:--", converter.Convert("not-a-number", typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(1024L, "1 KB")]
    [InlineData(1048576L * 5, "5 MB")]
    [InlineData(1073741824L * 10, "10 GB")]
    [InlineData(1099511627776L * 2, "2 TB")]
    public void BytesToHumanReadableConverter_FormatsValidByteSizes(long bytes, string expected)
    {
        var converter = new BytesToHumanReadableConverter();
        var result = converter.Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BytesToHumanReadableConverter_HandlesNullZeroAndParameter()
    {
        var converter = new BytesToHumanReadableConverter();

        Assert.Equal("--", converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("--", converter.Convert(0L, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("--", converter.Convert(-500L, typeof(string), null, CultureInfo.InvariantCulture));

        Assert.Equal("0 B", converter.Convert(0L, typeof(string), "zero", CultureInfo.InvariantCulture));
        Assert.Equal("0 B", converter.Convert(null, typeof(string), "allowZero", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DoubleToGridLengthConverter_HandlesNaN_Infinity_And_Star()
    {
        var converter = new DoubleToGridLengthConverter();

        var zeroLength = (GridLength)converter.Convert(0.0, typeof(GridLength), null, CultureInfo.InvariantCulture)!;
        Assert.Equal(0, zeroLength.Value);

        var nanLength = (GridLength)converter.Convert(double.NaN, typeof(GridLength), null, CultureInfo.InvariantCulture)!;
        Assert.Equal(0, nanLength.Value);

        var infLength = (GridLength)converter.Convert(double.PositiveInfinity, typeof(GridLength), null, CultureInfo.InvariantCulture)!;
        Assert.Equal(0, infLength.Value);

        var starLength = (GridLength)converter.Convert(100.0, typeof(GridLength), "star", CultureInfo.InvariantCulture)!;
        Assert.True(starLength.IsStar);

        var normalLength = (GridLength)converter.Convert(250.0, typeof(GridLength), null, CultureInfo.InvariantCulture)!;
        Assert.Equal(250.0, normalLength.Value);
    }

    [Fact]
    public void BoolAndVisibilityConverters_EvaluateCorrectly()
    {
        var activeText = new BoolToActiveTextConverter();
        Assert.Equal(Loc.Get("Common_Enabled"), activeText.Convert(true, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(Loc.Get("Common_Disabled"), activeText.Convert(false, typeof(string), null, CultureInfo.InvariantCulture));

        var opacity = new BoolToOpacityConverter();
        Assert.Equal(1.0, opacity.Convert(true, typeof(double), null, CultureInfo.InvariantCulture));
        Assert.Equal(0.45, opacity.Convert(false, typeof(double), null, CultureInfo.InvariantCulture));

        var inverseBool = new InverseBoolConverter();
        Assert.True((bool)inverseBool.Convert(false, typeof(bool), null, CultureInfo.InvariantCulture)!);
        Assert.False((bool)inverseBool.Convert(true, typeof(bool), null, CultureInfo.InvariantCulture)!);

        var invVis = new InverseBooleanToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, invVis.Convert(true, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, invVis.Convert(false, typeof(Visibility), null, CultureInfo.InvariantCulture));

        var countVis = new CountToVisibilityConverter();
        Assert.Equal(Visibility.Visible, countVis.Convert(5, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, countVis.Convert(0, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, countVis.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ItemActionMenuHeaderConverter_ReturnsCorrectTextBasedOnStatus()
    {
        var converter = new ItemActionMenuHeaderConverter();

        // Browser window closed -> Try again
        var item1 = new DownloadItem
        {
            Status = DownloadStatus.Paused,
            StatusMessage = Loc.Get("Status_BrowserWindowClosed")
        };
        Assert.Equal(Loc.Get("Menu_Retry"), converter.Convert(item1, typeof(string), null, CultureInfo.InvariantCulture));

        // Failed -> Try again
        var item2 = new DownloadItem
        {
            Status = DownloadStatus.Failed,
            StatusMessage = "Some error"
        };
        Assert.Equal(Loc.Get("Menu_Retry"), converter.Convert(item2, typeof(string), null, CultureInfo.InvariantCulture));

        // Downloading -> Pause
        var item3 = new DownloadItem
        {
            Status = DownloadStatus.Downloading
        };
        Assert.Equal(Loc.Get("Menu_Pause"), converter.Convert(item3, typeof(string), null, CultureInfo.InvariantCulture));

        // Normal Paused -> Resume
        var item4 = new DownloadItem
        {
            Status = DownloadStatus.Paused,
            StatusMessage = Loc.Get("Status_Paused")
        };
        Assert.Equal(Loc.Get("Menu_Resume"), converter.Convert(item4, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EqualsToVisibilityConverter_HandlesNormalAndNegatedComparison()
    {
        var converter = new EqualsToVisibilityConverter();

        // Normal comparison
        Assert.Equal(Visibility.Visible, converter.Convert("Completed", typeof(Visibility), "Completed", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, converter.Convert("Downloading", typeof(Visibility), "Completed", CultureInfo.InvariantCulture));

        // Negated comparison (!Completed)
        Assert.Equal(Visibility.Collapsed, converter.Convert("Completed", typeof(Visibility), "!Completed", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, converter.Convert("Downloading", typeof(Visibility), "!Completed", CultureInfo.InvariantCulture));
        // Pipe-separated comparison (OptionA|OptionB)
        Assert.Equal(Visibility.Visible, converter.Convert("General", typeof(Visibility), "General|Notifications", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, converter.Convert("Notifications", typeof(Visibility), "General|Notifications", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, converter.Convert("Appearance", typeof(Visibility), "General|Notifications", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void BoolToRightBorderThicknessConverter_ReturnsExpectedThickness()
    {
        var converter = new BoolToRightBorderThicknessConverter();

        var trueResult = (Thickness)converter.Convert(true, typeof(Thickness), null, CultureInfo.InvariantCulture);
        Assert.Equal(new Thickness(0, 0, 1, 0), trueResult);

        var falseResult = (Thickness)converter.Convert(false, typeof(Thickness), null, CultureInfo.InvariantCulture);
        Assert.Equal(new Thickness(0, 0, 0, 0), falseResult);

        var nullResult = (Thickness)converter.Convert(null, typeof(Thickness), null, CultureInfo.InvariantCulture);
        Assert.Equal(new Thickness(0, 0, 0, 0), nullResult);
    }
}
