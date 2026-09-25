using System.Windows.Media;
using Reepax.Helpers;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class ColorPaletteTests
{
    [Fact]
    public void FromHsv_ComputesCorrectPrimaryColors()
    {
        // Red (0 deg)
        var red = ColorHelper.FromHsv(0, 1.0, 1.0);
        Assert.Equal(255, red.R);
        Assert.Equal(0, red.G);
        Assert.Equal(0, red.B);

        // Green (120 deg)
        var green = ColorHelper.FromHsv(120, 1.0, 1.0);
        Assert.Equal(0, green.R);
        Assert.Equal(255, green.G);
        Assert.Equal(0, green.B);

        // Blue (240 deg)
        var blue = ColorHelper.FromHsv(240, 1.0, 1.0);
        Assert.Equal(0, blue.R);
        Assert.Equal(0, blue.G);
        Assert.Equal(255, blue.B);
    }

    [Fact]
    public void Hsv_Rgb_RoundTrip_PreservesColors()
    {
        var originalColor = Color.FromRgb(59, 130, 246); // Default blue #3B82F6
        ColorHelper.ToHsv(originalColor, out double h, out double s, out double v);

        var restoredColor = ColorHelper.FromHsv(h, s, v);
        Assert.InRange(Math.Abs(originalColor.R - restoredColor.R), 0, 2);
        Assert.InRange(Math.Abs(originalColor.G - restoredColor.G), 0, 2);
        Assert.InRange(Math.Abs(originalColor.B - restoredColor.B), 0, 2);
    }

    [Fact]
    public void TryParseHex_HandlesValidAndInvalidHex()
    {
        Assert.True(ColorHelper.TryParseHex("#FF0055", out var c1));
        Assert.Equal(255, c1.R);
        Assert.Equal(0, c1.G);
        Assert.Equal(0x55, c1.B);

        Assert.True(ColorHelper.TryParseHex("3B82F6", out var c2));
        Assert.Equal(59, c2.R);
        Assert.Equal(130, c2.G);
        Assert.Equal(246, c2.B);

        Assert.False(ColorHelper.TryParseHex("invalid", out _));
        Assert.False(ColorHelper.TryParseHex("", out _));
    }

    [Fact]
    public void MainViewModel_AccentColor_RemainsFixedDefaultBlue()
    {
        var vm = new MainViewModel();
        Assert.Equal("#3B82F6", vm.CurrentAccentColor);

        vm.UpdateFromHsv(0, 1.0, 1.0);
        Assert.Equal("#3B82F6", vm.CurrentAccentColor);

        vm.ResetAccentColor();
        Assert.Equal("#3B82F6", vm.CurrentAccentColor);
    }
}
