using System.Collections.Immutable;

namespace FluxKit;

// The now-playing state of one player on the computer. Times are in seconds
// of a clock that only moves forward, such as MprisPlugin.Now.
public sealed record RemotePlayer(string Name)
{
    // The longest position or length that Flux takes, in milliseconds: 7
    // days. A larger value from the computer is cut to it, so that the sums
    // below cannot overflow.
    public const long MaxMillis = 7L * 24 * 60 * 60 * 1000;

    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public bool Playing { get; init; }

    // The position and the length of the track in milliseconds.
    public long Position { get; init; }
    public long Length { get; init; }
    public bool CanSeek { get; init; }

    // The volume from 0 to 100, or null until the computer reports it.
    public int? Volume { get; init; }

    // The album art that the player reports. Only http and https URLs load on
    // this computer.
    public string ArtUrl { get; init; } = "";

    // The time of the position value.
    public double UpdatedAt { get; init; }

    // The position at the time now. It moves forward while the player plays.
    public long PositionAt(double now)
    {
        if (!Playing)
        {
            return Position;
        }
        var moved = Limited(Position) + Elapsed(UpdatedAt, now);
        return Math.Min(Math.Max(moved, 0), Math.Max(Length, 0));
    }

    // The milliseconds from start to now, from 0 to MaxMillis.
    internal static long Elapsed(double start, double now)
    {
        var ms = Math.Round((now - start) * 1000, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(ms) || ms <= 0)
        {
            return 0;
        }
        return ms >= MaxMillis ? MaxMillis : (long)ms;
    }

    // The value from 0 to MaxMillis.
    internal static long Limited(long ms) => Math.Min(Math.Max(ms, 0), MaxMillis);

    // The album art URL when this computer loads it: http or https with a
    // host. AlbumArtLoader loads it.
    public Uri? ArtUri => Uri.TryCreate(ArtUrl, UriKind.Absolute, out var uri) && AlbumArtLoader.Loads(uri) ? uri : null;
}

// The players of one computer and the player that this computer controls.
public sealed record RemoteMedia
{
    public ValueList<string> Players { get; init; } = ValueList<string>.Empty;
    public string? Current { get; init; }
    public ImmutableDictionary<string, RemotePlayer> States { get; init; } = ImmutableDictionary<string, RemotePlayer>.Empty;

    // The state of the player that this computer controls.
    public RemotePlayer? Player => Current is { } name && States.TryGetValue(name, out var s) ? s : null;

    // Merges a flux.mpris packet. request is the player whose now-playing
    // state this computer asks for next, or null.
    public RemoteMedia Apply(Packet p, double now, out string? request)
    {
        request = null;
        var next = this;
        if (p.Has("playerList"))
        {
            var players = ValueList.From(p.Strings("playerList"));
            var current = next.Current is { } c && players.Contains(c) ? c : players.FirstOrDefault();
            var states = next.States.RemoveRange(next.States.Keys.Where(k => !players.Contains(k)).ToArray());
            next = next with { Players = players, Current = current, States = states };
            request = current;
        }
        if (p.String("player") is not { } name)
        {
            return next;
        }
        var s = next.States.TryGetValue(name, out var old) ? old : new RemotePlayer(name);
        s = s with
        {
            Title = p.String("title") ?? s.Title,
            Artist = p.String("artist") ?? s.Artist,
            Album = p.String("album") ?? s.Album,
            Playing = p.Bool("isPlaying") ?? s.Playing,
            Position = p.Long("pos") is { } pos ? RemotePlayer.Limited(pos) : s.Position,
            Length = p.Long("length") is { } length ? RemotePlayer.Limited(length) : s.Length,
            CanSeek = p.Bool("canSeek") ?? s.CanSeek,
            Volume = p.Int("volume") is { } volume ? Math.Clamp(volume, 0, 100) : s.Volume,
            ArtUrl = p.String("albumArtUrl") ?? s.ArtUrl,
            UpdatedAt = now,
        };
        next = next with { States = next.States.SetItem(name, s) };
        if (next.Current is null)
        {
            next = next with { Current = name };
        }
        // A player that starts to play takes over a current player that is paused.
        if (next.Player is { Playing: false } && s.Playing)
        {
            next = next with { Current = name };
        }
        return next;
    }

    // Makes the player the one that this computer controls.
    public RemoteMedia Select(string name) => this with { Current = name };

    // The optimistic state after a PlayPause action, until the computer answers.
    public RemoteMedia TogglePlaying(double now)
    {
        if (Current is not { } name || !States.TryGetValue(name, out var s))
        {
            return this;
        }
        var position = s.Playing ? RemotePlayer.Limited(RemotePlayer.Limited(s.Position) + RemotePlayer.Elapsed(s.UpdatedAt, now)) : s.Position;
        return this with { States = States.SetItem(name, s with { Position = position, Playing = !s.Playing, UpdatedAt = now }) };
    }

    // The optimistic state after a seek.
    public RemoteMedia Seek(long position, double now)
    {
        if (Current is not { } name || !States.TryGetValue(name, out var s))
        {
            return this;
        }
        return this with { States = States.SetItem(name, s with { Position = RemotePlayer.Limited(position), UpdatedAt = now }) };
    }

    // The optimistic state after a volume change.
    public RemoteMedia SetVolume(int volume)
    {
        if (Current is not { } name || !States.TryGetValue(name, out var s))
        {
            return this;
        }
        return this with { States = States.SetItem(name, s with { Volume = volume }) };
    }
}
