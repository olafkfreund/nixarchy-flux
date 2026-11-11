using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// Ported from ThemePluginTests.swift of the Mac app. The plugin saves the
// theme book, loads it for the paired computers, and forgets the theme of a
// computer that is no longer paired.
public class ThemePluginTests
{
    static readonly LanConfig Offline = new() { LoopbackOnly = true };

    [Fact]
    public void AThemeIsSavedOnce()
    {
        var plugin = new ThemePlugin();
        using var t = new TestCore(Offline, [plugin]);
        Assert.Null(t.Core.Settings.Node(ThemePlugin.SettingsKey));
        Assert.Null(plugin.Model.Current(null));

        plugin.Receive(SampleThemes.Neon, "A");
        var saved = t.Core.Settings.Node(ThemePlugin.SettingsKey);
        Assert.NotNull(saved);
        Assert.Equal(SampleThemes.Neon, plugin.Model.Current(null)?.Theme);
        Assert.Equal(new Dictionary<string, string> { ["A"] = "neon" }, plugin.Model.Names());

        // A reconnect sends the same theme again, which changes nothing.
        plugin.Receive(SampleThemes.Neon, "A");
        Assert.True(JsonNode.DeepEquals(saved, t.Core.Settings.Node(ThemePlugin.SettingsKey)));

        plugin.Receive(SampleThemes.CatppuccinLatte, "B");
        Assert.False(JsonNode.DeepEquals(saved, t.Core.Settings.Node(ThemePlugin.SettingsKey)));
    }

    [Fact]
    public void ALoadKeepsOnlyThePairedComputers()
    {
        var plugin = new ThemePlugin();
        using var t = new TestCore(Offline, [plugin]);
        plugin.Receive(SampleThemes.Neon, "A");
        plugin.Receive(SampleThemes.CatppuccinLatte, "B");
        var saved = t.Core.Settings.Node(ThemePlugin.SettingsKey);

        var next = new ThemePlugin();
        next.Load(saved, new HashSet<string> { "A" });
        Assert.Equal(SampleThemes.Neon, next.Model.Current("A")?.Theme);
        Assert.Null(next.Model.Current("B"));
        Assert.Equal("A", next.Model.Current(null)?.DeviceId);
        Assert.Equal(["A"], next.Model.Names().Keys);

        next.Load(null, new HashSet<string> { "A" });
        Assert.Null(next.Model.Current(null));
    }

    [Fact]
    public void AnUnpairForgetsTheThemeAndSaves()
    {
        var plugin = new ThemePlugin();
        using var t = new TestCore(Offline, [plugin]);
        plugin.Receive(SampleThemes.Neon, "A");
        plugin.Forget("B");
        Assert.Equal(SampleThemes.Neon, plugin.Model.Current(null)?.Theme);
        plugin.Forget("A");
        Assert.Null(plugin.Model.Current(null));
        var saved = t.Core.Settings.Node(ThemePlugin.SettingsKey) as JsonObject;
        Assert.NotNull(saved);
        Assert.Empty((saved["computers"] as JsonArray)!);
        Assert.False(saved.ContainsKey("last"));
    }

    [Fact]
    public void TheStartLoadsTheBookOfThePairedComputersAndTheSetting()
    {
        var data = CoreTests.TempDir();
        try
        {
            var book = new ThemeBook();
            book.Put("A", SampleThemes.Neon);
            book.Put("B", SampleThemes.CatppuccinLatte);
            var settings = new FluxSettings(Path.Combine(data, "settings.json"));
            settings.Set(ThemePlugin.SettingsKey, book.ToJson());
            settings.Set(ThemeModes.SettingsKey, "dark");
            new TrustStore(Path.Combine(data, "trusted.json")).Put(
                new TrustedDevice("A", "omarchy-desk", "desktop", Convert.ToBase64String([1, 2, 3]), "", true));

            var plugin = new ThemePlugin();
            using var core = new FluxCore(new FluxPaths(data), new FluxHost { Post = new SerialQueue().Post }, Offline, [plugin]);
            Assert.Equal(SampleThemes.Neon, plugin.Model.Current("A")?.Theme);
            Assert.Null(plugin.Model.Current("B"));
            Assert.Equal("A", plugin.Model.Last?.DeviceId);
            Assert.Equal(ThemeMode.Dark, plugin.Model.Mode);
            Assert.Equal(ThemePalette.TokyoNight, plugin.Model.Palette(null, systemDark: false));
            plugin.SetMode(ThemeMode.Computer);
            Assert.Equal(ThemePalette.Of(SampleThemes.Neon), plugin.Model.Palette(null, systemDark: false));
            // B is not paired, so the palette follows Windows.
            Assert.Equal(ThemePalette.TokyoNightDay, plugin.Model.Palette("B", systemDark: false));
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }

    [Fact]
    public void TheSettingIsSaved()
    {
        var plugin = new ThemePlugin();
        using var t = new TestCore(Offline, [plugin]);
        Assert.Equal(ThemeMode.Computer, plugin.Model.Mode);
        Assert.Equal(ThemePalette.TokyoNightDay, plugin.Model.Palette(null, systemDark: false));
        plugin.SetMode(ThemeMode.Light);
        Assert.Equal(ThemeMode.Light, plugin.Model.Mode);
        Assert.Equal("light", t.Core.Settings.String(ThemeModes.SettingsKey));
        plugin.Receive(SampleThemes.Neon, "A");
        Assert.Equal(ThemePalette.TokyoNightDay, plugin.Model.Palette(null, systemDark: true));
    }

    [Fact]
    public async Task APacketReachesTheModelAndAnUnpairForgetsIt()
    {
        var plugin = new ThemePlugin();
        using var t = new TestCore(Offline, [plugin]);
        var changes = new ConcurrentQueue<string?>();
        plugin.Model.PropertyChanged += (_, e) => changes.Enqueue(e.PropertyName);
        var device = new Device(t.Core, new Identity("desk_0123456789", "omarchy-desk", "desktop", Identity.Version, [PacketType.Tunnel], []))
        {
            PairState = PairState.Paired,
        };
        // A body without a color is no theme.
        plugin.Handle(new Packet(PacketType.Theme, new JsonObject { ["name"] = "empty" }), device);
        plugin.Handle(new Packet(PacketType.Theme, SampleThemes.Neon.ToJson()), device);
        await Wait.UntilAsync(() => plugin.Model.Last is not null, "the theme in the model");
        Assert.Equal(SampleThemes.Neon, plugin.Model.Last!.Theme);
        Assert.Equal(device.Id, plugin.Model.Last.DeviceId);
        Assert.Contains(nameof(ThemeModel.Book), changes);
        Assert.Contains(nameof(ThemeModel.Last), changes);

        // A link that only drops keeps the theme.
        plugin.OnDisconnected(device);
        var flushed = new TaskCompletionSource();
        t.Core.Host.Post(flushed.SetResult);
        await flushed.Task;
        Assert.NotNull(plugin.Model.Last);

        device.PairState = PairState.None;
        plugin.OnDisconnected(device);
        await Wait.UntilAsync(() => plugin.Model.Last is null, "the forget after the unpair");
    }
}
