using System;
using System.IO;
using System.Windows.Media;
using Reepax.Helpers;
using Reepax.Services;
using Reepax.Services.Localization;
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
    public void MainViewModel_AccentColor_CanBeChangedAndReset()
    {
        var vm = new MainViewModel();

        // 1. Change color via HSV (pure red)
        vm.UpdateFromHsv(0, 1.0, 1.0);
        Assert.Equal("#FF0000", vm.CurrentAccentColor);
        Assert.Equal(255, vm.ColorR);
        Assert.Equal(0, vm.ColorG);
        Assert.Equal(0, vm.ColorB);

        // 2. Change color via RGB (pure green)
        vm.UpdateFromRgb(0, 255, 0);
        Assert.Equal("#00FF00", vm.CurrentAccentColor);
        Assert.Equal(0, vm.ColorR);
        Assert.Equal(255, vm.ColorG);
        Assert.Equal(0, vm.ColorB);

        // 3. Change color via Hex property (purple)
        vm.CustomAccentColorHex = "#A855F7";
        Assert.Equal("#A855F7", vm.CurrentAccentColor);

        // 4. Reset to default blue
        vm.ResetAccentColor();
        Assert.Equal("#3B82F6", vm.CurrentAccentColor);
        Assert.Equal(MainViewModel.DefaultAccentHue, vm.PickerHue, precision: 1);
        Assert.Equal(MainViewModel.DefaultAccentSaturation, vm.PickerSaturation, precision: 2);
        Assert.Equal(MainViewModel.DefaultAccentValue, vm.PickerValue, precision: 2);
    }

    [Fact]
    public void ResetAccentColor_ResetsSliderAndSpectrumCoordinatesToDefault()
    {
        var vm = new MainViewModel();

        // Move slider to yellow-orange and change coordinates
        vm.UpdateFromHsv(45.0, 0.5, 0.8);
        Assert.Equal(45.0, vm.PickerHue, precision: 1);
        Assert.Equal(0.5, vm.PickerSaturation, precision: 2);
        Assert.Equal(0.8, vm.PickerValue, precision: 2);

        // Reset
        vm.ResetAccentColor();

        // Check that slider and point coordinates are completely reset to default
        Assert.Equal(MainViewModel.DefaultAccentHue, vm.PickerHue, precision: 1);
        Assert.Equal(MainViewModel.DefaultAccentSaturation, vm.PickerSaturation, precision: 2);
        Assert.Equal(MainViewModel.DefaultAccentValue, vm.PickerValue, precision: 2);
        Assert.Equal("#3B82F6", vm.CurrentAccentColor);
        Assert.Equal(59, vm.ColorR);
        Assert.Equal(130, vm.ColorG);
        Assert.Equal(246, vm.ColorB);
    }

    [Fact]
    public void AchromaticColor_PreservesPickerHue()
    {
        var vm = new MainViewModel();
        try
        {
            vm.UpdateFromHsv(180.0, 0.8, 0.8); // Hue at 180 (Cyan)

            // Switching to black (#000000) does not destroy the HueSlider position
            vm.UpdateFromRgb(0, 0, 0);
            Assert.Equal("#000000", vm.CurrentAccentColor);
            Assert.Equal(180.0, vm.PickerHue, precision: 1);

            // Switching to white (#FFFFFF) does not destroy the HueSlider position
            vm.UpdateFromRgb(255, 255, 255);
            Assert.Equal("#FFFFFF", vm.CurrentAccentColor);
            Assert.Equal(180.0, vm.PickerHue, precision: 1);
        }
        finally
        {
            vm.ResetAccentColor();
        }
    }

    [Fact]
    public void AppSettings_PersistsHueSaturationAndValue()
    {
        var settings = new Models.AppSettings();
        Assert.Equal("#3B82F6", settings.AccentColorHex);
        Assert.Equal(217.22, settings.AccentColorHue);
        Assert.Equal(0.76, settings.AccentColorSaturation);
        Assert.Equal(0.96, settings.AccentColorValue);
    }

    [Fact]
    public void ThemeService_SetAccentColor_AppliesAndFiresEvent()
    {
        string? notifiedHex = null;
        void OnAccentChanged(string hex) => notifiedHex = hex;

        ThemeService.Instance.AccentColorChanged += OnAccentChanged;
        try
        {
            ThemeService.Instance.SetAccentColor("#10B981", save: false);
            Assert.Equal("#10B981", ThemeService.Instance.CurrentAccentColorHex);
            Assert.Equal("#10B981", notifiedHex);

            // Invalid hex falls back to default #3B82F6 without throwing
            ThemeService.Instance.SetAccentColor("invalid-color", save: false);
            Assert.Equal("#3B82F6", ThemeService.Instance.CurrentAccentColorHex);
        }
        finally
        {
            ThemeService.Instance.AccentColorChanged -= OnAccentChanged;
            ThemeService.Instance.SetAccentColor("#3B82F6", save: false);
        }
    }

    [Fact]
    public void MainWindow_TrashCanIcons_UseAccentColorDynamicResource()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\Reepax"));
        if (!Directory.Exists(projectDir))
        {
            projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\Reepax"));
        }

        string mainWindowPath = Path.Combine(projectDir, "MainWindow.xaml");
        Assert.True(File.Exists(mainWindowPath), $"MainWindow.xaml not found at {mainWindowPath}");

        string xaml = File.ReadAllText(mainWindowPath);

        // Find all lines with IconTrash in MainWindow.xaml
        var lines = xaml.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var trashLines = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(lines, l => l.Contains("IconTrash")));

        Assert.NotEmpty(trashLines);
        foreach (var line in trashLines)
        {
            // Verify none of the trash can icons are hardcoded red #EF4444
            Assert.DoesNotContain("#EF4444", line);
            // Verify they use dynamic accent brush
            Assert.Contains("{DynamicResource AccentBlueBrush}", line);
        }
    }

    [Fact]
    public void AccentColor_LocalizationStrings_AreDefinedInAllLanguages()
    {
        string[] requiredKeys =
        [
            "Settings_AccentColor_Title",
            "Settings_AccentColor_Subtitle",
            "Settings_AccentColor_ToggleTooltip",
            "Settings_AccentColor_Reset",
            "Settings_AccentColor_HexLabel",
            "Settings_AccentColor_RedLabel",
            "Settings_AccentColor_GreenLabel",
            "Settings_AccentColor_BlueLabel"
        ];

        foreach (var key in requiredKeys)
        {
            Assert.True(Strings_de.Map.ContainsKey(key), $"German translation missing for '{key}'");
            Assert.False(string.IsNullOrWhiteSpace(Strings_de.Map[key]), $"German translation empty for '{key}'");

            Assert.True(Strings_en.Map.ContainsKey(key), $"English translation missing for '{key}'");
            Assert.False(string.IsNullOrWhiteSpace(Strings_en.Map[key]), $"English translation empty for '{key}'");
        }
    }
}
