using System.Globalization;
using System.Xml.Linq;

namespace Cleanup.Core.Tests.Shared;

/// <summary>
/// Keeps the XAML themes in <c>src/Cleanup.App/Themes</c> in step with <c>shared/design-tokens.json</c> (v2), which the
/// macOS <c>Theme</c> is also checked against, and enforces WCAG AA on the same text pairs as the Swift suite.
/// </summary>
public class DesignTokensTests
{
    private sealed record ColorToken(string Hex, double? Opacity);

    private sealed record Tokens(
        Dictionary<string, string> Fonts,
        Dictionary<string, Dictionary<string, ColorToken>> Themes,
        Dictionary<string, ColorToken> Brand,
        Dictionary<string, double> Radii,
        Dictionary<string, double> TypeSizes);

    private readonly record struct Argb(byte A, byte R, byte G, byte B);

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static Tokens LoadTokens() => SharedFiles.Load<Tokens>("design-tokens.json");

    private static XElement LoadXaml(string name) =>
        XDocument.Load(Path.Combine(SharedFiles.RepoRoot, "src", "Cleanup.App", "Themes", name)).Root!;

    private static string Key(XElement e) => (string)e.Attribute(X + "Key")!;

    private static string Pascal(string token) => char.ToUpperInvariant(token[0]) + token[1..];

    private static Dictionary<string, Argb> Colors(XElement dictionary, string suffix = "Color") =>
        dictionary.Elements(Presentation + "Color").ToDictionary(
            e => Key(e)[..^suffix.Length],
            e => ParseArgb(e.Value.Trim()));

    private static Argb ParseArgb(string text)
    {
        Assert.Matches("^#[0-9A-Fa-f]{8}$", text);
        var v = uint.Parse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static Argb FromToken(ColorToken token)
    {
        var v = uint.Parse(token.Hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new((byte)Math.Round((token.Opacity ?? 1) * 255), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static void Check(Dictionary<string, ColorToken> json, Dictionary<string, Argb> xaml, string label)
    {
        Assert.Equal(json.Keys.Select(Pascal).Order(), xaml.Keys.Order());
        foreach (var (name, token) in json)
        {
            Assert.True(FromToken(token) == xaml[Pascal(name)], $"{label}.{name}: XAML {xaml[Pascal(name)]} vs token {token}");
        }
    }

    [Theory]
    [InlineData("daybreak", "Daybreak.xaml")]
    [InlineData("ledger", "Ledger.xaml")]
    public void ThemeMatchesTokens(string theme, string file)
    {
        var tokens = LoadTokens();
        Assert.Equal(["daybreak", "ledger"], tokens.Themes.Keys.Order());
        Check(tokens.Themes[theme], Colors(LoadXaml(file)), theme);
    }

    [Fact]
    public void ThemesShareOneVocabulary()
    {
        Assert.Equal(Colors(LoadXaml("Daybreak.xaml")).Keys.Order(), Colors(LoadXaml("Ledger.xaml")).Keys.Order());
    }

    [Fact]
    public void BrandRadiiTypeSizesAndFontsMatch()
    {
        var tokens = LoadTokens();
        var xaml = LoadXaml("Tokens.xaml");
        Check(tokens.Brand, Colors(xaml), "brand");

        var doubles = xaml.Elements(X + "Double").ToDictionary(Key, e => double.Parse(e.Value, CultureInfo.InvariantCulture));
        Assert.Equal(tokens.Radii.ToDictionary(kv => "Radius" + Pascal(kv.Key), kv => kv.Value)
            .Concat(tokens.TypeSizes.ToDictionary(kv => "FontSize" + Pascal(kv.Key), kv => kv.Value))
            .OrderBy(kv => kv.Key), doubles.OrderBy(kv => kv.Key));

        // "ms-appx:///Assets/Fonts/File.ttf#Family Name": the family after '#' is the token.
        var fonts = xaml.Elements(Presentation + "FontFamily").ToDictionary(Key, e => e.Value.Split('#')[1]);
        Assert.Equal(tokens.Fonts["display"], fonts["DisplayFontFamily"]);
        Assert.Equal(tokens.Fonts["text"], fonts["TextFontFamily"]);
    }

    // Contrast (WCAG 2.x): text tokens over the surfaces they sit on, opacity blended, as in the Swift suite.

    private static double Luminance((double R, double G, double B) c)
    {
        static double Channel(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static (double R, double G, double B) Blend(ColorToken value, (double R, double G, double B) baseColor)
    {
        var v = FromToken(value with { Opacity = 1 });
        var a = value.Opacity ?? 1;
        return (v.R / 255.0 * a + baseColor.R * (1 - a), v.G / 255.0 * a + baseColor.G * (1 - a), v.B / 255.0 * a + baseColor.B * (1 - a));
    }

    private static (double, double, double) Truncate((double R, double G, double B) c) =>
        (Math.Floor(c.R * 255) / 255, Math.Floor(c.G * 255) / 255, Math.Floor(c.B * 255) / 255);

    private static double Contrast(Dictionary<string, ColorToken> theme, string fg, string bg)
    {
        var surface = Blend(theme["surface"], (0, 0, 0));
        var back = Truncate(Blend(theme[bg], surface));
        var front = Blend(theme[fg], back);
        var (l1, l2) = (Luminance(front), Luminance(back));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    [Theory]
    [InlineData("daybreak")]
    [InlineData("ledger")]
    public void TextPairsMeetAA(string name)
    {
        var theme = LoadTokens().Themes[name];
        (string Fg, string Bg)[] pairs =
        [
            ("ink", "canvas"), ("body", "surface"), ("mute", "canvas"), ("mute", "surface"), ("accentInk", "accentSoft"),
            ("onAccent", "accent"), ("warn", "warnSoft"), ("warn", "surface"), ("ok", "surface"), ("danger", "surface"),
            ("heroForeground", "heroBackground"), ("heroButtonText", "heroButton"), ("onAccent", "accentStart"),
            ("onAccent", "accentEnd"),
        ];
        Assert.All(pairs, p =>
        {
            var ratio = Contrast(theme, p.Fg, p.Bg);
            Assert.True(ratio >= 4.5, $"{name}: {p.Fg} on {p.Bg} is {ratio:F2}:1");
        });
    }
}
