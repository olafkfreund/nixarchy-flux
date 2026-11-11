using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace FluxKit;

// The players of each computer, for the UI.
public sealed class MediaModel : Model
{
    ImmutableDictionary<string, RemoteMedia> devices = ImmutableDictionary<string, RemoteMedia>.Empty;

    // The players of each computer, by device ID.
    public ImmutableDictionary<string, RemoteMedia> Devices
    {
        get => devices;
        internal set => Set(ref devices, value);
    }

    // The players of the computer. A computer without a report has no players.
    public RemoteMedia Media(string deviceId) => devices.TryGetValue(deviceId, out var m) ? m : new RemoteMedia();

    internal void Update(string deviceId, Func<RemoteMedia, RemoteMedia> change) =>
        Devices = devices.SetItem(deviceId, change(Media(deviceId)));
}

// flux.mpris.request out, flux.mpris in: this computer controls the media
// players on the computer. The actions run on the UI thread, in order with
// the packets, and change the model at once. The state that the computer
// sends next replaces the change. MprisPlugin.swift of the Mac app has the
// same rules.
public sealed class MprisPlugin : IFluxPlugin
{
    FluxCore? core;

    public MediaModel Model { get; } = new();

    public IReadOnlyList<string> Incoming { get; } = [PacketType.Mpris];
    public IReadOnlyList<string> Outgoing { get; } = [PacketType.MprisRequest];

    // The time for the positions, in seconds of a clock that only moves
    // forward. RemotePlayer.PositionAt takes it.
    public static double Now => Environment.TickCount64 / 1000.0;

    public void Attach(FluxCore core) => this.core = core;

    // The window shows the current player, so the list loads on connect.
    public void OnConnected(Device device)
    {
        if (device.Accepts(PacketType.MprisRequest))
        {
            device.Send(PlayerListRequest());
        }
    }

    public void Handle(Packet packet, Device device)
    {
        var id = device.Id;
        var now = Now;
        Post(() =>
        {
            string? request = null;
            Model.Update(id, m => m.Apply(packet, now, out request));
            if (request is not null)
            {
                RequestNowPlaying(id, request);
            }
        });
    }

    // Asks for the player list and the state of the current player.
    public void RequestPlayers(string deviceId) => Post(() =>
    {
        core?.Send(PlayerListRequest(), deviceId);
        if (Model.Media(deviceId).Current is { } name)
        {
            RequestNowPlaying(deviceId, name);
        }
    });

    // Makes the player the one that this computer controls.
    public void Select(string deviceId, string player) => Post(() =>
    {
        Model.Update(deviceId, m => m.Select(player));
        RequestNowPlaying(deviceId, player);
    });

    // Sends PlayPause, Play, Pause, Next, Previous, or Stop to the current player.
    public void Action(string deviceId, string action) => Post(() =>
    {
        if (Model.Media(deviceId).Current is not { } name)
        {
            return;
        }
        core?.Send(ActionRequest(name, action), deviceId);
        if (action == "PlayPause")
        {
            var now = Now;
            Model.Update(deviceId, m => m.TogglePlaying(now));
        }
    });

    // Moves the current player to the position in milliseconds.
    public void Seek(string deviceId, long position) => Post(() =>
    {
        if (Model.Media(deviceId).Current is not { } name)
        {
            return;
        }
        core?.Send(SeekRequest(name, position), deviceId);
        var now = Now;
        Model.Update(deviceId, m => m.Seek(position, now));
    });

    // Sets the volume of the current player, from 0 to 100.
    public void SetVolume(string deviceId, int volume) => Post(() =>
    {
        if (Model.Media(deviceId).Current is not { } name)
        {
            return;
        }
        var v = Math.Clamp(volume, 0, 100);
        core?.Send(VolumeRequest(name, v), deviceId);
        Model.Update(deviceId, m => m.SetVolume(v));
    });

    void RequestNowPlaying(string deviceId, string name) => core?.Send(NowPlayingRequest(name), deviceId);

    void Post(Action action) => core?.Host.Post(action);

    // The bodies of flux.mpris.request. internal/core/media.go of fluxd reads them.

    internal static Packet PlayerListRequest() =>
        new(PacketType.MprisRequest, new JsonObject { ["requestPlayerList"] = true });

    internal static Packet NowPlayingRequest(string player) =>
        new(PacketType.MprisRequest, new JsonObject { ["player"] = player, ["requestNowPlaying"] = true, ["requestVolume"] = true });

    internal static Packet ActionRequest(string player, string action) =>
        new(PacketType.MprisRequest, new JsonObject { ["player"] = player, ["action"] = action });

    internal static Packet SeekRequest(string player, long position) =>
        new(PacketType.MprisRequest, new JsonObject { ["player"] = player, ["SetPosition"] = position });

    internal static Packet VolumeRequest(string player, int volume) =>
        new(PacketType.MprisRequest, new JsonObject { ["player"] = player, ["setVolume"] = volume });
}
