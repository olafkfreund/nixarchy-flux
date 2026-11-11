using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// A test that needs a headless fluxd. FLUX_E2E_BIN names the folder with the
// fluxd and flux-cli programs. Without it, the test is skipped.
public sealed class FluxdFactAttribute : FactAttribute
{
    public FluxdFactAttribute()
    {
        if (Fluxd.BinDir is null)
        {
            Skip = "Set FLUX_E2E_BIN to a folder with fluxd and flux-cli";
        }
    }
}

// A headless fluxd in its own folders, with the CLI to drive it.
public sealed class Fluxd : IAsyncDisposable
{
    public static string? BinDir => Environment.GetEnvironmentVariable("FLUX_E2E_BIN") is { Length: > 0 } d ? d : null;

    readonly Process process;
    readonly StringBuilder log = new();
    readonly Dictionary<string, string> env;

    public string Root { get; }
    public string Downloads => Path.Combine(Root, "Downloads");
    public int UdpPort { get; }
    public int TcpPort { get; }

    Fluxd(string root, int udpPort, int tcpPort, Dictionary<string, string> env, Process process)
    {
        Root = root;
        UdpPort = udpPort;
        TcpPort = tcpPort;
        this.env = env;
        this.process = process;
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    void Append(string? line)
    {
        if (line is null)
        {
            return;
        }
        lock (log)
        {
            log.AppendLine(line);
        }
    }

    public string Log
    {
        get
        {
            lock (log)
            {
                return log.ToString();
            }
        }
    }

    public static async Task<Fluxd> StartAsync(int udpPort, int tcpPort)
    {
        var root = CoreTests.TempDir();
        var run = Path.Combine(root, "run");
        Directory.CreateDirectory(run);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.CreateDirectory(Path.Combine(root, "Downloads"));
        var env = new Dictionary<string, string>
        {
            ["HOME"] = root,
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["XDG_RUNTIME_DIR"] = run,
            ["FLUX_SOCKET"] = Path.Combine(run, "flux", "fluxd.sock"),
        };
        var info = new ProcessStartInfo(Path.Combine(BinDir!, "fluxd"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-headless", "-udp-port", udpPort.ToString(), "-tcp-port", tcpPort.ToString() })
        {
            info.ArgumentList.Add(a);
        }
        foreach (var (k, v) in env)
        {
            info.Environment[k] = v;
        }
        var fluxd = new Fluxd(root, udpPort, tcpPort, env, Process.Start(info)!);
        await Wait.UntilAsync(() => File.Exists(env["FLUX_SOCKET"]), "the socket of fluxd", fluxd);
        return fluxd;
    }

    // Runs flux-cli with the arguments and returns its output. A failure throws
    // with the error text.
    public async Task<string> CliAsync(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(BinDir!, "flux-cli"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }
        foreach (var (k, v) in env)
        {
            info.Environment[k] = v;
        }
        using var p = Process.Start(info)!;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await p.WaitForExitAsync(timeout.Token);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"flux-cli {string.Join(' ', args)} failed: {await error}{await output}");
        }
        return await output;
    }

    // The device entry of the computer with the ID in flux-cli status --json.
    public async Task<JsonObject?> DeviceAsync(string id)
    {
        var status = JsonNode.Parse(await CliAsync("status", "--json"))!;
        return status["devices"]?.AsArray().OfType<JsonObject>().FirstOrDefault(d => Json.String(d["id"]) == id);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
        process.Dispose();
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}

// A FluxCore in its own data folder that records what it shows the user.
public sealed class TestCore : IDisposable
{
    public FluxCore Core { get; }
    public ConcurrentQueue<string> Toasts { get; } = new();
    public ConcurrentQueue<DeviceSnapshot> PairRequests { get; } = new();
    public string Data { get; }

    public TestCore(LanConfig lan, IReadOnlyList<IFluxPlugin> plugins, string name = "Windows Test")
    {
        Data = CoreTests.TempDir();
        var queue = new SerialQueue();
        Core = new FluxCore(new FluxPaths(Data), new FluxHost { DeviceName = () => name, DeviceType = "laptop", AppVersion = "0.0.1", Post = queue.Post }, lan, plugins);
        Core.Toasted += Toasts.Enqueue;
        Core.PairRequested += PairRequests.Enqueue;
        FluxLog.Sink = (level, category, message) => Log.Enqueue($"{level} {category}: {message}");
    }

    // The log lines of all cores in the test process.
    public static ConcurrentQueue<string> Log { get; } = new();

    public DeviceSnapshot? Computer => Core.State.Devices.FirstOrDefault();

    public void Dispose()
    {
        Core.Dispose();
        try
        {
            Directory.Delete(Data, true);
        }
        catch (IOException)
        {
        }
    }
}

public static class Wait
{
    // Checks the condition every 50 ms until it holds or 15 seconds pass.
    public static async Task UntilAsync(Func<bool> condition, string what, Fluxd? fluxd = null, double seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"waited for {what}" + (fluxd is null ? "" : $"\nfluxd log:\n{fluxd.Log}") +
                                   $"\ncore log:\n{string.Join("\n", TestCore.Log)}");
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, Fluxd? fluxd = null, double seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"waited for {what}" + (fluxd is null ? "" : $"\nfluxd log:\n{fluxd.Log}"));
    }
}
