using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace FluxKit;

// A battery charge in percent and whether the charger is plugged in.
public sealed record BatteryState(int Charge, bool Charging)
{
    // At this charge or lower, a battery that does not charge is low. fluxd
    // uses the same limit.
    public const int LowCharge = 15;

    // Reads a flux.battery packet. A negative charge means that the device
    // has no battery.
    public static BatteryState? From(Packet p) =>
        p.Int("currentCharge") is { } charge && charge >= 0 ? new BatteryState(charge, p.Bool("isCharging") ?? false) : null;

    // The flux.battery packet. thresholdEvent 1 means "battery low": 15% or
    // less and not charging.
    public Packet ToPacket() => new(PacketType.Battery, new JsonObject
    {
        ["currentCharge"] = Charge,
        ["isCharging"] = Charging,
        ["thresholdEvent"] = Charge <= LowCharge && !Charging ? 1 : 0,
    });

    // Maps a battery level from 0 to 1. A negative level means that the
    // system reports no battery.
    public static BatteryState? FromLevel(double level, bool charging)
    {
        if (!(level >= 0))
        {
            return null;
        }
        var charge = (int)Math.Min(100, Math.Max(0, Math.Round(level * 100, MidpointRounding.AwayFromZero)));
        return new BatteryState(charge, charging);
    }
}

// The battery of this computer. The Windows app reads the power status of
// Windows. Tests use a fake.
public interface IBatterySource
{
    // The battery of this computer, or null when it has none. Charging means
    // that the computer runs on AC power. The core calls it with the core
    // lock held, so it must not block.
    BatteryState? Read();

    // The app raises it after the charge or the power source changes, on
    // any thread.
    event Action? Changed;
}

// flux.battery in both directions. This computer sends its battery when a
// computer connects and when the battery changes. A computer without a
// battery neither advertises nor sends a battery. The battery that a
// computer sends shows in the model. BatteryPlugin.swift of the Mac app has
// the same rules.
public sealed class BatteryPlugin : IFluxPlugin
{
    readonly IBatterySource? source;
    readonly Lock gate = new();
    FluxCore? core;
    // The last state that went to the computers.
    BatteryState? last;

    // source is null on a computer that cannot read its battery.
    public BatteryPlugin(IBatterySource? source = null)
    {
        this.source = source;
        last = source?.Read();
        HasBattery = last is not null;
        Outgoing = HasBattery ? [PacketType.Battery] : [];
    }

    // The batteries of the computers.
    public BatteryModel Model { get; } = new();

    // True when this computer has a battery. The value does not change while
    // Flux runs, because the capabilities come from it.
    public bool HasBattery { get; }

    public IReadOnlyList<string> Incoming { get; } = [PacketType.Battery];
    public IReadOnlyList<string> Outgoing { get; }

    public void Attach(FluxCore core)
    {
        this.core = core;
        if (HasBattery && source is not null)
        {
            source.Changed += () => BatteryChanged(source.Read());
        }
    }

    public void OnConnected(Device device)
    {
        if (!HasBattery || !device.Accepts(PacketType.Battery) || source?.Read() is not { } state)
        {
            return;
        }
        device.Send(state.ToPacket());
    }

    public void OnDisconnected(Device device)
    {
        var id = device.Id;
        core?.Host.Post(() => Model.Computers = Model.Computers.Remove(id));
    }

    public void Handle(Packet packet, Device device)
    {
        var id = device.Id;
        var state = BatteryState.From(packet);
        core?.Host.Post(() => Model.Computers = state is null ? Model.Computers.Remove(id) : Model.Computers.SetItem(id, state));
    }

    // Sends a new state to each connected computer that accepts flux.battery.
    void BatteryChanged(BatteryState? state)
    {
        if (core is null || state is null)
        {
            return;
        }
        lock (gate)
        {
            if (state == last)
            {
                return;
            }
            last = state;
        }
        var p = state.ToPacket();
        foreach (var id in core.ConnectedPairedIds(PacketType.Battery))
        {
            core.Send(p, id);
        }
    }
}

// The batteries that the computers report, for the UI.
public sealed class BatteryModel : Model
{
    ImmutableDictionary<string, BatteryState> computers = ImmutableDictionary<string, BatteryState>.Empty;

    // The battery of each computer, by device ID. A computer without a
    // battery has no entry.
    public ImmutableDictionary<string, BatteryState> Computers
    {
        get => computers;
        internal set => Set(ref computers, value);
    }

    // The battery of the computer, or null.
    public BatteryState? Of(string deviceId) => computers.TryGetValue(deviceId, out var s) ? s : null;
}
