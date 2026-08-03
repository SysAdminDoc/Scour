using System.Windows;
using System.Windows.Media;
using Scour.Core;
using CoreThemeMode = Scour.Core.ThemeMode;

namespace Scour.App.Services;

public static class ThemeManager
{
    private static readonly IReadOnlyDictionary<CoreThemeMode, IReadOnlyDictionary<string, Color>> Palettes =
        new Dictionary<CoreThemeMode, IReadOnlyDictionary<string, Color>>
        {
            [CoreThemeMode.Mocha] = Palette(
                ("Rosewater", "#F5E0DC"), ("Flamingo", "#F2CDCD"), ("Pink", "#F5C2E7"),
                ("Mauve", "#CBA6F7"), ("Red", "#F38BA8"), ("Maroon", "#EBA0AC"),
                ("Peach", "#FAB387"), ("Yellow", "#F9E2AF"), ("Green", "#A6E3A1"),
                ("Teal", "#94E2D5"), ("Sky", "#89DCEB"), ("Sapphire", "#74C7EC"),
                ("Blue", "#89B4FA"), ("Lavender", "#B4BEFE"), ("Text", "#CDD6F4"),
                ("Subtext1", "#BAC2DE"), ("Subtext0", "#A6ADC8"), ("Overlay2", "#9399B2"),
                ("Overlay1", "#7F849C"), ("Overlay0", "#6C7086"), ("Surface2", "#585B70"),
                ("Surface1", "#45475A"), ("Surface0", "#313244"), ("Base", "#1E1E2E"),
                ("Mantle", "#181825"), ("Crust", "#11111B")),
            [CoreThemeMode.Latte] = Palette(
                ("Rosewater", "#DC8A78"), ("Flamingo", "#DD7878"), ("Pink", "#EA76CB"),
                ("Mauve", "#8839EF"), ("Red", "#D20F39"), ("Maroon", "#E64553"),
                ("Peach", "#FE640B"), ("Yellow", "#DF8E1D"), ("Green", "#40A02B"),
                ("Teal", "#179299"), ("Sky", "#04A5E5"), ("Sapphire", "#209FB5"),
                ("Blue", "#1E66F5"), ("Lavender", "#7287FD"), ("Text", "#4C4F69"),
                ("Subtext1", "#5C5F77"), ("Subtext0", "#6C6F85"), ("Overlay2", "#7C7F93"),
                ("Overlay1", "#8C8FA1"), ("Overlay0", "#9CA0B0"), ("Surface2", "#ACB0BE"),
                ("Surface1", "#BCC0CC"), ("Surface0", "#CCD0DA"), ("Base", "#EFF1F5"),
                ("Mantle", "#E6E9EF"), ("Crust", "#DCE0E8")),
            [CoreThemeMode.OLED] = Palette(
                ("Rosewater", "#F5E0DC"), ("Flamingo", "#F2CDCD"), ("Pink", "#F5C2E7"),
                ("Mauve", "#BB9AF7"), ("Red", "#F7768E"), ("Maroon", "#EBA0AC"),
                ("Peach", "#FF9E64"), ("Yellow", "#E0AF68"), ("Green", "#9ECE6A"),
                ("Teal", "#73DACA"), ("Sky", "#7DCFFF"), ("Sapphire", "#2AC3DE"),
                ("Blue", "#7AA2F7"), ("Lavender", "#BB9AF7"), ("Text", "#F5F5F5"),
                ("Subtext1", "#D0D0D0"), ("Subtext0", "#A0A0A0"), ("Overlay2", "#808080"),
                ("Overlay1", "#686868"), ("Overlay0", "#505050"), ("Surface2", "#303030"),
                ("Surface1", "#202020"), ("Surface0", "#141414"), ("Base", "#000000"),
                ("Mantle", "#000000"), ("Crust", "#000000")),
        };

    private static readonly IReadOnlyDictionary<string, string> BrushColors =
        new Dictionary<string, string>
        {
            ["BaseBrush"] = "Base",
            ["MantleBrush"] = "Mantle",
            ["CrustBrush"] = "Crust",
            ["Surface0Brush"] = "Surface0",
            ["Surface1Brush"] = "Surface1",
            ["Surface2Brush"] = "Surface2",
            ["Overlay0Brush"] = "Overlay0",
            ["Overlay1Brush"] = "Overlay1",
            ["TextBrush"] = "Text",
            ["Subtext1Brush"] = "Subtext1",
            ["Subtext0Brush"] = "Subtext0",
            ["AccentBrush"] = "Mauve",
            ["BlueBrush"] = "Blue",
            ["GreenBrush"] = "Green",
            ["RedBrush"] = "Red",
            ["PeachBrush"] = "Peach",
            ["YellowBrush"] = "Yellow",
            ["LavenderBrush"] = "Lavender",
        };

    public static void Apply(CoreThemeMode mode)
    {
        var application = Application.Current;
        if (application == null)
            return;

        var palette = Palettes.TryGetValue(mode, out var selected) ? selected : Palettes[CoreThemeMode.Mocha];
        foreach (var color in palette)
            application.Resources[color.Key] = color.Value;

        foreach (var brush in BrushColors)
        {
            var color = palette[brush.Value];
            if (application.Resources[brush.Key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                existing.Color = color;
            }
            else
            {
                application.Resources[brush.Key] = new SolidColorBrush(color);
            }
        }
    }

    private static IReadOnlyDictionary<string, Color> Palette(params (string Name, string Hex)[] entries)
        => entries.ToDictionary(entry => entry.Name, entry => (Color)ColorConverter.ConvertFromString(entry.Hex)!);
}
