using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Cleanup.App.Services;

/// <summary>
/// Builds the Light (Daybreak) and Dark (Ledger) theme dictionaries from the token XAML files: every <c>XColor</c>
/// also becomes an <c>XBrush</c>, the red → orange accent gradient becomes <c>AccentGradientBrush</c>, and WinUI's own
/// accent resources are pointed at the Cleanup accent so native controls (checkboxes, rings, toggles) match.
/// </summary>
public static class ThemeLoader
{
    public static void Install(ResourceDictionary resources)
    {
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Themes/Controls.xaml") });
        resources.ThemeDictionaries["Light"] = Build("ms-appx:///Themes/Daybreak.xaml", dark: false);
        resources.ThemeDictionaries["Dark"] = Build("ms-appx:///Themes/Ledger.xaml", dark: true);
    }

    private static ResourceDictionary Build(string source, bool dark)
    {
        var tokens = new ResourceDictionary { Source = new Uri(source) };
        var theme = new ResourceDictionary();
        var colors = new Dictionary<string, Color>();
        foreach (var (key, value) in tokens)
        {
            if (key is string name && value is Color color && name.EndsWith("Color", StringComparison.Ordinal))
            {
                colors[name[..^5]] = color;
                theme[name] = color;
                theme[name[..^5] + "Brush"] = new SolidColorBrush(color);
            }
        }

        var start = colors["AccentStart"];
        var end = colors["AccentEnd"];
        theme["AccentGradientBrush"] = Gradient(start, end);
        theme["AccentGradientSoftBrush"] = Gradient(WithAlpha(start, 0x26), WithAlpha(end, 0x26));
        theme["HeroGradientBrush"] = dark ? new SolidColorBrush(colors["HeroBackground"]) : Gradient(start, end);
        theme["HeroDividerBrush"] = new SolidColorBrush(WithAlpha(colors["HeroForeground"], 0x30));
        theme["CardShadowColor"] = dark ? Color.FromArgb(0x55, 0, 0, 0) : Color.FromArgb(0x1A, 0x13, 0x16, 0x13);

        var accent = colors["Accent"];
        var onAccent = colors["OnAccent"];
        foreach (var key in (string[])["SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
                     "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"])
        {
            theme[key] = accent;
        }
        theme["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        theme["AccentFillColorSecondaryBrush"] = new SolidColorBrush(WithAlpha(accent, 0xE6));
        theme["AccentFillColorTertiaryBrush"] = new SolidColorBrush(WithAlpha(accent, 0xCC));
        theme["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(colors["AccentInk"]);
        theme["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(colors["AccentInk"]);
        theme["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(onAccent);
        theme["TextOnAccentFillColorSecondaryBrush"] = new SolidColorBrush(onAccent);

        // Checkboxes: the gradient fill with a near-black check, as on macOS (white fails AA on the gradient).
        foreach (var state in (string[])["", "PointerOver", "Pressed"])
        {
            theme["CheckBoxCheckBackgroundFillChecked" + state] = Gradient(start, end);
            theme["CheckBoxCheckBackgroundFillIndeterminate" + state] = Gradient(start, end);
            theme["CheckBoxCheckBackgroundStrokeChecked" + state] = new SolidColorBrush(Colors.Transparent);
            theme["CheckBoxCheckBackgroundStrokeIndeterminate" + state] = new SolidColorBrush(Colors.Transparent);
            theme["CheckBoxCheckGlyphForegroundChecked" + state] = new SolidColorBrush(onAccent);
            theme["CheckBoxCheckGlyphForegroundIndeterminate" + state] = new SolidColorBrush(onAccent);
        }
        theme["ToggleSwitchFillOn"] = Gradient(start, end);
        theme["ToggleSwitchFillOnPointerOver"] = Gradient(start, end);
        theme["ToggleSwitchKnobFillOn"] = new SolidColorBrush(onAccent);
        theme["ToggleSwitchKnobFillOnPointerOver"] = new SolidColorBrush(onAccent);
        theme["ProgressBarForeground"] = Gradient(start, end);
        return theme;
    }

    public static LinearGradientBrush Gradient(Color start, Color end) => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops = { new GradientStop { Color = start, Offset = 0 }, new GradientStop { Color = end, Offset = 1 } },
    };

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
