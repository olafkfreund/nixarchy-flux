using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FluxKit;
using Microsoft.Win32;

namespace Flux;

// Protects the private key with DPAPI for the current Windows user.
public sealed class DpapiKeyProtector : IKeyProtector
{
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}

// The start of Flux at login: a value in the Run key of the current user.
// Flux then starts in the tray with --background.
public static class LoginItem
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Flux";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    // Turns the login start on or off. It returns false when Windows refused.
    public static bool Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (on)
            {
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            FluxLog.Error("app", $"cannot change the login start: {e.Message}");
            return false;
        }
    }
}

// Facts about this computer.
public static class ThisComputer
{
    // "laptop" when the computer has a battery, else "desktop".
    public static string DeviceType =>
        System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
            ? "desktop"
            : "laptop";

    // The Downloads folder of the user, from the known folder of Windows.
    public static string Downloads()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0)
            {
                return path;
            }
        }
        catch (Exception e) when (e is COMException or EntryPointNotFoundException)
        {
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out string path);

    // True when Windows uses the dark mode for apps.
    public static bool SystemDark
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
    }

    // True when the taskbar uses the light mode, so that the tray icon draws dark.
    public static bool TaskbarLight
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light == 1;
        }
    }

    // Opens a file, a folder, or a web link with the app that Windows picks.
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            FluxLog.Error("app", $"cannot open: {e.Message}");
        }
    }

    // Shows a file in File Explorer with the file selected.
    public static void ShowInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            FluxLog.Error("app", $"cannot show the file: {e.Message}");
        }
    }
}

// Writes the log to flux.log in the data folder. At 1 MB, the file moves to
// flux.log.1, so that the log stays small.
public sealed class FileLog
{
    const long MaxBytes = 1024 * 1024;
    readonly string path;
    readonly Lock gate = new();

    public FileLog(string path) => this.path = path;

    public void Write(LogLevel level, string category, string message)
    {
        lock (gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {level} {category}: {message}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        Debug.WriteLine($"{level} {category}: {message}");
    }
}
