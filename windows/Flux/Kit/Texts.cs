using System.Globalization;
using FluxKit;

namespace Flux;

// The texts that several views share.
public static class Texts
{
    // The parts of a context line that are not empty, joined with " · ".
    public static string ContextLine(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));

    // The time of day in the format of the system, such as "13:41".
    public static string ClockTime(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    // "Today", "Yesterday", or the date.
    public static string DayText(DateTimeOffset at)
    {
        var day = at.ToLocalTime().Date;
        var today = DateTime.Today;
        if (day == today)
        {
            return "Today";
        }
        if (day == today.AddDays(-1))
        {
            return "Yesterday";
        }
        return day.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    // A size such as "18 KB" or "4.2 MB".
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : value < 10 ? $"{value:0.#} {units[unit]}" : $"{value:0} {units[unit]}";
    }

    // "1 item needs you", "<n> items need you", or "Nothing needs you".
    public static string NeedsText(int count) => count switch
    {
        0 => "Nothing needs you",
        1 => "1 item needs you",
        _ => $"{count} items need you",
    };

    // The link text of a computer: "Not reachable", "Connected, battery 80%",
    // or "Connected".
    public static string LinkText(DeviceSnapshot device, int? charge = null, bool charging = false)
    {
        if (!device.Online)
        {
            return "Not reachable";
        }
        if (charge is not { } c)
        {
            return "Connected";
        }
        return $"Connected, battery {c}%" + (charging ? ", charging" : "");
    }

    // The state of a computer in words.
    public static string StatusText(DeviceSnapshot d) => d.PairState switch
    {
        PairState.Requested => "Waiting for the computer",
        PairState.Incoming => "Wants to pair",
        PairState.Paired => d.Online ? "Connected" : "Offline",
        _ => d.Online ? "Not paired" : "Offline",
    };

    // The icon of a computer.
    public static string Symbol(DeviceSnapshot d) => d.Type == "laptop" ? Icons.Laptop : Icons.Desktop;

    // True for an address of Tailscale: 100.64.0.0/10 or fd7a:115c:a1e0::/48.
    public static bool IsTailscale(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length == 4 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b))
        {
            return a == 100 && b is >= 64 and <= 127;
        }
        return ip.StartsWith("fd7a:115c:a1e0:", StringComparison.OrdinalIgnoreCase);
    }

    // "3 minutes ago" and the like, for the last search.
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var s = (now - at).TotalSeconds;
        if (s < 60)
        {
            return "just now";
        }
        var m = (int)(s / 60);
        if (m < 60)
        {
            return m == 1 ? "1 minute ago" : $"{m} minutes ago";
        }
        var h = m / 60;
        return h == 1 ? "1 hour ago" : $"{h} hours ago";
    }
}
