namespace Reepax.Models;

public sealed class ThemeAccentSettings
{
    public string Hex { get; set; } = Services.ThemeService.FixedAccentColorHex;
    public double? Hue { get; set; }
    public double? Saturation { get; set; }
    public double? Value { get; set; }
}
