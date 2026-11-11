using System.Runtime.InteropServices;
using System.Windows;
using FluxKit;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace Flux;

// The text clipboard of Windows. Reads go through Win32, which works from
// any thread. A write needs a window that owns the clipboard, so it goes
// through WPF on the UI thread. A busy clipboard throws ExternalException
// after a few tries, and the plugin tries again on its next step.
public sealed class WindowsClipboard : IClipboard
{
    const uint UnicodeText = 13;
    static readonly uint Exclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint History = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    static readonly uint Cloud = RegisterClipboardFormat("CanUploadToCloudClipboard");

    public long ChangeCount => GetClipboardSequenceNumber();

    public bool HoldsPrivate => IsClipboardFormatAvailable(Exclude) || DwordIsZero(History) || DwordIsZero(Cloud);

    public string? ReadText()
    {
        if (!IsClipboardFormatAvailable(UnicodeText))
        {
            return null;
        }
        Open();
        try
        {
            var handle = GetClipboardData(UnicodeText);
            if (handle == IntPtr.Zero)
            {
                return null;
            }
            var data = GlobalLock(handle);
            try
            {
                return data == IntPtr.Zero ? null : Marshal.PtrToStringUni(data);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void WriteText(string text)
    {
        var app = Application.Current ?? throw new ExternalException("no UI thread");
        app.Dispatcher.Invoke(() =>
        {
            for (var i = 0; ; i++)
            {
                try
                {
                    Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
                    return;
                }
                catch (ExternalException) when (i < 5)
                {
                    Thread.Sleep(40);
                }
            }
        });
    }

    // True when the format holds the DWORD 0, the mark of a private copy.
    static bool DwordIsZero(uint format)
    {
        if (!IsClipboardFormatAvailable(format))
        {
            return false;
        }
        Open();
        try
        {
            var handle = GetClipboardData(format);
            var data = handle == IntPtr.Zero ? IntPtr.Zero : GlobalLock(handle);
            if (data == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return Marshal.ReadInt32(data) == 0;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    // Opens the clipboard for a read. Another app can hold it for a moment.
    static void Open()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return;
            }
            Thread.Sleep(20);
        }
        throw new ExternalException("the clipboard is busy");
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr handle);
}

// The battery of this computer from the power status of Windows. Windows
// reports a change of the power source. A timer catches a change of the
// charge every minute.
public sealed class BatterySource : IBatterySource
{
    readonly Timer timer;
    BatteryState? last;

    public BatterySource()
    {
        last = Read();
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.StatusChange)
            {
                Check();
            }
        };
        timer = new Timer(_ => Check(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public event Action? Changed;

    public BatteryState? Read()
    {
        var s = System.Windows.Forms.SystemInformation.PowerStatus;
        if (s.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) ||
            s.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Unknown))
        {
            return null;
        }
        return BatteryState.FromLevel(s.BatteryLifePercent, s.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online);
    }

    void Check()
    {
        var now = Read();
        if (now != last)
        {
            last = now;
            Changed?.Invoke();
        }
    }
}

// The Flux toasts that Windows still shows, for the notification limit after
// a restart.
public sealed class ToastHistory : INotificationHistory
{
    public IReadOnlyList<string> DeliveredIds()
    {
        try
        {
            return ToastNotificationManager.History.GetHistory(ToastBackend.AppId).Select(t => t.Tag).ToArray();
        }
        catch (Exception e) when (e is COMException or ArgumentException)
        {
            return [];
        }
    }
}
