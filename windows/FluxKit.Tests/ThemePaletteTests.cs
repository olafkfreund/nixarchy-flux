using System.Globalization;
using FluxKit;

namespace FluxKit.Tests;

// Ported from ThemePaletteTests.swift of the Mac app. The reference values
// come from the Kotlin code of Flux for Android and from the Swift code,
// which give the same palettes.
public class ThemePaletteTests
{
    static readonly string[] FillNames = ["accent", "cyan", "green", "magenta", "orange", "red", "yellow"];

    static string Hex(int c) => ColorMath.Hex(c);

    static void AssertContrast(string what, int a, int b, double least)
    {
        var r = ColorMath.Contrast(a, b);
        Assert.True(r >= least, $"{what}: {Hex(a)} on {Hex(b)} is {r.ToString("F2", CultureInfo.InvariantCulture)}:1, needs {least}:1");
    }

    // Asserts each contrast that the palette promises, see the contract of
    // ThemePalette.
    static void AssertReadable(ThemePalette p)
    {
        var text = ThemePalette.TextContrast;
        var nonText = ThemePalette.NonTextContrast;
        Assert.Equal([.. p.Surfaces, p.Line, p.AccentTile], p.Containers);
        foreach (var s in p.Containers)
        {
            AssertContrast($"{p.Name} text", p.Text, s, text);
            AssertContrast($"{p.Name} sub", p.Sub, s, text);
        }
        AssertContrast($"{p.Name} text against sub", p.Text, p.Sub, ThemePalette.TextStep);
        foreach (var s in new[] { p.Bg, p.Tile, p.TileHi })
        {
            AssertContrast($"{p.Name} dim", p.Dim, s, nonText);
        }
        foreach (var (name, c) in FillNames.Zip(p.Fills))
        {
            foreach (var s in p.Surfaces.Append(p.AccentTile))
            {
                AssertContrast($"{p.Name} {name} as text", c, s, text);
            }
            AssertContrast($"{p.Name} {name} as an icon on line", c, p.Line, nonText);
            AssertContrast($"{p.Name} ink on {name}", p.OnAccent, c, text);
        }
        foreach (var s in p.Surfaces)
        {
            AssertContrast($"{p.Name} terminal blue", p.TermBlue, s, text);
        }
        Assert.NotEmpty(p.Border);
        foreach (var b in p.Border)
        {
            foreach (var s in new[] { p.Bg, p.Tile })
            {
                AssertContrast($"{p.Name} border", b, s, nonText);
            }
        }
        AssertApart(p);
        // The selected tile has the luminance of TileHi, so it does not change
        // a contrast.
        Assert.True(ColorMath.Contrast(p.AccentTile, p.TileHi) < 1.03, $"{p.Name} accent tile");
    }

    // Asserts that the accent does not look like red, because red means
    // "needs you".
    static void AssertApart(ThemePalette p)
    {
        var d = ColorMath.Distance(p.Accent, p.Red);
        Assert.True(d >= ThemePalette.MinAccentDistance,
            $"{p.Name}: the accent {Hex(p.Accent)} and red {Hex(p.Red)} are {d.ToString("F3", CultureInfo.InvariantCulture)} apart");
    }

    static double HueDistance(double a, double b)
    {
        var d = Math.Abs(a - b) % (2 * Math.PI);
        return d > Math.PI ? 2 * Math.PI - d : d;
    }

    // Asserts that the guard kept the hue of a color with a visible chroma.
    static void AssertSameHue(string what, int from, int to)
    {
        var a = ColorMath.ToOklch(from);
        var b = ColorMath.ToOklch(to);
        if (a.C < 0.04 || b.C < 0.04)
        {
            return;
        }
        Assert.True(HueDistance(a.H, b.H) < 0.08, $"{what}: the hue moved from {Hex(from)} to {Hex(to)}");
    }

    // Asserts that 2 colors differ by 1 at most in each channel. Floating
    // point can give a difference of 1.
    static void AssertNear(int a, int b, string what)
    {
        foreach (var shift in new[] { 16, 8, 0 })
        {
            var x = (a >> shift) & 0xFF;
            var y = (b >> shift) & 0xFF;
            Assert.True(Math.Abs(x - y) <= 1, $"{what}: {Hex(a)} is not near {Hex(b)}");
        }
    }

    [Fact]
    public void ContrastFollowsWcag()
    {
        Assert.Equal(21.0, ColorMath.Contrast(0x000000, 0xFFFFFF), 0.01);
        Assert.Equal(1.0, ColorMath.Contrast(0x7AA2F7, 0x7AA2F7), 0.0001);
        // The well-known lightest gray that passes AA on white.
        Assert.Equal(4.54, ColorMath.Contrast(0x767676, 0xFFFFFF), 0.01);
        Assert.Equal(ColorMath.Contrast(0x16161E, 0xC0CAF5), ColorMath.Contrast(0xC0CAF5, 0x16161E));
    }

    [Fact]
    public void TheGuardKeepsAPassingColor()
    {
        Assert.Equal(0xC0CAF5, ColorMath.Guarded(0xC0CAF5, [new ContrastNeed(0x16161E, ThemePalette.TextContrast)], lighter: true));
    }

    [Fact]
    public void TheGuardMovesOnlyTheLightness()
    {
        int[] tiles = [0x16161E, 0x1F2335, 0x24283B];
        // The old Tokyo Night dim, 2.35 to 2.91:1 on the tiles.
        var moved = ColorMath.Guarded(0x565F89, tiles.Select(t => new ContrastNeed(t, ThemePalette.TextContrast)).ToArray(), lighter: true);
        foreach (var t in tiles)
        {
            AssertContrast("moved dim", moved, t, ThemePalette.TextContrast);
        }
        Assert.True(ColorMath.ToOklch(moved).L > ColorMath.ToOklch(0x565F89).L);
        AssertSameHue("dim", 0x565F89, moved);
        // A saturated red on a light tile goes darker and stays red.
        var red = ColorMath.Guarded(0xF52A65, [new ContrastNeed(0xE9EAEF, ThemePalette.TextContrast)], lighter: false);
        AssertContrast("red", red, 0xE9EAEF, ThemePalette.TextContrast);
        AssertSameHue("red", 0xF52A65, red);
    }

    [Fact]
    public void TheFallbackPalettesAreReadable()
    {
        AssertReadable(ThemePalette.TokyoNight);
        AssertReadable(ThemePalette.TokyoNightDay);
        Assert.True(ThemePalette.TokyoNight.Dark);
        Assert.False(ThemePalette.TokyoNightDay.Dark);
    }

    [Fact]
    public void TheFallbackPalettesKeepTheValuesOfTheReview()
    {
        var day = ThemePalette.TokyoNightDay;
        var night = ThemePalette.TokyoNight;
        // Light: the true Tokyo Night Day page, a neutral ink, and white on the
        // primary fill.
        Assert.Equal(0xE1E2E7, day.Bg);
        Assert.Equal(0x2457B8, day.Accent);
        Assert.Equal(0xFFFFFF, day.OnAccent);
        Assert.True(ColorMath.Contrast(day.OnAccent, day.Accent) > 6.5);
        Assert.True(ColorMath.ToOklch(day.Text).C < 0.06, "the light body ink is a neutral");
        // The light body ink and the accent no longer look alike.
        Assert.True(ColorMath.ToOklch(day.Accent).C > 3 * ColorMath.ToOklch(day.Text).C);
        Assert.Equal(0x44518A, day.Sub);
        Assert.Equal(0x8B94BE, night.Sub);
        Assert.Equal(0x16161E, night.Bg);
        Assert.Equal(0xC0CAF5, night.Text);
        Assert.Equal(night.Accent, night.TermBlue);
        Assert.Equal([night.Accent, night.Cyan], night.Border);
        Assert.Null(night.BorderAngle);
    }

    [Fact]
    public void FallbackAccentTiles()
    {
        AssertNear(ThemePalette.TokyoNight.AccentTile, 0x1E2845, "Tokyo Night accent tile");
        AssertNear(ThemePalette.TokyoNightDay.AccentTile, 0xCBD6EE, "Tokyo Night Day accent tile");
    }

    [Fact]
    public void TheRolesGiveEachColorAsRgb()
    {
        var p = ThemePalette.TokyoNight;
        Assert.Equal(19, p.Roles.Count);
        Assert.Equal(p.Roles.Count, p.Roles.Select(r => r.Role).Distinct().Count());
        var bg = p.Roles.Single(r => r.Role == "Bg").Color;
        Assert.Equal(new Rgb(0x16, 0x16, 0x1E), bg);
        Assert.Equal(0x16161E, bg.Value);
        Assert.Equal("#16161e", bg.Hex);
        Assert.Equal(Rgb.Of(p.Accent), p.Roles.Single(r => r.Role == "Accent").Color);
        Assert.Equal(p.Border.Select(Rgb.Of), p.BorderColors);
    }

    [Fact]
    public void TheThemeOfTheUserKeepsItsColors()
    {
        var p = ThemePalette.Of(SampleThemes.Neon);
        AssertReadable(p);
        Assert.True(p.Dark);
        // The colors that pass stay as they are.
        Assert.Equal(0x0C031F, p.Bg);
        Assert.Equal(0xE8E6EF, p.Text);
        Assert.Equal(0xD563FE, p.Accent);
        Assert.Equal(0xFE288F, p.Red);
        Assert.Equal(0x21E4F8, p.Cyan);
        // The accent, not the lime blue, is the primary color. The lime blue
        // is the ANSI blue.
        Assert.NotEqual(0xBDFF6D, p.Accent);
        Assert.Equal(0xBDFF6D, p.TermBlue);
        // The selected tile moves toward the purple of the accent.
        Assert.True(ColorMath.Distance(p.AccentTile, p.Accent) < ColorMath.Distance(p.TileHi, p.Accent));
        // The muted color gets lighter for its 3:1 and keeps its hue.
        Assert.NotEqual(0x665A8C, p.Dim);
        AssertSameHue("muted", 0x665A8C, p.Dim);
        // The ink on the accent is the dark page, which reads better than the
        // light text.
        Assert.Equal(p.Bg, p.OnAccent);
        Assert.True(ColorMath.Contrast(p.OnAccent, p.Accent) > ColorMath.Contrast(p.Text, p.Accent));
        // The tiles are lighter steps of the page, with its purple hue.
        Assert.True(ColorMath.Luminance(p.Tile) > ColorMath.Luminance(p.Bg));
        Assert.True(ColorMath.Luminance(p.TileHi) > ColorMath.Luminance(p.Tile));
        AssertSameHue("tile", 0x0C031F, p.Tile);
        // The border comes from the theme, at its angle.
        Assert.Equal([0x21E4F8, 0xD563FE], p.Border);
        Assert.Equal(45, p.BorderAngle);
    }

    [Fact]
    public void TokyoNightFromAComputerStaysTokyoNight()
    {
        var p = ThemePalette.Of(SampleThemes.TokyoNight);
        AssertReadable(p);
        Assert.Equal(0x1A1B26, p.Bg);
        Assert.Equal(0xC0CAF5, p.Text);
        Assert.Equal(0x7AA2F7, p.Accent);
        Assert.Equal(0xF7768E, p.Red);
        Assert.Equal(0x24283B, p.TileHi);
        Assert.True(ColorMath.Luminance(p.Tile) > ColorMath.Luminance(p.Bg));
    }

    [Fact]
    public void TokyoNightDayGetsANeutralInkAndDarkerColors()
    {
        var theme = SampleThemes.TokyoNightDay;
        var p = ThemePalette.Of(theme);
        AssertReadable(p);
        Assert.False(p.Dark);
        Assert.Equal(0xE1E2E7, p.Bg);
        // The body ink was the accent blue. Now it is a neutral with the same hue.
        Assert.True(ColorMath.ToOklch(p.Text).C <= 0.05);
        AssertSameHue("red", theme["red"]!.Value, p.Red);
        AssertSameHue("accent", theme["accent"]!.Value, p.Accent);
        // The tiles are the darker steps of the page.
        Assert.True(ColorMath.Luminance(p.Tile) < ColorMath.Luminance(p.Bg));
        Assert.True(ColorMath.Luminance(p.TileHi) < ColorMath.Luminance(p.Tile));
        // The light ink reads better on the dark accent.
        Assert.True(ColorMath.Luminance(p.OnAccent) > ColorMath.Luminance(p.Accent));
    }

    [Fact]
    public void CatppuccinLatteIsReadable()
    {
        var theme = SampleThemes.CatppuccinLatte;
        var p = ThemePalette.Of(theme);
        AssertReadable(p);
        Assert.False(p.Dark);
        Assert.Equal(0xEFF1F5, p.Bg);
        Assert.Equal(0xE6E9EF, p.Tile);
        Assert.Equal(0xDCE0E8, p.TileHi);
        // The body ink is a little darker than the foreground, so that the
        // second ink can sit between it and the page.
        Assert.True(ColorMath.Luminance(p.Text) < ColorMath.Luminance(0x4C4F69));
        AssertSameHue("text", 0x4C4F69, p.Text);
        // The ANSI blue is the theme blue, not the mauve accent.
        AssertSameHue("terminal blue", 0x1E66F5, p.TermBlue);
        foreach (var key in new[] { "red", "green", "yellow", "cyan", "magenta" })
        {
            var index = Array.IndexOf(FillNames, key);
            AssertSameHue(key, theme[key]!.Value, p.Fills[index]);
        }
        // 1 border color makes a solid border.
        Assert.Single(p.Border);
    }

    [Fact]
    public void AnAccentThatLooksLikeRedGivesWayToTheThemeBlue()
    {
        // cotton-candy: the accent #e1a4ed and red #f097c5 are 2 pinks.
        var theme = SampleThemes.CottonCandy;
        Assert.True(ColorMath.Distance(theme["accent"]!.Value, theme["red"]!.Value) < ThemePalette.MinAccentDistance);
        var p = ThemePalette.Of(theme);
        AssertReadable(p);
        Assert.Equal(0x8EAFFE, p.Accent);
        Assert.Equal(0xF097C5, p.Red);
        Assert.Equal(0x61E6FF, p.Cyan);
        Assert.Equal(0x8EAFFE, p.TermBlue);
        // The border keeps the accent of the theme, so the theme stays
        // recognizable.
        Assert.Equal([0x61E6FF, 0xE1A4ED], p.Border);
        // The selected tile moves toward the new accent.
        Assert.True(ColorMath.Distance(p.AccentTile, p.Accent) < ColorMath.Distance(p.TileHi, p.Accent));
    }

    [Fact]
    public void AnAccentThatIsRedGivesWayToAColorWithChroma()
    {
        // futurism: the accent is red, and blue and cyan are a near white.
        var theme = SampleThemes.Futurism;
        Assert.Equal(theme["accent"], theme["red"]);
        var p = ThemePalette.Of(theme);
        AssertReadable(p);
        // The near white has no chroma, so bright_blue takes the place of the
        // accent.
        Assert.Equal(0x00BFFF, p.Accent);
        AssertSameHue("red", 0xFF40A3, p.Red);
        // The terminal keeps the near white blue of the theme.
        Assert.Equal(0xF0F8FF, p.TermBlue);
        // Without a theme border, the gradient starts at the accent of the
        // theme.
        AssertSameHue("border", 0xFF40A3, p.Border[0]);
        Assert.Null(p.BorderAngle);
    }

    [Fact]
    public void AThemeWithoutChromaTakesTheTokyoNightAccent()
    {
        // snow: each color is the same near black, so no theme color can be
        // the accent.
        const int ink = 0x0A0A0A;
        string[] keys = ["foreground", "accent", "red", "green", "yellow", "orange", "cyan", "blue", "magenta",
            "bright_blue", "bright_cyan", "bright_magenta"];
        var colors = new Dictionary<string, int> { ["background"] = 0xFFFFFF, ["muted"] = 0x919191 };
        foreach (var key in keys)
        {
            colors[key] = ink;
        }
        var p = ThemePalette.Of(new OmarchyTheme("snow", false, colors));
        AssertReadable(p);
        Assert.False(p.Dark);
        AssertSameHue("accent", ThemePalette.TokyoNightDay.Accent, p.Accent);
        Assert.Equal(ink, p.Red);
    }

    [Fact]
    public void AnAccentFarFromRedStays()
    {
        foreach (var theme in new[] { SampleThemes.Neon, SampleThemes.TokyoNight, SampleThemes.CatppuccinLatte })
        {
            var p = ThemePalette.Of(theme);
            AssertSameHue($"{theme.Name} accent", theme["accent"]!.Value, p.Accent);
        }
    }

    [Fact]
    public void TheTintKeepsTheLuminance()
    {
        var t = ColorMath.Tint(0x24283B, 0x7AA2F7, 0.18);
        Assert.NotEqual(0x24283B, t);
        Assert.Equal(ColorMath.Luminance(0x24283B), ColorMath.Luminance(t), 0.002);
        Assert.True(ColorMath.Distance(t, 0x7AA2F7) < ColorMath.Distance(0x24283B, 0x7AA2F7));
        Assert.Equal(0, ColorMath.Distance(0x7AA2F7, 0x7AA2F7));
        Assert.True(ColorMath.Distance(0xE1A4ED, 0xF097C5) < ThemePalette.MinAccentDistance);
        Assert.True(ColorMath.Distance(0x8EAFFE, 0xF097C5) > ThemePalette.MinAccentDistance);
    }

    [Fact]
    public void ALowContrastThemeBecomesReadable()
    {
        var theme = SampleThemes.LowContrast;
        var p = ThemePalette.Of(theme);
        AssertReadable(p);
        Assert.True(p.Dark);
        // The page goes darker so that the ink can reach its contrast.
        Assert.True(ColorMath.Luminance(p.Bg) < ColorMath.Luminance(theme["background"]!.Value));
        Assert.True(ColorMath.Contrast(p.Text, p.Bg) >= ThemePalette.TextContrast);
        AssertSameHue("red", theme["red"]!.Value, p.Red);
        // The gray blue accent is too close to the gray red, and the theme has
        // no other color with chroma. The Tokyo Night blue takes the place of
        // the accent.
        AssertSameHue("accent", ThemePalette.TokyoNight.Accent, p.Accent);
    }

    [Fact]
    public void AThemeWithFewColorsTakesTheRestFromTokyoNight()
    {
        var dark = ThemePalette.Of(new OmarchyTheme("bare", null, new Dictionary<string, int> { ["background"] = 0x101010, ["foreground"] = 0xEEEEEE }));
        AssertReadable(dark);
        Assert.True(dark.Dark);
        Assert.Equal(ThemePalette.TokyoNight.Red, dark.Red);
        var light = ThemePalette.Of(new OmarchyTheme("bare", false, new Dictionary<string, int> { ["accent"] = 0x8839EF }));
        AssertReadable(light);
        Assert.False(light.Dark);
        Assert.Equal(ThemePalette.TokyoNightDay.Bg, light.Bg);
        // Without a border, the gradient goes from the accent to cyan, corner
        // to corner.
        Assert.Equal([light.Accent, light.Cyan], light.Border);
        Assert.Null(light.BorderAngle);
    }

    [Fact]
    public void TheBackgroundDecidesTheMode()
    {
        var p = ThemePalette.Of(new OmarchyTheme("wrong mode", true, new Dictionary<string, int> { ["background"] = 0xF7F7F7, ["foreground"] = 0x2B2426 }));
        Assert.False(p.Dark);
        Assert.Equal(0xF7F7F7, p.Bg);
        AssertReadable(p);
    }

    [Fact]
    public void ADarkBorderGetsLighter()
    {
        var p = ThemePalette.Of(new OmarchyTheme("dark border", true, new Dictionary<string, int> { ["background"] = 0x0C031F },
            [0x1A0A30, 0x0C031F], 90));
        AssertReadable(p);
        Assert.Equal(2, p.Border.Count);
        Assert.Equal(90, p.BorderAngle);
    }

    [Fact]
    public void EverySampleIsReadable()
    {
        foreach (var theme in SampleThemes.All)
        {
            AssertReadable(ThemePalette.Of(theme));
        }
    }

    // Reference values

    // A palette from the 19 colors in the order of Roles.
    static ThemePalette Reference(string name, bool dark, int[] v, int[] border, double? angle) =>
        new(name, dark, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12],
            v[13], v[14], v[15], v[16], v[17], v[18], ValueList.From(border), angle);

    static void AssertNearPalette(ThemePalette got, ThemePalette want)
    {
        Assert.Equal(want.Name, got.Name);
        Assert.Equal(want.Dark, got.Dark);
        foreach (var ((role, g), (_, w)) in got.Roles.Zip(want.Roles))
        {
            AssertNear(g.Value, w.Value, $"{want.Name} {role}");
        }
        Assert.Equal(want.Border.Count, got.Border.Count);
        foreach (var (g, w) in got.Border.Zip(want.Border))
        {
            AssertNear(g, w, $"{want.Name} border");
        }
        Assert.Equal(want.BorderAngle, got.BorderAngle);
    }

    // The full palettes of the samples. The Kotlin code of Flux for Android
    // and the Swift code of the Mac app give the same values.
    [Fact]
    public void SamplePalettesMatchTheReferenceValues()
    {
        (OmarchyTheme Theme, ThemePalette Palette)[] want =
        [
            (SampleThemes.Neon, Reference("neon", true, [
                0x0C031F, 0x130A26, 0x1A112C, 0x231B35, 0x332B44, 0x4E485E, 0xE8E6EF, 0xA6A2B1, 0x6E6295, 0x0C031F,
                0xD563FE, 0x2E1242, 0x21E4F8, 0x9ECE6A, 0xBB9AF7, 0xFF9E64, 0xFE288F, 0xE0AF68, 0xBDFF6D,
            ], [0x21E4F8, 0xD563FE], 45)),
            (SampleThemes.TokyoNight, Reference("tokyo-night", true, [
                0x1A1B26, 0x1D1F2D, 0x202333, 0x24283B, 0x30354A, 0x464C64, 0xC0CAF5, 0x959DBE, 0x66709B, 0x1A1B26,
                0x7AA2F7, 0x1E2845, 0x7DCFFF, 0x9ECE6A, 0xBB9AF7, 0xFF9E64, 0xF7768E, 0xE0AF68, 0x7AA2F7,
            ], [0x33CCFF, 0x00FF99], 45)),
            (SampleThemes.TokyoNightDay, Reference("tokyo-night-day", false, [
                0xE1E2E7, 0xDBDCE1, 0xD5D6DB, 0xCECFD4, 0xC2C6D2, 0xADB7CF, 0x323D55, 0x4B5365, 0x6B729A, 0xE1E2E7,
                0x0055B8, 0xC1D0E9, 0x006081, 0x456125, 0x772BCA, 0x8A4600, 0xB20042, 0x725325, 0x0055B8,
            ], [0x0055B8, 0x006081], null)),
            (SampleThemes.CatppuccinLatte, Reference("catppuccin-latte", false, [
                0xEFF1F5, 0xEBEDF2, 0xE6E9EF, 0xDCE0E8, 0xD0D4DE, 0xBCC0CC, 0x41445E, 0x585B6D, 0x7C7F8F, 0xEFF1F5,
                0x8230E7, 0xE5DBFF, 0x006A95, 0x1C7300, 0xA5368B, 0xAD4000, 0xC80033, 0x8E5600, 0x0D57E5,
            ], [0x8839EF], 0)),
            (SampleThemes.CottonCandy, Reference("cotton-candy", true, [
                0x191125, 0x1D152A, 0x21192F, 0x271F35, 0x372F44, 0x524B5E, 0xE9E6EF, 0xABA6B2, 0x73678C, 0x191125,
                0x8EAFFE, 0x21203D, 0x61E6FF, 0x58E3DC, 0xE1A4ED, 0xFEB79E, 0xF097C5, 0xF9DD7D, 0x8EAFFE,
            ], [0x61E6FF, 0xE1A4ED], 45)),
            (SampleThemes.Futurism, Reference("futurism", true, [
                0x0A1428, 0x0E1B32, 0x12213C, 0x17294A, 0x283A58, 0x475772, 0xF0F8FF, 0xABB4BF, 0x64738C, 0x0A1428,
                0x00BFFF, 0x002B4C, 0xF0F8FF, 0x00BFFF, 0xFF42A4, 0xFF7AB8, 0xFF42A4, 0x6991CF, 0xF0F8FF,
            ], [0xFF42A4, 0xF0F8FF], null)),
            (SampleThemes.LowContrast, Reference("low-contrast", true, [
                0x424242, 0x464646, 0x4A4A4A, 0x4F4F4F, 0x515151, 0x565656, 0xE6E6E6, 0xC3C3C3, 0x9D9D9D, 0x424242,
                0xA3C1FF, 0x494F5E, 0xA8C7C7, 0xB3C5B3, 0xCDB4FF, 0xFFAE7F, 0xE4B5B4, 0xC2C2A3, 0xA3C1FF,
            ], [0x979797, 0x979797], null)),
        ];
        foreach (var (theme, palette) in want)
        {
            AssertNearPalette(ThemePalette.Of(theme), palette);
        }
    }
}
