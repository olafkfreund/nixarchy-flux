namespace FluxKit;

// The theme setting. The order is the order of the choices.
public enum ThemeMode
{
    // The palette of the computer theme in effect. Without one, it follows
    // this computer like System.
    Computer,

    // Tokyo Night when Windows is dark, else Tokyo Night Day.
    System,

    // Tokyo Night Day.
    Light,

    // Tokyo Night.
    Dark,
}

// The rules of the theme setting. ThemeMode.swift of the Mac app has the same
// rules.
public static class ThemeModes
{
    // The key of the setting in core.Settings. The Mac app uses the same key.
    public const string SettingsKey = "appearance";

    // The choices in their order.
    public static readonly IReadOnlyList<ThemeMode> All = [ThemeMode.Computer, ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];

    // Reads a saved value. A missing or unknown value reads as Computer. The
    // old Appearance values "automatic", "light", and "dark" of the Mac read
    // as Computer, Light, and Dark.
    public static ThemeMode Parse(string? key) => key switch
    {
        "system" => ThemeMode.System,
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.Computer,
    };

    // The saved value: "computer", "system", "light", or "dark".
    public static string Key(this ThemeMode mode) => mode switch
    {
        ThemeMode.System => "system",
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        _ => "computer",
    };

    // "Computer", "System", "Light", or "Dark".
    public static string Label(this ThemeMode mode) => mode switch
    {
        ThemeMode.System => "System",
        ThemeMode.Light => "Light",
        ThemeMode.Dark => "Dark",
        _ => "Computer",
    };

    // The palette to draw. Computer takes computer, and without it follows
    // this computer like System. systemDark is true when Windows uses the
    // dark app mode.
    public static ThemePalette Palette(this ThemeMode mode, ThemePalette? computer, bool systemDark)
    {
        var device = systemDark ? ThemePalette.TokyoNight : ThemePalette.TokyoNightDay;
        return mode switch
        {
            ThemeMode.Computer => computer ?? device,
            ThemeMode.Light => ThemePalette.TokyoNightDay,
            ThemeMode.Dark => ThemePalette.TokyoNight,
            _ => device,
        };
    }

    // The light or dark mode that the app sets on its windows: Dark, Light,
    // or System to follow Windows.
    public static ThemeMode NightMode(this ThemeMode mode, ThemePalette? computer)
    {
        if (mode != ThemeMode.Computer)
        {
            return mode;
        }
        if (computer is null)
        {
            return ThemeMode.System;
        }
        return computer.Dark ? ThemeMode.Dark : ThemeMode.Light;
    }

    // The second line of the Computer choice. It names the theme that the
    // choice draws now and the computer that sent it, for example
    // "cotton-candy from omarchy-desk". Without a computer theme, it names the
    // Tokyo Night palette that the app draws until a computer sends its
    // theme. themeComputer is the name of the computer that sent theme.
    // scopeComputer is the name of the computer in scope. Give null for a
    // missing name. An empty name counts as no name.
    public static string ComputerLine(ComputerTheme? theme, string? themeComputer, string? scopeComputer, bool systemDark)
    {
        static string? Named(string? s) => string.IsNullOrEmpty(s) ? null : s;
        if (theme is null)
        {
            var fallback = systemDark ? "Tokyo Night" : "Tokyo Night Day";
            return Named(scopeComputer) is { } scope
                ? $"{fallback} until {scope} sends its theme"
                : $"{fallback} until a computer sends its theme";
        }
        var name = Named(theme.Name);
        var computer = Named(themeComputer);
        return (name, computer) switch
        {
            ({ } n, { } c) => $"{n} from {c}",
            ({ } n, null) => n,
            (null, { } c) => $"The theme of {c}",
            _ => "The theme of a computer",
        };
    }
}
