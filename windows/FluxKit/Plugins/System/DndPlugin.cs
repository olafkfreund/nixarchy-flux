using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace FluxKit;

// The Do Not Disturb state of this computer. Windows has no public API that
// turns Do Not Disturb on or off, so the app reports the state when it can
// read it, and sets CanApply to false when it cannot apply a state. Tests use
// a fake.
public interface IDndSource
{
    // The Do Not Disturb state of this computer, or null when the app cannot
    // read it.
    bool? Read();

    // The app raises it after the state of this computer changes, with the
    // new state, on any thread.
    event Action<bool>? Changed;

    // False when this computer cannot turn Do Not Disturb on or off. A
    // computer then shows the state of the computers and changes nothing.
    bool CanApply { get; }

    // Turns Do Not Disturb on or off. It returns null after a change, or the
    // error message. It must not throw.
    Task<string?> ApplyAsync(bool on);
}

// Syncs Do Not Disturb between this computer and the computers with flux.dnd
// {"on": bool}. Each side sends the state only after a local change, and a
// guard keeps a change from going back to the side that made it. A change
// that a computer did not get, because it had no link or Flux did not run,
// waits in a FocusBacklog until the computer connects. DndPlugin.swift of
// the Mac app has the same rules.
public sealed class DndPlugin : IFluxPlugin
{
    // The setting "Sync Do Not Disturb". The default is on.
    internal const string SyncKey = "dnd.sync";

    readonly IDndSource? source;
    readonly DndGuard dndGuard;
    FluxCore? core;
    FocusBacklog? backlog;

    // source is null on a computer that cannot read or apply Do Not
    // Disturb. settleMs is how long a state from a computer waits for the
    // report of this computer. The apply and the report can take a moment,
    // so the default is longer than on a phone.
    public DndPlugin(IDndSource? source = null, long settleMs = 10_000)
    {
        this.source = source;
        dndGuard = new DndGuard(settleMs);
    }

    public DndModel Model { get; } = new();

    public IReadOnlyList<string> Incoming { get; } = [PacketType.Dnd];
    public IReadOnlyList<string> Outgoing { get; } = [PacketType.Dnd];

    public void Attach(FluxCore core)
    {
        this.core = core;
        backlog = new FocusBacklog(core.Settings);
        var canApply = source?.CanApply ?? false;
        core.Host.Post(() => Model.Load(core.Settings, canApply));
        if (source is null)
        {
            return;
        }
        source.Changed += FocusChanged;
        if (source.Read() is { } on)
        {
            Start(on);
        }
    }

    // Takes the state at the start, so that the start is not a change. A
    // state that differs from the last one that Flux saw is a change that
    // came while Flux did not run. It waits for the paired computers.
    void Start(bool on)
    {
        dndGuard.Local(on, Now());
        core?.Host.Post(() => Model.FocusOn = on);
        if (core is not null && backlog is not null)
        {
            backlog.Report(on, PairedIds(core), Sync(core));
        }
    }

    // Takes a state that this computer reports. A local change goes to the
    // connected computers now and waits in the backlog for the other
    // computers that are paired now.
    internal void FocusChanged(bool on)
    {
        core?.Host.Post(() => Model.FocusOn = on);
        if (core is null || backlog is null || !dndGuard.Local(on, Now()))
        {
            return;
        }
        var sync = Sync(core);
        var waits = backlog.Report(on, PairedIds(core), sync);
        if (!sync)
        {
            return;
        }
        FluxLog.Info("dnd", $"Do Not Disturb is {OnOff(on)} on this computer");
        var reached = Send(core, on, except: null);
        if (waits)
        {
            backlog.Reached(reached, on);
        }
    }

    // Applies the state of a computer. The core lock is held, so the apply
    // runs in a task.
    public void Handle(Packet packet, Device device)
    {
        if (core is null || packet.Bool("on") is not { } on)
        {
            return;
        }
        var from = device.Id;
        core.Host.Post(() => Model.Computers = Model.Computers.SetItem(from, on));
        if (!Sync(core))
        {
            return;
        }
        if (source is not { CanApply: true })
        {
            FluxLog.Info("dnd", $"{device.Name} turned Do Not Disturb {OnOff(on)}, and this computer cannot follow");
            return;
        }
        if (!dndGuard.Remote(on, Now()))
        {
            return;
        }
        FluxLog.Info("dnd", $"{device.Name} turned Do Not Disturb {OnOff(on)}");
        _ = Task.Run(() => ApplyAsync(core, source, on, from));
    }

    // Applies the state, and then gives it to the other computers. The
    // origin of the state does not get it back.
    async Task ApplyAsync(FluxCore core, IDndSource source, bool on, string from)
    {
        var error = await source.ApplyAsync(on).ConfigureAwait(false);
        dndGuard.Applied(on, Now());
        if (error is not null)
        {
            FluxLog.Error("dnd", $"cannot turn Do Not Disturb {OnOff(on)}: {error}");
            core.Toast($"Flux cannot turn Do Not Disturb {OnOff(on)}: {error}");
        }
        core.Host.Post(() => Model.LastError = error);
        if (!Sync(core) || backlog is null)
        {
            return;
        }
        var others = PairedIds(core).Where(id => id != from).ToArray();
        var waits = backlog.Report(on, others);
        var reached = Send(core, on, except: from);
        if (waits)
        {
            backlog.Reached(reached, on);
        }
    }

    // Sends a change that came while this computer was not connected. The
    // core lock is held, so it sends on the device.
    public void OnConnected(Device device)
    {
        if (core is null || backlog is null || !Sync(core) || !device.Accepts(PacketType.Dnd) ||
            backlog.Take(device.Id) is not { } on)
        {
            return;
        }
        device.Send(DndPacket(on));
    }

    // Called when the link closes and after an unpair. An unpaired computer
    // stops its wait for a change.
    public void OnDisconnected(Device device)
    {
        var id = device.Id;
        if (!device.Paired)
        {
            backlog?.Forget(id);
        }
        core?.Host.Post(() => Model.Computers = Model.Computers.Remove(id));
    }

    static bool Sync(FluxCore core) => core.Settings.Bool(SyncKey, true);

    static IReadOnlyList<string> PairedIds(FluxCore core) => core.Trust.All().Select(t => t.Id).ToArray();

    static Packet DndPacket(bool on) => new(PacketType.Dnd, new JsonObject { ["on"] = on });

    // Sends the state to each connected computer that accepts flux.dnd,
    // except the computer with the ID except. It returns the computers that
    // got it.
    static List<string> Send(FluxCore core, bool on, string? except)
    {
        var reached = new List<string>();
        foreach (var id in core.ConnectedPairedIds(PacketType.Dnd))
        {
            if (id != except && core.Send(DndPacket(on), id))
            {
                reached.Add(id);
            }
        }
        return reached;
    }

    static string OnOff(bool on) => on ? "on" : "off";

    static long Now() => Environment.TickCount64;
}

// The Do Not Disturb settings and state, for the UI.
public sealed class DndModel : Model
{
    FluxSettings? settings;
    bool? focusOn;
    bool sync = true;
    bool canApply;
    string? lastError;
    ImmutableDictionary<string, bool> computers = ImmutableDictionary<string, bool>.Empty;

    // The Do Not Disturb state of this computer, or null before the first report.
    public bool? FocusOn
    {
        get => focusOn;
        internal set => Set(ref focusOn, value);
    }

    // The setting "Sync Do Not Disturb".
    public bool Sync
    {
        get => sync;
        set
        {
            if (Set(ref sync, value))
            {
                settings?.Set(DndPlugin.SyncKey, value);
            }
        }
    }

    // False when this computer cannot turn Do Not Disturb on or off. The UI
    // then says that this computer only shows the state of the computers.
    public bool CanApply
    {
        get => canApply;
        internal set => Set(ref canApply, value);
    }

    // The last error of an apply, or null after an apply that worked.
    public string? LastError
    {
        get => lastError;
        internal set => Set(ref lastError, value);
    }

    // The Do Not Disturb state that each connected computer sent, by device ID.
    public ImmutableDictionary<string, bool> Computers
    {
        get => computers;
        internal set => Set(ref computers, value);
    }

    internal void Load(FluxSettings settings, bool canApply)
    {
        Sync = settings.Bool(DndPlugin.SyncKey, true);
        CanApply = canApply;
        this.settings = settings;
    }
}
