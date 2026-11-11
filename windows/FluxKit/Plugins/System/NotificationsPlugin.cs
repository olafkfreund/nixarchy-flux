using System.Collections.Immutable;

namespace FluxKit;

// The notifications of Flux that Windows still shows, for example from an
// earlier run. The Windows app reads the toast history. Tests use a fake.
public interface INotificationHistory
{
    // The IDs of the Flux notifications that the system shows now, the
    // oldest first.
    IReadOnlyList<string> DeliveredIds();
}

// Shows the notifications that a computer sends in flux.notification
// packets.
//
// The phone half of the notification feature shares the notifications of
// other apps with the computer. Windows gives this app no access to the
// notifications of other apps, so this plugin sends nothing and does not
// accept flux.notification.request, .reply, or .action.
// NotificationsPlugin.swift of the Mac app has the same rules.
public sealed class NotificationsPlugin : IFluxPlugin
{
    // The notification category of computer notifications. It has no
    // actions: a click opens Flux.
    public const string Category = "computer-notification";

    // The key in UserInfo with the device ID of the computer.
    public const string ComputerKey = "computer";

    readonly INotificationHistory? history;
    FluxCore? core;

    // history is null when the app cannot read the notifications that the
    // system shows.
    public NotificationsPlugin(INotificationHistory? history = null) => this.history = history;

    public NotificationsModel Model { get; } = new();

    public IReadOnlyList<string> Incoming { get; } = [PacketType.Notification];
    public IReadOnlyList<string> Outgoing { get; } = [];

    // Called on the UI thread after a click on a computer notification, with
    // the device ID of the computer.
    public event Action<string>? Opened;

    // Counts the notifications of an earlier run toward the limit of each
    // computer, before the network starts.
    public void Attach(FluxCore core)
    {
        this.core = core;
        core.Host.Notifier.Register(Category, [], (_, info, _) =>
        {
            if (info.TryGetValue(ComputerKey, out var id))
            {
                core.Host.Post(() => Opened?.Invoke(id));
            }
        });
        if (history is not null)
        {
            DeliveredNotifications.Seed(core.Host.Notifier, history.DeliveredIds());
        }
    }

    public void Handle(Packet packet, Device device)
    {
        if (core is null || ComputerNotification.From(packet, device.Id, device.Name) is not { } n)
        {
            return;
        }
        var id = DeliveredNotifications.NotificationPrefix + n.Key;
        var deviceId = device.Id;
        var grant = NotificationLimit.TakeNow(deviceId);
        if (grant == NotificationGrant.Drop)
        {
            FluxLog.Info("notifications", $"notification {id} dropped: too many from this computer");
            core.Host.Post(() => Model.Dropped = Bump(Model.Dropped, deviceId));
            return;
        }
        // The text can be private, so the log gets only the ID.
        FluxLog.Info("notifications", $"notification {id}");
        DeliveredNotifications.Post(core.Host.Notifier, id, deviceId);
        core.Host.Notifier.Post(new SystemNotification(id, Category, n.Title, n.Text)
        {
            Subtitle = n.Subtitle,
            Sound = grant == NotificationGrant.Sound,
            UserInfo = new Dictionary<string, string> { [ComputerKey] = deviceId },
        });
        core.Host.Post(() => Model.Received = Bump(Model.Received, deviceId));
    }

    static ImmutableDictionary<string, int> Bump(ImmutableDictionary<string, int> counts, string key) =>
        counts.SetItem(key, (counts.TryGetValue(key, out var n) ? n : 0) + 1);
}

// The notification counts of the computers since Flux started, for the UI.
public sealed class NotificationsModel : Model
{
    ImmutableDictionary<string, int> received = ImmutableDictionary<string, int>.Empty;
    ImmutableDictionary<string, int> dropped = ImmutableDictionary<string, int>.Empty;

    // The notifications that each computer showed, by device ID.
    public ImmutableDictionary<string, int> Received
    {
        get => received;
        internal set => Set(ref received, value);
    }

    // The notifications of each computer that the limit dropped, by device ID.
    public ImmutableDictionary<string, int> Dropped
    {
        get => dropped;
        internal set => Set(ref dropped, value);
    }
}

// The delivered notifications of each computer, the oldest first. A computer
// keeps at most Cap of them, so that it cannot fill the notification center.
public sealed class DeliveredNotifications
{
    public const int Cap = 20;

    // The start of the ID of a computer notification. The ID continues with
    // the device ID, a colon, and the ID from the computer.
    public const string NotificationPrefix = "computer-";

    // The start of the ID of a received link. The ID continues with the
    // device ID, a colon, and a GUID.
    public const string LinkPrefix = "share-link-";

    // The notifications and received links of all computers.
    static readonly DeliveredNotifications shared = new();
    static readonly Lock sharedGate = new();

    readonly Dictionary<string, List<string>> ids = [];

    // Adds the notification id of the computer before its post, and removes
    // the oldest notifications of the computer above Cap.
    public static void Post(Notifier notifier, string id, string deviceId)
    {
        List<string> old;
        lock (sharedGate)
        {
            old = shared.Add(id, deviceId);
        }
        foreach (var oldId in old)
        {
            notifier.Remove(oldId);
        }
    }

    // Adds the notifications of the computers that the system still shows
    // from an earlier run, so that the limit holds after a restart. It
    // removes the oldest notifications above Cap.
    public static void Seed(Notifier notifier, IReadOnlyList<string> found)
    {
        List<string> old;
        lock (sharedGate)
        {
            old = shared.Seed(found);
        }
        foreach (var oldId in old)
        {
            notifier.Remove(oldId);
        }
    }

    // A new notification ID for a received link of the computer.
    public static string LinkId(string deviceId) => $"{LinkPrefix}{deviceId}:{Guid.NewGuid():D}";

    // The device ID in the ID of a computer notification or a received link,
    // or null for the ID of another notification.
    public static string? DeviceIdOf(string id)
    {
        foreach (var prefix in new[] { NotificationPrefix, LinkPrefix })
        {
            if (!id.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var rest = id[prefix.Length..];
            var colon = rest.IndexOf(':');
            if (colon < 0)
            {
                return null;
            }
            var deviceId = rest[..colon];
            return Identity.ValidDeviceId(deviceId) ? deviceId : null;
        }
        return null;
    }

    // Adds the delivered IDs of an earlier run, the oldest first, before the
    // IDs of this run, and returns the IDs to remove, the oldest first. It
    // skips the IDs that are in the list and the IDs of other notifications.
    internal List<string> Seed(IEnumerable<string> found)
    {
        var earlier = new Dictionary<string, List<string>>();
        foreach (var id in found)
        {
            if (DeviceIdOf(id) is not { } deviceId ||
                (ids.TryGetValue(deviceId, out var now) && now.Contains(id)) ||
                (earlier.TryGetValue(deviceId, out var seen) && seen.Contains(id)))
            {
                continue;
            }
            if (!earlier.TryGetValue(deviceId, out var list))
            {
                earlier[deviceId] = list = [];
            }
            list.Add(id);
        }
        var old = new List<string>();
        foreach (var (deviceId, seeded) in earlier)
        {
            var list = seeded.Concat(ids.TryGetValue(deviceId, out var current) ? current : []).ToList();
            var drop = Math.Max(0, list.Count - Cap);
            old.AddRange(list.Take(drop));
            ids[deviceId] = list.Skip(drop).ToList();
        }
        return old;
    }

    // Adds the notification id of the device and returns the IDs to remove,
    // the oldest first. A post with an ID that is in the list replaces that
    // notification, so the ID moves to the end.
    internal List<string> Add(string id, string deviceId)
    {
        if (!ids.TryGetValue(deviceId, out var list))
        {
            ids[deviceId] = list = [];
        }
        list.Remove(id);
        list.Add(id);
        var drop = Math.Max(0, list.Count - Cap);
        var old = list.Take(drop).ToList();
        list.RemoveRange(0, drop);
        return old;
    }

    // The delivered IDs of the device, the oldest first.
    internal IReadOnlyList<string> Delivered(string deviceId) => ids.TryGetValue(deviceId, out var list) ? list.ToArray() : [];
}

// What a new notification of a computer can do now.
public enum NotificationGrant
{
    // The notification must not show.
    Drop,

    // The notification shows without a sound, because it is part of a burst.
    Quiet,

    // The notification shows with a sound.
    Sound,
}

// A token bucket for each computer: a burst of Burst notifications, then
// PerSecond each second.
public sealed class NotificationLimit
{
    public const double Burst = 10;
    public const double PerSecond = 1;

    // The limit of all notifications that a computer causes, such as
    // flux.notification packets and received links, so that a computer
    // cannot bury the other notifications of this computer.
    static readonly NotificationLimit shared = new();
    static readonly Lock sharedGate = new();

    readonly Dictionary<string, (double Tokens, double At)> buckets = [];

    // Takes a token of the computer from the shared limit now. It returns
    // false when the notification must not show.
    public static bool AllowsNow(string deviceId) => TakeNow(deviceId) != NotificationGrant.Drop;

    // Takes a token of the computer from the shared limit now.
    public static NotificationGrant TakeNow(string deviceId)
    {
        var now = Environment.TickCount64 / 1000.0;
        lock (sharedGate)
        {
            return shared.Take(deviceId, now);
        }
    }

    // Reports whether a notification from the device may show now, and takes
    // a token for it. now is in seconds.
    internal bool Allow(string deviceId, double now) => Take(deviceId, now) != NotificationGrant.Drop;

    // Takes a token for a notification from the device. Only a notification
    // that finds the full burst makes a sound, so that a burst makes 1
    // sound. now is in seconds.
    internal NotificationGrant Take(string deviceId, double now)
    {
        var last = buckets.TryGetValue(deviceId, out var b) ? b : (Tokens: Burst, At: now);
        var tokens = Math.Min(Burst, last.Tokens + Math.Max(0, now - last.At) * PerSecond);
        if (tokens < 1)
        {
            buckets[deviceId] = (tokens, now);
            return NotificationGrant.Drop;
        }
        buckets[deviceId] = (tokens - 1, now);
        return tokens >= Burst ? NotificationGrant.Sound : NotificationGrant.Quiet;
    }
}
