using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// SystemTests.swift of the Mac app: the Do Not Disturb guard, the computer
// notifications and their limits, and the battery packet.
public class SystemDndGuardTests
{
    [Fact]
    public void LocalChangesGoOutOnce()
    {
        var g = new DndGuard();
        Assert.False(g.Local(false, 0)); // the first state is the start value
        Assert.False(g.Local(false, 10)); // the same state is not a change
        Assert.True(g.Local(true, 20)); // a new state goes to the computers
        Assert.False(g.Local(true, 30)); // a change goes out once
    }

    [Fact]
    public void RemoteChangeDoesNotEcho()
    {
        var g = new DndGuard();
        g.Local(false, 0);
        Assert.True(g.Remote(true, 100)); // a new state from a computer applies
        Assert.False(g.Local(false, 500)); // the old state during the wait is not a change
        Assert.False(g.Local(true, 800)); // the applied state does not go back
        Assert.False(g.Remote(true, 900)); // the same state from a computer does not apply again
        Assert.True(g.Local(false, 10_000)); // a later local change goes out
    }

    [Fact]
    public void FailedApplyGivesTheLocalState()
    {
        var g = new DndGuard(3_000);
        g.Local(false, 0);
        g.Remote(true, 0);
        Assert.False(g.Local(false, 2_000));
        Assert.True(g.Local(false, 4_000)); // after the wait, the local state goes out
    }

    [Fact]
    public void RemoteBeforeTheFirstLocalState()
    {
        var g = new DndGuard();
        Assert.True(g.Remote(true, 0));
        Assert.False(g.Local(true, 10));
    }

    [Fact]
    public void TheEndOfTheApplyStartsTheWaitAgain()
    {
        var g = new DndGuard(3_000);
        g.Local(false, 0);
        g.Remote(true, 0);
        // The apply took 5 seconds. The old state that comes after it is not a change.
        g.Applied(true, 5_000);
        Assert.False(g.Local(false, 6_000));
        Assert.True(g.Local(false, 8_500));
        // An apply of an older state does not move the wait.
        g.Remote(true, 10_000);
        g.Applied(false, 20_000);
        Assert.True(g.Local(false, 14_000));
    }
}

public class SystemNotificationTests
{
    static Packet Wire(JsonObject body) => Packet.Parse(new Packet(PacketType.Notification, body).Serialize())!;

    [Fact]
    public void NotificationFromFluxd()
    {
        var n = ComputerNotification.From(
            Wire(new JsonObject { ["id"] = "flux-1", ["appName"] = "omarchy-xps", ["title"] = "Build done", ["text"] = "make · 42s", ["time"] = "1790000000000", ["isClearable"] = true }),
            "pc1", "omarchy-xps")!;
        Assert.Equal("omarchy-xps", n.Subtitle);
        Assert.Equal("Build done", n.Title);
        Assert.Equal("make · 42s", n.Text);
        Assert.Equal("pc1:flux-1", n.Key);
    }

    [Fact]
    public void NotificationFromAnotherApp()
    {
        // Another app name shows next to the computer name.
        var n = ComputerNotification.From(Wire(new JsonObject { ["id"] = "7", ["appName"] = "Firefox", ["title"] = "Download done" }), "pc1", "omarchy-xps")!;
        Assert.Equal("Firefox · omarchy-xps", n.Subtitle);
        Assert.Equal("Download done", n.Title);
        Assert.Equal("", n.Text);
        // The app name compares without case.
        Assert.Equal("omarchy-xps", ComputerNotification.From(Wire(new JsonObject { ["id"] = "8", ["appName"] = "Omarchy-XPS", ["title"] = "x" }), "pc1", "omarchy-xps")!.Subtitle);
    }

    [Fact]
    public void NotificationWithoutTitleOrId()
    {
        Assert.Null(ComputerNotification.From(Wire(new JsonObject { ["id"] = "x" }), "pc1", "omarchy-xps"));
        // The text is not a title.
        Assert.Null(ComputerNotification.From(Wire(new JsonObject { ["id"] = "8", ["text"] = "only text" }), "pc1", "pc"));
        // The ticker is not a title.
        Assert.Null(ComputerNotification.From(Wire(new JsonObject { ["id"] = "9", ["ticker"] = "Download done" }), "pc1", "pc"));
        // A blank title.
        Assert.Null(ComputerNotification.From(Wire(new JsonObject { ["id"] = "10", ["title"] = "  " }), "pc1", "pc"));
        Assert.Null(ComputerNotification.From(Wire(new JsonObject { ["title"] = "no id" }), "pc1", "omarchy-xps"));
        Assert.Null(ComputerNotification.From(Wire(new JsonObject { ["id"] = " ", ["title"] = "blank id" }), "pc1", "omarchy-xps"));
        Assert.Null(ComputerNotification.From(Wire(new JsonObject { ["id"] = 5, ["title"] = "number id" }), "pc1", "omarchy-xps"));
    }

    // A computer that sends many notifications cannot bury the others.
    [Fact]
    public void NotificationLimitPerComputer()
    {
        var limit = new NotificationLimit();
        var burst = (int)NotificationLimit.Burst;
        for (var i = 0; i < burst; i++)
        {
            Assert.True(limit.Allow("pc1", 100), $"{i}");
        }
        Assert.False(limit.Allow("pc1", 100)); // the burst is used up
        Assert.True(limit.Allow("pc2", 100)); // another computer has its own limit
        Assert.False(limit.Allow("pc1", 100.5));
        Assert.True(limit.Allow("pc1", 101.1)); // 1 more each second
        Assert.False(limit.Allow("pc1", 101.2));
        for (var i = 0; i < burst; i++)
        {
            Assert.True(limit.Allow("pc1", 1000));
        }
        Assert.False(limit.Allow("pc1", 1000)); // a long pause gives the burst again, not more
    }

    // Only a notification that finds the full burst makes a sound.
    [Fact]
    public void ABurstMakesOneSound()
    {
        var limit = new NotificationLimit();
        Assert.Equal(NotificationGrant.Sound, limit.Take("pc1", 100));
        Assert.Equal(NotificationGrant.Quiet, limit.Take("pc1", 100));
        Assert.Equal(NotificationGrant.Quiet, limit.Take("pc1", 100.5));
        Assert.Equal(NotificationGrant.Sound, limit.Take("pc2", 100.5)); // another computer has its own burst
        Assert.Equal(NotificationGrant.Sound, limit.Take("pc1", 200)); // a pause fills the burst again
    }

    // A computer keeps at most 20 delivered notifications. A new one removes
    // the oldest.
    [Fact]
    public void AComputerKeepsAtMost20Notifications()
    {
        var delivered = new DeliveredNotifications();
        var removed = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            removed.AddRange(delivered.Add($"n{i}", "pc1"));
        }
        Assert.Equal(20, delivered.Delivered("pc1").Count);
        Assert.Equal("n10", delivered.Delivered("pc1")[0]);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"n{i}"), removed); // the oldest go first
        Assert.Empty(delivered.Add("n15", "pc1")); // a post with the same ID replaces that notification
        Assert.Equal(20, delivered.Delivered("pc1").Count);
        Assert.Equal("n15", delivered.Delivered("pc1")[^1]);
        Assert.Empty(delivered.Add("x", "pc2")); // another computer has its own list
    }

    // The notification IDs of a computer name its device ID, so that a
    // restart finds the notifications of each computer.
    [Fact]
    public void NotificationIdsNameTheComputer()
    {
        const string pc = "0123456789abcdef0123456789abcdef";
        Assert.Equal(pc, DeliveredNotifications.DeviceIdOf($"computer-{pc}:build:done"));
        Assert.Equal(pc, DeliveredNotifications.DeviceIdOf(DeliveredNotifications.LinkId(pc)));
        Assert.Null(DeliveredNotifications.DeviceIdOf($"share-{Guid.NewGuid()}")); // a received file has no computer
        Assert.Null(DeliveredNotifications.DeviceIdOf($"pair-{pc}"));
        Assert.Null(DeliveredNotifications.DeviceIdOf($"computer-{pc}")); // the ID needs a colon after the device ID
        Assert.Null(DeliveredNotifications.DeviceIdOf("computer-bad id:x"));
    }

    // The notifications of an earlier run count toward the limit. They are
    // older than the notifications of this run.
    [Fact]
    public void ASeedKeepsTheLimit()
    {
        const string pc1 = "0123456789abcdef0123456789abcdef";
        const string pc2 = "fedcba9876543210fedcba9876543210";
        var delivered = new DeliveredNotifications();
        Assert.Empty(delivered.Add($"computer-{pc1}:new", pc1));
        var found = Enumerable.Range(0, 25).Select(i => $"computer-{pc1}:{i}")
            .Concat([$"computer-{pc1}:new", $"computer-{pc2}:a", "share-file", $"pair-{pc1}"]).ToArray();
        var removed = delivered.Seed(found);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"computer-{pc1}:{i}"), removed); // the oldest go first
        Assert.Equal(20, delivered.Delivered(pc1).Count);
        Assert.Equal($"computer-{pc1}:6", delivered.Delivered(pc1)[0]);
        Assert.Equal($"computer-{pc1}:new", delivered.Delivered(pc1)[^1]); // a notification of this run stays the newest
        Assert.Equal([$"computer-{pc2}:a"], delivered.Delivered(pc2));
        var shown = found.Where(id => !removed.Contains(id)).ToArray();
        Assert.Empty(delivered.Seed(shown)); // a second seed of the shown notifications adds nothing
        Assert.Equal(20, delivered.Delivered(pc1).Count);
    }

    // Notifications and received links of a computer take tokens from 1
    // shared limit.
    [Fact]
    public void SharedLimitPerComputer()
    {
        var pc = Guid.NewGuid().ToString();
        for (var i = 0; i < (int)NotificationLimit.Burst; i++)
        {
            Assert.True(NotificationLimit.AllowsNow(pc), $"{i}");
        }
        Assert.False(NotificationLimit.AllowsNow(pc)); // the burst is used up
        Assert.True(NotificationLimit.AllowsNow(Guid.NewGuid().ToString())); // another computer has its own limit
    }
}

public class SystemBatteryStateTests
{
    [Fact]
    public void PacketReportsLowOnlyWhenNotCharging()
    {
        var low = new BatteryState(15, false).ToPacket();
        Assert.Equal(PacketType.Battery, low.Type);
        Assert.Equal(15, low.Int("currentCharge"));
        Assert.False(low.Bool("isCharging"));
        Assert.Equal(1, low.Int("thresholdEvent"));
        Assert.Equal(0, new BatteryState(15, true).ToPacket().Int("thresholdEvent"));
        Assert.Equal(0, new BatteryState(16, false).ToPacket().Int("thresholdEvent"));
    }

    [Fact]
    public void ComputerBatteryWithoutChargeIsNone()
    {
        Assert.Null(BatteryState.From(new Packet(PacketType.Battery, new JsonObject { ["currentCharge"] = -1, ["isCharging"] = false })));
        Assert.Null(BatteryState.From(new Packet(PacketType.Battery)));
        Assert.Equal(new BatteryState(80, true), BatteryState.From(new Packet(PacketType.Battery, new JsonObject { ["currentCharge"] = 80, ["isCharging"] = true })));
    }

    [Fact]
    public void PacketRoundTrips()
    {
        foreach (var state in new[] { new BatteryState(0, false), new BatteryState(57, true), new BatteryState(100, false) })
        {
            Assert.Equal(state, BatteryState.From(Packet.Parse(state.ToPacket().Serialize())!));
        }
    }

    [Fact]
    public void LevelMapping()
    {
        Assert.Null(BatteryState.FromLevel(-1, false)); // -1 means no battery
        Assert.Null(BatteryState.FromLevel(double.NaN, false));
        Assert.Equal(new BatteryState(0, false), BatteryState.FromLevel(0, false));
        Assert.Equal(new BatteryState(57, true), BatteryState.FromLevel(0.574, true));
        Assert.Equal(new BatteryState(58, false), BatteryState.FromLevel(0.576, false)); // the level rounds
        Assert.Equal(new BatteryState(100, true), BatteryState.FromLevel(1, true));
        Assert.Equal(new BatteryState(100, false), BatteryState.FromLevel(1.2, false)); // the charge stays at most 100
    }
}
