using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// Ported from OmarchyThemeTests.swift of the Mac app: the theme packet.
public class ThemePacketTests
{
    static JsonObject Body(string json) => JsonNode.Parse(json) as JsonObject ?? [];

    [Fact]
    public void ReadsTheThemePacket()
    {
        const string line = """{"id":1,"type":"flux.theme","body":{"name":"synthwave","mode":"dark","colors":{"background":"#0c031f","foreground":"#E8E6EF","accent":"#d563fe","bright_red":"#fe83af"},"border":{"colors":["#21e4f8ee","#d563feee"],"angle":45}}}""";
        var p = Packet.Parse(line)!;
        Assert.Equal(PacketType.Theme, p.Type);
        var t = OmarchyTheme.Parse(p.Body)!;
        Assert.Equal("synthwave", t.Name);
        Assert.True(t.Dark);
        Assert.Equal(0x0C031F, t["background"]);
        Assert.Equal(0xE8E6EF, t["foreground"]);
        Assert.Equal(0xFE83AF, t["bright_red"]);
        // The border drops the alpha of Hyprland.
        Assert.Equal([0x21E4F8, 0xD563FE], t.Border);
        Assert.Equal(45, t.BorderAngle);
    }

    [Fact]
    public void EachKeyCanBeMissing()
    {
        var t = OmarchyTheme.Parse(Body("""{"colors":{"background":"#fdf6e3"}}"""))!;
        Assert.Equal("", t.Name);
        Assert.Null(t.Dark);
        Assert.Empty(t.Border);
        Assert.Null(t.BorderAngle);
        var light = OmarchyTheme.Parse(Body("""{"mode":"light","border":{"colors":["#ffffff"]}}"""))!;
        Assert.False(light.Dark);
        Assert.Empty(light.Colors);
    }

    [Fact]
    public void IgnoresWhatItCannotRead()
    {
        const string json = """{"mode":"sepia","colors":{"background":"#12345","foreground":"rgb(1,2,3)","Accent":"#ffffff","red":7,"green":"#00ff00"},"border":{"colors":["#zzzzzz",3,"#00ff00"],"angle":"-90deg"}}""";
        var t = OmarchyTheme.Parse(Body(json))!;
        Assert.Null(t.Dark);
        Assert.Equal(new Dictionary<string, int> { ["green"] = 0x00FF00 }, t.Colors);
        Assert.Equal([0x00FF00], t.Border);
        Assert.Equal(270, t.BorderAngle);
    }

    [Fact]
    public void ABodyWithoutColorsIsNoTheme()
    {
        Assert.Null(OmarchyTheme.Parse(Body("""{"name":"empty"}""")));
        Assert.Null(OmarchyTheme.Parse(Body("""{"colors":{"background":"blue"}}""")));
    }

    [Fact]
    public void LimitsTheSizes()
    {
        var colors = string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"c{i}\":\"#010203\""));
        var border = string.Join(",", Enumerable.Repeat("\"#ffffff\"", 20));
        var name = new string('x', 200);
        var t = OmarchyTheme.Parse(Body("{\"name\":\"" + name + "\",\"colors\":{" + colors + "},\"border\":{\"colors\":[" + border + "]}}"))!;
        Assert.Equal(OmarchyTheme.MaxColors, t.Colors.Count);
        Assert.Equal(OmarchyTheme.MaxBorder, t.Border.Count);
        Assert.Equal(OmarchyTheme.MaxName, t.Name.Length);
    }

    [Fact]
    public void TheNameCutKeepsWholeCharacters()
    {
        // 64 flags of 2 code points each, and 1 more.
        var flags = string.Concat(Enumerable.Repeat("\U0001F1F3\U0001F1F4", 65));
        var t = OmarchyTheme.Parse(new JsonObject { ["name"] = flags, ["colors"] = new JsonObject { ["background"] = "#000000" } })!;
        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F1F3\U0001F1F4", 64)), t.Name);
    }

    [Fact]
    public void TheSavedFormReadsBackTheSame()
    {
        foreach (var t in SampleThemes.All)
        {
            Assert.Equal(t, OmarchyTheme.Parse(t.ToJson()));
        }
        Assert.Equal("#0c031f", ColorMath.Hex(0x0C031F));
        Assert.Equal(0x0C031F, ColorMath.ParseColor(" #0C031Fee "));
        Assert.Null(ColorMath.ParseColor("#0c031"));
        // A fullwidth digit is not a hex digit.
        Assert.Null(ColorMath.ParseColor("#０c031f"));
    }

    [Fact]
    public void ThemesCompareByValue()
    {
        var a = new OmarchyTheme("t", true, new Dictionary<string, int> { ["background"] = 1, ["accent"] = 2 }, [3], 45);
        var b = new OmarchyTheme("t", true, new Dictionary<string, int> { ["accent"] = 2, ["background"] = 1 }, [3], 45);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Name = "u" });
        Assert.NotEqual(a, new OmarchyTheme("t", true, new Dictionary<string, int> { ["background"] = 1, ["accent"] = 4 }, [3], 45));
    }

    [Fact]
    public void TheDeviceAsksForTheTheme()
    {
        Assert.Equal("flux.theme", PacketType.Theme);
        Assert.Equal([PacketType.Theme], new ThemePlugin().Incoming);
        Assert.Empty(new ThemePlugin().Outgoing);
        using var t = new TestCore(new LanConfig { LoopbackOnly = true }, [new ThemePlugin()]);
        Assert.Contains(PacketType.Theme, t.Core.IncomingCapabilities);
        Assert.DoesNotContain(PacketType.Theme, t.Core.OutgoingCapabilities);
    }
}
