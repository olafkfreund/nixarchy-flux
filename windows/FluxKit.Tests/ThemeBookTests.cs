using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// Ported from ThemeBookTests.swift of the Mac app.
public class ThemeBookTests
{
    static readonly OmarchyTheme A = SampleThemes.Neon;
    static readonly OmarchyTheme B = SampleThemes.CatppuccinLatte;
    static readonly OmarchyTheme B2 = SampleThemes.CottonCandy;

    static OmarchyTheme? LastTheme(ThemeBook book) => book.Current(null)?.Theme;

    [Fact]
    public void TheFirstThemeBecomesTheLastTheme()
    {
        var book = new ThemeBook();
        Assert.Null(book.Current(null));
        Assert.True(book.Put("A", A));
        Assert.Equal("A", book.LastId);
        Assert.Equal(A, LastTheme(book));
        Assert.Equal(ThemePalette.Of(A), book.Current(null)?.Palette);
        Assert.Equal("A", book.Theme("A")?.DeviceId);
    }

    [Fact]
    public void AReconnectWithTheSameThemeKeepsTheLastTheme()
    {
        var book = new ThemeBook();
        book.Put("A", A);
        // B sends its first theme. It does not take the place of the theme of A.
        Assert.True(book.Put("B", B));
        Assert.Equal(A, LastTheme(book));
        // Both computers connect again in turns, with the same themes.
        for (var i = 0; i < 3; i++)
        {
            Assert.False(book.Put("A", A));
            Assert.False(book.Put("B", B));
            Assert.Equal(A, LastTheme(book));
            Assert.False(book.Put("B", B));
            Assert.False(book.Put("A", A));
            Assert.Equal(A, LastTheme(book));
        }
        // The theme on B changes, so B has the last theme.
        Assert.True(book.Put("B", B2));
        Assert.Equal(B2, LastTheme(book));
        // A connects again with the same theme. The last theme stays.
        Assert.False(book.Put("A", A));
        Assert.Equal(B2, LastTheme(book));
        Assert.Equal(new Dictionary<string, string> { ["A"] = "neon", ["B"] = "cotton-candy" }, book.Names());
    }

    [Fact]
    public void TheComputerWithTheLastThemeCanChangeItAgain()
    {
        var book = new ThemeBook();
        book.Put("A", A);
        book.Put("B", B);
        Assert.True(book.Put("A", B2));
        Assert.Equal("A", book.LastId);
        Assert.Equal(B2, LastTheme(book));
    }

    [Fact]
    public void AColdStartRestoresTheBook()
    {
        var first = new ThemeBook();
        first.Put("A", A);
        first.Put("B", B);
        first.Put("B", B2);
        var saved = JsonNode.Parse(first.ToJson().ToJsonString());
        Assert.NotNull(saved);

        var next = new ThemeBook();
        next.Load(saved);
        Assert.Equal("B", next.LastId);
        Assert.Equal(B2, LastTheme(next));
        Assert.Equal(first.Current(null), next.Current(null));
        Assert.Equal(first.Names(), next.Names());
        // The computers connect after the cold start and send the same themes.
        Assert.False(next.Put("A", A));
        Assert.False(next.Put("B", B2));
        Assert.Equal(B2, LastTheme(next));
    }

    [Fact]
    public void AnUnpairOfTheLastComputerTakesTheNextTheme()
    {
        var book = new ThemeBook();
        book.Put("A", A);
        book.Put("B", B);
        book.Put("B", B2);
        Assert.False(book.Forget("C"));
        Assert.True(book.Forget("B"));
        Assert.Equal("A", book.LastId);
        Assert.Equal(A, LastTheme(book));
        Assert.True(book.Forget("A"));
        Assert.Null(book.LastId);
        Assert.Null(book.Current(null));
        Assert.Empty(book.Names());
    }

    [Fact]
    public void AnUnpairOfAnotherComputerKeepsTheLastTheme()
    {
        var book = new ThemeBook();
        book.Put("A", A);
        book.Put("B", B);
        Assert.True(book.Forget("B"));
        Assert.Equal(A, LastTheme(book));
    }

    [Fact]
    public void AScopeShowsOnlyTheThemeOfThatComputer()
    {
        var book = new ThemeBook();
        book.Put("A", A);
        book.Put("B", B);
        Assert.Equal(A, book.Current("A")?.Theme);
        Assert.Equal(B, book.Current("B")?.Theme);
        // C sent no theme. The app does not draw the theme of another computer
        // for it.
        Assert.Null(book.Current("C"));
        Assert.Equal(A, book.Current(null)?.Theme);
    }

    [Fact]
    public void ALoadKeepsOnlyThePairedComputers()
    {
        var first = new ThemeBook();
        first.Put("A", A);
        first.Put("B", B);
        first.Put("B", B2);
        var next = new ThemeBook();
        next.Load(first.ToJson(), id => id == "A");
        Assert.Equal("A", next.LastId);
        Assert.Null(next.Current("B"));
        Assert.Equal(["A"], next.Names().Keys);
    }

    [Fact]
    public void ALoadIgnoresWhatItCannotRead()
    {
        var book = new ThemeBook();
        book.Put("A", A);
        book.Load(null);
        Assert.Null(book.Current(null));
        var broken = JsonNode.Parse("""
            {"last":"X","computers":[{"theme":{"colors":{"background":"#000000"}}},
            {"id":"B","theme":{"colors":{}}},{"id":"C","theme":"red"},
            {"id":"D","theme":{"name":"bare","colors":{"background":"#101010"}}}]}
            """);
        Assert.NotNull(broken);
        book.Load(broken);
        Assert.Equal(["D"], book.Names().Keys);
        // The saved last computer is not in the book, so the last entry takes
        // its place.
        Assert.Equal("D", book.LastId);
    }

    [Fact]
    public void ACopyDoesNotChangeWithTheBook()
    {
        var book = new ThemeBook();
        book.Put("A", A);
        var copy = book.Copy();
        book.Put("B", B);
        book.Forget("A");
        Assert.Equal("A", copy.LastId);
        Assert.Equal(["A"], copy.Names().Keys);
        Assert.Equal(A, copy.Current(null)?.Theme);
    }
}
