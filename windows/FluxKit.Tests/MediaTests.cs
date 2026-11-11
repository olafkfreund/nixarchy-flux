using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// MediaTests.swift of the Mac app, the request bodies, the control of the
// players of a computer on loopback, and the album art loader.
public class MediaTests
{
    static Packet Mpris(JsonObject body) => Packet.Parse(new Packet(PacketType.Mpris, body).Serialize())!;

    static JsonArray Names(params string[] names) => new(names.Select(n => (JsonNode)n).ToArray());

    [Fact]
    public void PlayerListPicksFirstPlayerAndDropsGoneStates()
    {
        var m = new RemoteMedia().Apply(Mpris(new JsonObject { ["player"] = "vlc", ["title"] = "Old" }), 1, out _);
        Assert.Equal("vlc", m.Current);

        m = m.Apply(Mpris(new JsonObject { ["playerList"] = Names("spotify", "firefox") }), 2, out var request);
        Assert.Equal(["spotify", "firefox"], m.Players);
        Assert.Equal("spotify", m.Current);
        Assert.Equal("spotify", request);
        Assert.False(m.States.ContainsKey("vlc"));
        Assert.Null(m.Player);
    }

    [Fact]
    public void PlayerListKeepsCurrentPlayerThatStillRuns()
    {
        var m = new RemoteMedia().Apply(Mpris(new JsonObject { ["playerList"] = Names("spotify", "firefox") }), 1, out _);
        m = m.Select("firefox");
        m = m.Apply(Mpris(new JsonObject { ["playerList"] = Names("mpv", "firefox") }), 2, out var request);
        Assert.Equal("firefox", request);
        Assert.Equal("firefox", m.Current);

        m = m.Apply(Mpris(new JsonObject { ["playerList"] = Names() }), 3, out request);
        Assert.Null(request);
        Assert.Null(m.Current);
    }

    [Fact]
    public void StateMergesOnlyTheFieldsThatArrive()
    {
        var m = new RemoteMedia().Apply(Mpris(new JsonObject
        {
            ["player"] = "spotify", ["title"] = "Weightless", ["artist"] = "Marconi Union", ["isPlaying"] = true,
            ["pos"] = 1000, ["length"] = 480_000, ["canSeek"] = true, ["volume"] = 40,
        }), 10, out _);
        m = m.Apply(Mpris(new JsonObject { ["player"] = "spotify", ["isPlaying"] = false, ["pos"] = 5000 }), 20, out _);
        var s = m.Player!;
        Assert.Equal("Weightless", s.Title);
        Assert.Equal("Marconi Union", s.Artist);
        Assert.False(s.Playing);
        Assert.Equal(5000, s.Position);
        Assert.Equal(480_000, s.Length);
        Assert.True(s.CanSeek);
        Assert.Equal(40, s.Volume);
        Assert.Equal(20, s.UpdatedAt);
    }

    [Fact]
    public void PlayingPlayerTakesOverPausedCurrentPlayer()
    {
        var m = new RemoteMedia().Apply(Mpris(new JsonObject { ["player"] = "firefox", ["isPlaying"] = false }), 1, out _);
        m = m.Apply(Mpris(new JsonObject { ["player"] = "spotify", ["isPlaying"] = true }), 2, out _);
        Assert.Equal("spotify", m.Current);
        // A playing current player keeps control.
        m = m.Apply(Mpris(new JsonObject { ["player"] = "firefox", ["isPlaying"] = true }), 3, out _);
        Assert.Equal("spotify", m.Current);
    }

    [Fact]
    public void PositionMovesWhilePlayingAndStopsAtLength()
    {
        var p = new RemotePlayer("spotify") { Position = 10_000, Length = 12_000, UpdatedAt = 100 };
        Assert.Equal(10_000, p.PositionAt(101.5));
        p = p with { Playing = true };
        Assert.Equal(11_500, p.PositionAt(101.5));
        Assert.Equal(12_000, p.PositionAt(200));
    }

    [Fact]
    public void TogglePlayingKeepsThePositionReached()
    {
        var m = new RemoteMedia().Apply(Mpris(new JsonObject { ["player"] = "spotify", ["isPlaying"] = true, ["pos"] = 1000, ["length"] = 60_000 }), 10, out _);
        m = m.TogglePlaying(13);
        Assert.False(m.Player!.Playing);
        Assert.Equal(4000, m.Player.Position);
        Assert.Equal(4000, m.Player.PositionAt(50));
    }

    [Fact]
    public void ArtUrlLoadsOnlyOverHttp()
    {
        var p = new RemotePlayer("spotify") { ArtUrl = "https://i.scdn.co/image/ab67616d" };
        Assert.NotNull(p.ArtUri);
        Assert.Null((p with { ArtUrl = "file:///home/me/.cache/cover.png" }).ArtUri);
        Assert.Null((p with { ArtUrl = "/home/me/.cache/cover.png" }).ArtUri);
        Assert.Null((p with { ArtUrl = "http:///cover.png" }).ArtUri); // a URL without a host does not load
        Assert.Null((p with { ArtUrl = "" }).ArtUri);
        Assert.False(AlbumArtLoader.Loads(new Uri("ftp://x.org/a.png")));
        Assert.True(AlbumArtLoader.Loads(new Uri("HTTP://x.org/a.png")));
    }

    // A position near long.MaxValue must not overflow in the sum with the
    // time that passed.
    [Fact]
    public void LargePositionDoesNotOverflow()
    {
        var m = new RemoteMedia().Apply(Mpris(new JsonObject { ["player"] = "x", ["isPlaying"] = true, ["pos"] = long.MaxValue, ["length"] = long.MaxValue }), 10, out _);
        Assert.Equal(RemotePlayer.MaxMillis, m.Player!.Position);
        Assert.Equal(RemotePlayer.MaxMillis, m.Player.Length);
        Assert.Equal(RemotePlayer.MaxMillis, m.Player.PositionAt(1e12));
        m = m.TogglePlaying(1e300);
        Assert.Equal(RemotePlayer.MaxMillis, m.Player!.Position);
        m = m.Apply(Mpris(new JsonObject { ["player"] = "x", ["pos"] = long.MinValue, ["length"] = -5, ["volume"] = 1_000_000 }), 20, out _);
        Assert.Equal(0, m.Player!.Position);
        Assert.Equal(0, m.Player.Length);
        Assert.Equal(100, m.Player.Volume);
        m = m.Apply(Mpris(new JsonObject { ["player"] = "x", ["pos"] = 1e300 }), 30, out _);
        Assert.Equal(0, m.Player!.Position); // a number that does not fit keeps the last position
        m = m.Seek(long.MaxValue, 40);
        Assert.Equal(RemotePlayer.MaxMillis, m.Player!.Position);
    }

    [Fact]
    public void ChangesWithoutACurrentPlayerDoNothing()
    {
        var m = new RemoteMedia();
        Assert.Equal(m, m.TogglePlaying(1));
        Assert.Equal(m, m.Seek(5, 1));
        Assert.Equal(m, m.SetVolume(5));
    }

    // internal/core/media.go of fluxd reads these bodies.
    [Fact]
    public void RequestBodiesMatchFluxd()
    {
        static string Body(Packet p) => p.Body.ToJsonString();
        Assert.Equal("""{"requestPlayerList":true}""", Body(MprisPlugin.PlayerListRequest()));
        Assert.Equal("""{"player":"spotify","requestNowPlaying":true,"requestVolume":true}""", Body(MprisPlugin.NowPlayingRequest("spotify")));
        Assert.Equal("""{"player":"spotify","action":"PlayPause"}""", Body(MprisPlugin.ActionRequest("spotify", "PlayPause")));
        Assert.Equal("""{"player":"spotify","SetPosition":90000}""", Body(MprisPlugin.SeekRequest("spotify", 90_000)));
        Assert.Equal("""{"player":"spotify","setVolume":30}""", Body(MprisPlugin.VolumeRequest("spotify", 30)));
        Assert.All(new[] { MprisPlugin.PlayerListRequest(), MprisPlugin.ActionRequest("a", "Next") }, p => Assert.Equal(PacketType.MprisRequest, p.Type));
    }

    // This computer controls the players of a computer on loopback, with
    // the optimistic changes.
    [Fact]
    public async Task ControlsThePlayersOfTheComputer()
    {
        using var computer = new SystemTestComputer(32500, 32501, [PacketType.MprisRequest], [PacketType.Mpris]);
        var plugin = new MprisPlugin();
        using var t = new TestCore(SystemTestComputer.CoreLan(32520), [plugin]);
        await computer.PairAsync(t);
        var id = computer.Id;
        IReadOnlyList<Packet> Requests() => computer.Got(PacketType.MprisRequest);

        // The list loads on connect.
        await Wait.UntilAsync(() => Requests().Count == 1, "the player list request");
        Assert.True(Requests()[0].Bool("requestPlayerList"));

        // The answer selects the first player and asks for its state.
        computer.Send(new Packet(PacketType.Mpris, new JsonObject { ["playerList"] = Names("spotify", "mpv") }));
        await Wait.UntilAsync(() => Requests().Count == 2, "the now-playing request");
        Assert.Equal("spotify", Requests()[1].String("player"));
        Assert.True(Requests()[1].Bool("requestNowPlaying"));
        Assert.True(Requests()[1].Bool("requestVolume"));
        computer.Send(new Packet(PacketType.Mpris, new JsonObject
        {
            ["player"] = "spotify", ["title"] = "Weightless", ["isPlaying"] = true, ["pos"] = 1000, ["length"] = 480_000,
            ["canSeek"] = true, ["volume"] = 40, ["albumArtUrl"] = "https://i.scdn.co/image/x",
        }));
        await Wait.UntilAsync(() => plugin.Model.Media(id).Player is { Title: "Weightless" }, "the state of the player");
        Assert.NotNull(plugin.Model.Media(id).Player!.ArtUri);

        // PlayPause pauses at once, before the computer answers.
        plugin.Action(id, "PlayPause");
        await Wait.UntilAsync(() => Requests().Count == 3 && plugin.Model.Media(id).Player is { Playing: false }, "the pause");
        Assert.Equal("PlayPause", Requests()[2].String("action"));

        plugin.Seek(id, 90_000);
        await Wait.UntilAsync(() => Requests().Count == 4 && plugin.Model.Media(id).Player is { Position: 90_000 }, "the seek");
        Assert.Equal(90_000, Requests()[3].Long("SetPosition"));

        plugin.SetVolume(id, 130);
        await Wait.UntilAsync(() => Requests().Count == 5 && plugin.Model.Media(id).Player is { Volume: 100 }, "the volume");
        Assert.Equal(100, Requests()[4].Int("setVolume"));

        // Another player becomes the current one and loads its state.
        plugin.Select(id, "mpv");
        await Wait.UntilAsync(() => Requests().Count == 6 && plugin.Model.Media(id).Current == "mpv", "the selection");
        Assert.Equal("mpv", Requests()[5].String("player"));
        Assert.True(Requests()[5].Bool("requestNowPlaying"));

        // A refresh asks for the list and the current player.
        plugin.RequestPlayers(id);
        await Wait.UntilAsync(() => Requests().Count == 8, "the refresh");
        Assert.True(Requests()[6].Bool("requestPlayerList"));
        Assert.Equal("mpv", Requests()[7].String("player"));

        // An action without a current player sends nothing.
        plugin.Action("0123456789abcdef0123456789abcdef", "Next");
        await Task.Delay(200);
        Assert.Equal(8, Requests().Count);
    }

    // A small HTTP server on loopback with 1 answer for each path.
    sealed class ArtServer : IDisposable
    {
        readonly TcpListener listener;
        readonly Dictionary<string, Func<Stream, Task>> answers = [];
        readonly CancellationTokenSource stop = new();

        public ArtServer(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Port = port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

        public void Answer(string path, string head, byte[] body, bool chunked = false) => answers[path] = async s =>
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes(head + "\r\nConnection: close\r\n" +
                (chunked ? "Transfer-Encoding: chunked\r\n\r\n" : $"Content-Length: {body.Length}\r\n\r\n")));
            if (!chunked)
            {
                await s.WriteAsync(body);
                return;
            }
            for (var i = 0; i < body.Length; i += 65536)
            {
                var n = Math.Min(65536, body.Length - i);
                await s.WriteAsync(Encoding.ASCII.GetBytes($"{n:x}\r\n"));
                await s.WriteAsync(body.AsMemory(i, n));
                await s.WriteAsync("\r\n"u8.ToArray());
            }
            await s.WriteAsync("0\r\n\r\n"u8.ToArray());
        };

        async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var s = client.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n") && await s.ReadAsync(one) == 1)
                    {
                        head.Append((char)one[0]);
                    }
                    var path = head.ToString().Split(' ')[1];
                    if (answers.TryGetValue(path, out var answer))
                    {
                        await answer(s);
                    }
                    else
                    {
                        await s.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }

    [Fact]
    public async Task ArtLoaderKeepsTheLimits()
    {
        using var server = new ArtServer(32550);
        var image = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        server.Answer("/art.png", "HTTP/1.1 200 OK\r\nContent-Type: image/png", image);
        server.Answer("/chunked.png", "HTTP/1.1 200 OK\r\nContent-Type: image/png", image, chunked: true);
        server.Answer("/big.png", "HTTP/1.1 200 OK", new byte[AlbumArtLoader.MaxBytes + 1]);
        server.Answer("/big-chunked.png", "HTTP/1.1 200 OK", new byte[AlbumArtLoader.MaxBytes + 1], chunked: true);
        server.Answer("/max.png", "HTTP/1.1 200 OK", new byte[AlbumArtLoader.MaxBytes], chunked: true);
        server.Answer("/moved.png", $"HTTP/1.1 302 Found\r\nLocation: {server.Url("/art.png")}", []);
        server.Answer("/to-file.png", "HTTP/1.1 302 Found\r\nLocation: file:///etc/passwd", []);
        server.Answer("/relative.png", "HTTP/1.1 301 Moved Permanently\r\nLocation: /art.png", []);
        server.Answer("/loop.png", "HTTP/1.1 307 Temporary Redirect\r\nLocation: /loop.png", []);
        server.Answer("/partial.png", "HTTP/1.1 206 Partial Content", image);

        Assert.Equal(image, await AlbumArtLoader.LoadAsync(new Uri(server.Url("/art.png"))));
        Assert.Equal(image, await AlbumArtLoader.LoadAsync(new Uri(server.Url("/chunked.png"))));
        Assert.Equal(image, await AlbumArtLoader.LoadAsync(new Uri(server.Url("/moved.png"))));
        Assert.Equal(AlbumArtLoader.MaxBytes, (await AlbumArtLoader.LoadAsync(new Uri(server.Url("/max.png"))))!.Length);
        Assert.Null(await AlbumArtLoader.LoadAsync(new Uri(server.Url("/big.png")))); // the length is above the limit
        Assert.Null(await AlbumArtLoader.LoadAsync(new Uri(server.Url("/big-chunked.png")))); // the data is above the limit
        Assert.Equal(image, await AlbumArtLoader.LoadAsync(new Uri(server.Url("/relative.png"))));
        Assert.Null(await AlbumArtLoader.LoadAsync(new Uri(server.Url("/to-file.png")))); // a redirect to another scheme
        Assert.Null(await AlbumArtLoader.LoadAsync(new Uri(server.Url("/loop.png")))); // too many redirects
        Assert.Null(await AlbumArtLoader.LoadAsync(new Uri(server.Url("/partial.png")))); // only 200 counts
        Assert.Null(await AlbumArtLoader.LoadAsync(new Uri(server.Url("/missing.png"))));
        Assert.Null(await AlbumArtLoader.LoadAsync(new Uri("file:///etc/hostname")));
        Assert.Null(await AlbumArtLoader.LoadAsync(new Uri("http://127.0.0.1:32559/closed.png"))); // nobody listens
    }
}
