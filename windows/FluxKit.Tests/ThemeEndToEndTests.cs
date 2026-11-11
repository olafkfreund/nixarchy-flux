using FluxKit;

namespace FluxKit.Tests;

// The theme against a headless fluxd. A headless fluxd has no theme file,
// so it sends no flux.theme: internal/core/theme.go and docs/omarchy.md say
// so. The test checks that the pairing works with the theme plugin and that
// no theme arrives. A test of a received theme needs a fluxd with a theme.
public class ThemeEndToEndTests
{
    [FluxdFact]
    public async Task HeadlessFluxdSendsNoTheme()
    {
        await using var fluxd = await Fluxd.StartAsync(31166, 31170);
        var theme = new ThemePlugin();
        using var t = new TestCore(new LanConfig
        {
            UdpPort = 31181,
            PeerUdpPort = fluxd.UdpPort,
            FirstTcpPort = 31190,
            LastTcpPort = 31198,
            LoopbackOnly = true,
        }, [theme]);
        Assert.Contains(PacketType.Theme, t.Core.IncomingCapabilities);
        await EndToEndTests.PairFromWindowsAsync(fluxd, t);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Null(theme.Model.Last);
        Assert.Null(t.Core.Settings.Node(ThemePlugin.SettingsKey));
        Assert.Equal(ThemePalette.TokyoNight, theme.Model.Palette(null, systemDark: true));
    }
}
