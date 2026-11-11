namespace FluxKit;

// Packet types that Flux uses. internal/proto/identity.go in the Go source
// is the reference.
public static class PacketType
{
    public const string Identity = "flux.identity";
    public const string Pair = "flux.pair";
    public const string Ping = "flux.ping";
    public const string Battery = "flux.battery";
    public const string Clipboard = "flux.clipboard";
    public const string ClipboardConnect = "flux.clipboard.connect";
    public const string Share = "flux.share.request";
    public const string ShareUpdate = "flux.share.request.update";
    public const string Notification = "flux.notification";
    public const string RunCommand = "flux.runcommand";
    public const string RunCommandRequest = "flux.runcommand.request";
    public const string Mpris = "flux.mpris";
    public const string MprisRequest = "flux.mpris.request";
    public const string Sftp = "flux.sftp";
    public const string SftpRequest = "flux.sftp.request";

    // Moves the pointer, clicks, scrolls, and types on the computer.
    // docs/remote-input.md describes the body.
    public const string MousepadRequest = "flux.mousepad.request";

    // This device opens a listener that the computer connects to.
    public const string Tunnel = "flux.tunnel";

    // Resumable file transfers in chunks. docs/workflows.md describes them.
    public const string Transfer = "flux.transfer";

    // This device streams its camera to the computer as a virtual webcam.
    public const string Webcam = "flux.webcam";

    // The Do Not Disturb state, {"on": bool}, after a local change.
    public const string Dnd = "flux.dnd";

    // This device streams its microphone to the computer as a virtual source.
    public const string Mic = "flux.mic";

    // The herdr agents of the computer. Both sides send it.
    public const string Herdr = "flux.herdr";

    // This device streams its screen to a window on the computer.
    public const string Screen = "flux.screen";

    // The computer tells whether it accepts remote input and whether it
    // shows its screen, {"enabled": bool, "desktop": bool}.
    public const string Input = "flux.input";

    // The computer streams its screen to a window on this device.
    public const string Desktop = "flux.desktop";

    // The Hyprland key bindings, workspaces, and windows of the computer.
    // Both sides send it.
    public const string Shortcuts = "flux.shortcuts";

    // The computer asks this device to approve sudo or polkit.
    public const string Approve = "flux.approve";

    // The active Omarchy theme of the computer. docs/omarchy.md, section
    // "Theme packet", describes the body.
    public const string Theme = "flux.theme";

    // The computer asks this device to start its webcam or its microphone.
    // The stream starts only after a click of the user.
    public const string StreamRequest = "flux.stream.request";
}
