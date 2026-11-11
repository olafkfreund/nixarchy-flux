using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// The battery and the computer notifications against a headless fluxd. A
// headless fluxd has no Do Not Disturb backend, so SystemPluginTests checks
// that feature against a computer on loopback.
public class SystemEndToEndTests
{
    static LanConfig Lan(Fluxd fluxd, int udpPort, int tcpPort) => new()
    {
        UdpPort = udpPort,
        PeerUdpPort = fluxd.UdpPort,
        FirstTcpPort = tcpPort,
        LastTcpPort = tcpPort + 8,
        LoopbackOnly = true,
    };

    [FluxdFact]
    public async Task NotifyReachesTheNotificationBackend()
    {
        await using var fluxd = await Fluxd.StartAsync(32016, 32020);
        using var t = new TestCore(Lan(fluxd, 32031, 32040), [new NotificationsPlugin()]);
        var shown = new SystemNotificationRecorder();
        t.Core.Host.Notifier.Backend = shown;
        var computer = await EndToEndTests.PairFromWindowsAsync(fluxd, t);

        await fluxd.CliAsync("notify", "Build done", "make took 42s");
        await Wait.UntilAsync(() => shown.Shown.Count == 1, "the notification from fluxd", fluxd);
        var n = shown.Shown[0];
        Assert.StartsWith($"computer-{computer.Id}:flux-", n.Id);
        Assert.Equal(NotificationsPlugin.Category, n.Category);
        Assert.Equal("Build done", n.Title);
        Assert.Equal("make took 42s", n.Body);
        // fluxd sends its own name as the app name, so only the computer name shows.
        Assert.Equal(computer.Name, n.Subtitle);
        Assert.True(n.Sound);
    }

    [FluxdFact]
    public async Task BatteryReachesFluxd()
    {
        await using var fluxd = await Fluxd.StartAsync(32066, 32070);
        var battery = new FakeBattery(new BatteryState(57, true));
        using var t = new TestCore(Lan(fluxd, 32081, 32090), [new BatteryPlugin(battery), new StreamRequestPlugin(new FakeStreamStarter())]);
        await EndToEndTests.PairFromWindowsAsync(fluxd, t);
        var id = t.Core.Local.DeviceId;

        await Wait.UntilAsync(async () => (await fluxd.DeviceAsync(id))?["battery"] is JsonObject b &&
                                          Json.Int(b["charge"]) == 57 && Json.Bool(b["charging"]) == true,
            "the battery on fluxd", fluxd);
        var device = (await fluxd.DeviceAsync(id))!;
        var plugins = Json.Strings(device["plugins"]);
        Assert.Contains("battery", plugins);
        // This computer can take a request for the webcam and the mic.
        Assert.Contains("streamrequest", plugins);

        battery.Change(new BatteryState(14, false));
        await Wait.UntilAsync(async () => (await fluxd.DeviceAsync(id))?["battery"] is JsonObject b &&
                                          Json.Int(b["charge"]) == 14 && Json.Bool(b["charging"]) == false,
            "the battery change on fluxd", fluxd);

        // A headless fluxd cannot take a webcam, so it sends no request.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fluxd.CliAsync("webcam", "start"));
        Assert.Contains("headless", refused.Message);
    }
}
