namespace FluxKit;

// flux.ping in both directions.
public sealed class PingPlugin : IFluxPlugin
{
    FluxCore? core;

    public IReadOnlyList<string> Incoming { get; } = [PacketType.Ping];
    public IReadOnlyList<string> Outgoing { get; } = [PacketType.Ping];

    public void Attach(FluxCore core) => this.core = core;

    public void Handle(Packet packet, Device device)
    {
        var message = packet.String("message") ?? "Ping";
        core?.Toast($"{message} from {device.Name}");
    }

    // Sends a ping to a paired, connected computer.
    public void Ping(string deviceId)
    {
        if (core is null)
        {
            return;
        }
        core.Toast(core.Send(new Packet(PacketType.Ping), deviceId) ? "Ping sent" : "Not connected. Try again in a moment");
    }
}
