namespace FluxKit;

// A feature that handles packets of some types for paired computers.
//
// The core calls OnConnected, OnDisconnected, and Handle with the core lock
// held, on a thread of the pool. A plugin that blocks moves its work to a
// task. A plugin publishes UI state through its own model and events.
public interface IFluxPlugin
{
    // Packet types that this device accepts because of this plugin.
    IReadOnlyList<string> Incoming { get; }

    // Packet types that this device sends because of this plugin.
    IReadOnlyList<string> Outgoing { get; }

    // Packet types that the core routes to this plugin. The core reads them
    // once, before the network starts. The default is Incoming. A plugin whose
    // Incoming changes with a setting lists every type that it may accept.
    IReadOnlyList<string> HandledTypes => Incoming;

    // Called once, after the core exists and before the network starts.
    void Attach(FluxCore core)
    {
    }

    // A paired computer connected, or a connected computer finished pairing.
    void OnConnected(Device device)
    {
    }

    // The link to a paired computer closed, or the pairing ended.
    void OnDisconnected(Device device)
    {
    }

    // A packet with a type from HandledTypes arrived from a paired computer.
    void Handle(Packet packet, Device device);
}
