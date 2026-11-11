using FluxKit;

namespace FluxKit.Tests;

// Ported from ThemeModeTests.swift of the Mac app: the theme setting, the
// light or dark mode of the windows, and the line of the Computer choice.
public class ThemeModeTests
{
    [Fact]
    public void TheComputerSettingFollowsTheComputerOrWindows()
    {
        var computer = ThemePalette.Of(SampleThemes.Neon);
        Assert.Equal(computer, ThemeMode.Computer.Palette(computer, systemDark: false));
        Assert.Equal(ThemePalette.TokyoNight, ThemeMode.Computer.Palette(null, systemDark: true));
        Assert.Equal(ThemePalette.TokyoNightDay, ThemeMode.Computer.Palette(null, systemDark: false));
        Assert.Equal(ThemePalette.TokyoNight, ThemeMode.System.Palette(computer, systemDark: true));
        Assert.Equal(ThemePalette.TokyoNightDay, ThemeMode.System.Palette(computer, systemDark: false));
        Assert.Equal(ThemePalette.TokyoNightDay, ThemeMode.Light.Palette(computer, systemDark: true));
        Assert.Equal(ThemePalette.TokyoNight, ThemeMode.Dark.Palette(computer, systemDark: false));
    }

    [Fact]
    public void TheNightModeFollowsTheComputerTheme()
    {
        var neon = ThemePalette.Of(SampleThemes.Neon);
        Assert.Equal(ThemeMode.Dark, ThemeMode.Computer.NightMode(neon));
        Assert.Equal(ThemeMode.Light, ThemeMode.Computer.NightMode(ThemePalette.Of(SampleThemes.CatppuccinLatte)));
        Assert.Equal(ThemeMode.System, ThemeMode.Computer.NightMode(null));
        Assert.Equal(ThemeMode.Light, ThemeMode.Light.NightMode(neon));
        Assert.Equal(ThemeMode.Dark, ThemeMode.Dark.NightMode(null));
        Assert.Equal(ThemeMode.System, ThemeMode.System.NightMode(neon));
    }

    [Fact]
    public void ComputerIsTheDefaultSetting()
    {
        Assert.Equal(ThemeMode.Computer, ThemeModes.Parse(null));
        Assert.Equal(ThemeMode.Computer, ThemeModes.Parse("unknown"));
        Assert.Equal(ThemeMode.System, ThemeModes.Parse("system"));
        Assert.Equal(ThemeMode.Computer, ThemeModes.Parse("computer"));
        Assert.Equal("appearance", ThemeModes.SettingsKey);
    }

    [Fact]
    public void TheOldAppearanceValuesStay()
    {
        // The Mac kept Automatic, Light, or Dark under the same key.
        Assert.Equal(ThemeMode.Computer, ThemeModes.Parse("automatic"));
        Assert.Equal(ThemeMode.Light, ThemeModes.Parse("light"));
        Assert.Equal(ThemeMode.Dark, ThemeModes.Parse("dark"));
    }

    [Fact]
    public void TheChoicesAndTheirLabels()
    {
        Assert.Equal([ThemeMode.Computer, ThemeMode.System, ThemeMode.Light, ThemeMode.Dark], ThemeModes.All);
        Assert.Equal(["Computer", "System", "Light", "Dark"], ThemeModes.All.Select(m => m.Label()));
        Assert.Equal(["computer", "system", "light", "dark"], ThemeModes.All.Select(m => m.Key()));
        Assert.All(ThemeModes.All, m => Assert.Equal(m, ThemeModes.Parse(m.Key())));
    }

    [Fact]
    public void TheComputerLine()
    {
        var named = new ComputerTheme("A", SampleThemes.CottonCandy, ThemePalette.TokyoNight);
        var unnamed = new ComputerTheme("A", SampleThemes.CottonCandy with { Name = "" }, ThemePalette.TokyoNight);
        Assert.Equal("Tokyo Night until omarchy-desk sends its theme",
            ThemeModes.ComputerLine(null, null, "omarchy-desk", systemDark: true));
        Assert.Equal("Tokyo Night Day until a computer sends its theme",
            ThemeModes.ComputerLine(null, null, null, systemDark: false));
        Assert.Equal("cotton-candy from omarchy-desk",
            ThemeModes.ComputerLine(named, "omarchy-desk", null, systemDark: true));
        Assert.Equal("cotton-candy",
            ThemeModes.ComputerLine(named, null, null, systemDark: true));
        Assert.Equal("The theme of omarchy-desk",
            ThemeModes.ComputerLine(unnamed, "omarchy-desk", null, systemDark: true));
        Assert.Equal("The theme of a computer",
            ThemeModes.ComputerLine(unnamed, "", "", systemDark: false));
    }
}
