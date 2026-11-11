namespace FluxKit;

// A notification that a computer sends to this computer, from a
// flux.notification packet: flux-cli notify, or flux-cli notify --run.
// ComputerNotification.swift of the Mac app has the same rules.
public sealed record ComputerNotification(string Key, string Subtitle, string Title, string Text)
{
    // Reads the packet from the computer with the ID and the name. Key is the
    // device ID, a colon, and the ID of the packet, so that the same computer
    // and ID replace the old notification. The app name of the packet shows
    // next to the computer name, when it is another name. It returns null for
    // a packet without an ID or without a title.
    public static ComputerNotification? From(Packet p, string deviceId, string computer)
    {
        if (p.String("id") is not { } id || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        var title = (p.String("title") ?? "").Trim();
        if (title.Length == 0)
        {
            return null;
        }
        var app = (p.String("appName") ?? "").Trim();
        var subtitle = app.Length == 0 || string.Equals(app, computer, StringComparison.OrdinalIgnoreCase)
            ? computer
            : $"{app} · {computer}";
        return new ComputerNotification($"{deviceId}:{id}", subtitle, title, (p.String("text") ?? "").Trim());
    }
}
