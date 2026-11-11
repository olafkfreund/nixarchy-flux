using System.Globalization;
using System.Text;

namespace FluxKit;

// A clip that this computer sent or received, for the UI. Preview is a short
// form of the text. Flux keeps it only in memory. ClipEvent.swift of the Mac
// app has the same rules.
public sealed record ClipEvent(
    // The computers of the clip.
    ValueList<string> DeviceIds,
    // The name of the computer, or "<n> computers".
    string Computer,
    // True for a clip that this computer sent, false for a clip from a computer.
    bool Sent,
    string Preview,
    // True for a secret, such as a password from a password manager. Its
    // Preview is HiddenText, so that the UI does not show the secret.
    bool Secret,
    // When the clip went out or arrived.
    DateTimeOffset At)
{
    // The preview of a secret.
    public const string HiddenText = "Hidden text";

    // The most characters of a preview.
    public const int PreviewLength = 160;

    // A clip that went to the computers. With secret, the event keeps no part
    // of the text. It returns null when computers is empty.
    public static ClipEvent? SentTo(IReadOnlyList<(string Id, string Name)> computers, string text, bool secret = false, DateTimeOffset? at = null)
    {
        if (computers.Count == 0)
        {
            return null;
        }
        var name = computers.Count == 1 ? computers[0].Name : $"{computers.Count} computers";
        return new ClipEvent(ValueList.From(computers.Select(c => c.Id)), name, true,
            secret ? HiddenText : MakePreview(text), secret, at ?? DateTimeOffset.Now);
    }

    // A clip from a computer.
    public static ClipEvent ReceivedFrom(string deviceId, string computer, string text, DateTimeOffset? at = null) =>
        new(ValueList.From(new[] { deviceId }), computer, false, MakePreview(text), false, at ?? DateTimeOffset.Now);

    // A short form of a clip: 1 line with single spaces, cut at limit
    // characters. A cut text ends with "…".
    public static string MakePreview(string text, int limit = PreviewLength)
    {
        var t = new StringBuilder();
        var gap = false;
        foreach (var r in text.Trim().EnumerateRunes())
        {
            if (IsSpace(r))
            {
                gap = true;
                continue;
            }
            if (gap && t.Length > 0)
            {
                t.Append(' ');
            }
            gap = false;
            t.Append(r.ToString());
        }
        var s = t.ToString();
        var info = new StringInfo(s);
        if (info.LengthInTextElements <= limit)
        {
            return s;
        }
        return info.SubstringByTextElements(0, Math.Max(0, limit - 1)) + "…";
    }

    // The characters of Kotlin \s: space, tab, line feed, vertical tab, form
    // feed, and carriage return. The apps of the other platforms use the same.
    static bool IsSpace(Rune r) => r.Value is 0x20 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D;
}
