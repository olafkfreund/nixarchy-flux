namespace FluxKit;

// The text clipboard of Windows. The WPF app implements it, and the tests use
// a fake. ClipboardPlugin calls each member on a thread of the pool, 1 call at
// a time. An implementation that needs the UI thread, such as the Clipboard
// class of WPF, moves the call there itself. The Win32 clipboard functions
// work from any thread.
//
// While another app holds the clipboard open, a member can throw an
// ExternalException, for example the COMException of WPF. The plugin then
// skips the step, and the next poll tries again.
public interface IClipboard
{
    // The Unicode text on the clipboard, or null when it holds no text.
    string? ReadText();

    // Puts text from a computer on the clipboard as Unicode text. Do not add
    // the formats that mark data as private: the plugin then reads the text
    // as a secret and does not sync it to the other computers.
    void WriteText(string text);

    // A number that changes each time an app writes the clipboard, also this
    // app. On Windows, it is GetClipboardSequenceNumber. Reading it does not
    // open the clipboard.
    long ChangeCount { get; }

    // True when the source marks the data on the clipboard as private, so
    // that sync leaves it alone. On Windows, the source adds the format
    // ExcludeClipboardContentFromMonitorProcessing, or the format
    // CanIncludeInClipboardHistory or CanUploadToCloudClipboard with the
    // DWORD value 0. Password managers do this.
    bool HoldsPrivate { get; }
}
