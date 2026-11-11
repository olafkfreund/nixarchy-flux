using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// CommandsTests.swift of the Mac app, and the command list against a
// headless fluxd.
public class CommandsTests
{
    static IReadOnlyList<RemoteCommand> List(JsonNode? commandList) =>
        RemoteCommand.List(Packet.Parse(new Packet(PacketType.RunCommand, new JsonObject { ["commandList"] = commandList, ["canAddCommand"] = false }).Serialize())!);

    [Fact]
    public void StringListKeepsTheOrderOfTheComputer()
    {
        const string text = """{"zz9":{"name":"Lock screen","command":"omarchy-system-lock"},"a1":{"name":"Suspend","command":"systemctl suspend"},"m5":{"name":"Echo","command":"touch /tmp/ran"}}""";
        Assert.Equal(
            [
                new RemoteCommand("zz9", "Lock screen", "omarchy-system-lock"),
                new RemoteCommand("a1", "Suspend", "systemctl suspend"),
                new RemoteCommand("m5", "Echo", "touch /tmp/ran"),
            ],
            List(text));
    }

    [Fact]
    public void ObjectListGivesNoCommands()
    {
        // fluxd sends commandList as a JSON string.
        var obj = new JsonObject
        {
            ["k1"] = new JsonObject { ["name"] = "Suspend", ["command"] = "systemctl suspend" },
            ["k2"] = new JsonObject { ["command"] = "omarchy-system-lock" },
        };
        Assert.Empty(List(obj));
    }

    [Fact]
    public void BadListsGiveNoCommands()
    {
        Assert.Empty(List("not json"));
        Assert.Empty(List("""["a"]"""));
        Assert.Empty(List(null));
        Assert.Equal([new RemoteCommand("b", "B", "")], List("""{"a":"not an object","b":{"name":"B"}}"""));
    }

    [Fact]
    public void KeyOrderSkipsNestedValuesAndEscapes()
    {
        const string text = """ { "b\"q" : {"name":"x,}{","list":[1,{"k":"]"}]} , "a":"\\" ,"c":null, "b\"q":1 } """;
        Assert.Equal(["b\"q", "a", "c"], JsonKeys.Ordered(text));
        Assert.Empty(JsonKeys.Ordered("[]"));
        // A syntax error ends the read.
        Assert.Equal(["a"], JsonKeys.Ordered("""{"a":1 "b":2}"""));
        Assert.Equal(["é"], JsonKeys.Ordered("""{"é":1}"""));
    }

    [Fact]
    public void RunWithoutALinkSaysSo()
    {
        var toasts = new ConcurrentQueue<string>();
        var plugin = new RunCommandPlugin();
        using var p = new SystemPluginCore([plugin]);
        p.Core.Toasted += toasts.Enqueue;
        plugin.Run("0123456789abcdef0123456789abcdef", new RemoteCommand("k", "Lock", "x"));
        Assert.Equal(["Not connected. Try again in a moment"], toasts);
        Assert.Null(plugin.Model.Commands("0123456789abcdef0123456789abcdef"));
    }

    // flux-cli commands add writes each command to config.toml of fluxd.
    // fluxd sends the list in config order, and this computer keeps it.
    [FluxdFact]
    public async Task TheCommandListArrivesAndACommandRuns()
    {
        await using var fluxd = await Fluxd.StartAsync(32116, 32120);
        await fluxd.CliAsync("commands", "add", "Zeta mark", "touch zeta-ran");
        await fluxd.CliAsync("commands", "add", "Alpha", "true");
        var config = await File.ReadAllTextAsync(Path.Combine(fluxd.Root, "config", "flux", "config.toml"));
        Assert.Contains("touch zeta-ran", config);

        var plugin = new RunCommandPlugin();
        using var t = new TestCore(new LanConfig
        {
            UdpPort = 32131,
            PeerUdpPort = fluxd.UdpPort,
            FirstTcpPort = 32140,
            LastTcpPort = 32148,
            LoopbackOnly = true,
        }, [plugin]);
        var computer = await EndToEndTests.PairFromWindowsAsync(fluxd, t);

        // fluxd sends the list on connect.
        await Wait.UntilAsync(() => plugin.Model.Commands(computer.Id) is { Count: 2 }, "the command list", fluxd);
        var commands = plugin.Model.Commands(computer.Id)!;
        Assert.Equal(["Zeta mark", "Alpha"], commands.Select(c => c.Name));
        Assert.Equal("touch zeta-ran", commands[0].Command);

        // A request gets the list again. The list does not change, so the
        // test removes it first.
        t.Core.Host.Post(() => plugin.Model.Lists = plugin.Model.Lists.Remove(computer.Id));
        await Wait.UntilAsync(() => plugin.Model.Commands(computer.Id) is null, "the removed list");
        plugin.Request(computer.Id);
        await Wait.UntilAsync(() => plugin.Model.Commands(computer.Id) is { Count: 2 }, "the list after a request", fluxd);

        // A run of a command works.
        plugin.Run(computer.Id, commands[0]);
        await Wait.UntilAsync(() => t.Toasts.Contains("Ran “Zeta mark”"), "the toast of the run", fluxd);
        await Wait.UntilAsync(() => File.Exists(Path.Combine(fluxd.Root, "zeta-ran")), "the command on fluxd", fluxd);
        plugin.Run(computer.Id, commands[1]);
        await Wait.UntilAsync(() => fluxd.Log.Contains($"runs {commands[1].Key}: true"), "the second command on fluxd", fluxd);

        // A new command reaches this computer without a request.
        await fluxd.CliAsync("commands", "add", "Beta", "true");
        await Wait.UntilAsync(() => plugin.Model.Commands(computer.Id) is { Count: 3 }, "the new list", fluxd);
        Assert.Equal("Beta", plugin.Model.Commands(computer.Id)![2].Name);
    }
}
