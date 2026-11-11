using FluxKit;

namespace FluxKit.Tests;

// FocusBacklogTests.swift of the Mac app. The settings file takes the place
// of the defaults.
public class SystemFocusBacklogTests
{
    static FluxSettings MakeSettings() => new(Path.Combine(CoreTests.TempDir(), "settings.json"));

    [Fact]
    public void TheFirstStateIsTheStart()
    {
        var b = new FocusBacklog(MakeSettings());
        Assert.False(b.Report(true, ["a"])); // the first state is the start value
        Assert.False(b.Report(true, ["a"])); // the same state is not a change
        Assert.Null(b.Take("a")); // no change waits
    }

    [Fact]
    public void AChangeWaitsForEachComputer()
    {
        var b = new FocusBacklog(MakeSettings());
        b.Report(false, ["a", "b"]);
        Assert.True(b.Report(true, ["a", "b"])); // a change while no computer is connected
        Assert.True(b.Take("a")); // the computer gets the change when it connects
        Assert.Null(b.Take("a")); // a computer gets a change once
        Assert.True(b.Take("b")); // each computer gets the change
    }

    [Fact]
    public void AComputerThatPairsLaterGetsNoChange()
    {
        var settings = MakeSettings();
        var b = new FocusBacklog(settings);
        b.Report(false, ["a"]);
        b.Report(true, ["a"]);
        Assert.Null(b.Take("c")); // a computer that was not paired at the change gets nothing at pairing
        Assert.True(b.Take("a"));
        Assert.Null(settings.Node(FocusBacklog.UnsentKey)); // the change is gone after each computer got it
        Assert.Null(b.Take("d"));
    }

    [Fact]
    public void AChangeWithNoPairedComputerWaitsForNothing()
    {
        var b = new FocusBacklog(MakeSettings());
        b.Report(false, []);
        Assert.True(b.Report(true, []));
        Assert.Null(b.Take("a")); // the first computer that pairs later gets nothing
    }

    [Fact]
    public void ComputersThatGotTheChangeGetNoCopy()
    {
        var settings = MakeSettings();
        var b = new FocusBacklog(settings);
        b.Report(false, ["a", "b"]);
        b.Report(true, ["a", "b"]);
        b.Reached(["a"], true);
        Assert.Null(b.Take("a")); // the change went to the connected computer
        b.Reached(["b"], false);
        Assert.True(b.Take("b")); // a report for an older state does not count
        b.Report(false, ["a", "b"]);
        b.Reached(["a", "b"], false);
        Assert.Null(settings.Node(FocusBacklog.UnsentKey)); // the change is gone when each computer got it at once
    }

    [Fact]
    public void AnUnpairedComputerStopsItsWait()
    {
        var b = new FocusBacklog(MakeSettings());
        b.Report(false, ["a", "b"]);
        b.Report(true, ["a", "b"]);
        b.Forget("a");
        Assert.Null(b.Take("a")); // a computer that pairs again gets no old change
        Assert.True(b.Take("b")); // the other computers still wait
    }

    [Fact]
    public void ANewChangeReplacesTheOldOne()
    {
        var b = new FocusBacklog(MakeSettings());
        b.Report(false, ["a"]);
        b.Report(true, ["a"]);
        Assert.True(b.Take("a"));
        Assert.True(b.Report(false, ["a"]));
        Assert.False(b.Take("a")); // the newest state goes out
    }

    [Fact]
    public void TheChangeOutlivesFlux()
    {
        var path = Path.Combine(CoreTests.TempDir(), "settings.json");
        var before = new FocusBacklog(new FluxSettings(path));
        before.Report(false, ["a"]);
        before.Report(true, ["a"]);
        var after = new FocusBacklog(new FluxSettings(path));
        Assert.False(after.Report(true, ["a"])); // the state at the next start equals the last one
        Assert.True(after.Take("a")); // the change waits across a restart
    }

    [Fact]
    public void AChangeWhileFluxDidNotRun()
    {
        var path = Path.Combine(CoreTests.TempDir(), "settings.json");
        new FocusBacklog(new FluxSettings(path)).Report(false, ["a"]);
        var next = new FocusBacklog(new FluxSettings(path));
        Assert.True(next.Report(true, ["a"])); // a new state at the start is a change
        Assert.True(next.Take("a"));
    }

    [Fact]
    public void NoChangeWaitsWhileTheSyncIsOff()
    {
        var b = new FocusBacklog(MakeSettings());
        b.Report(false, ["a"]);
        b.Report(true, ["a"]);
        Assert.False(b.Report(false, ["a"], keep: false));
        Assert.Null(b.Take("a")); // the sync is off, so the change goes nowhere
        Assert.False(b.Report(false, ["a"])); // the state was recorded
    }

    [Fact]
    public void ClearForgetsTheChange()
    {
        var b = new FocusBacklog(MakeSettings());
        b.Report(false, ["a"]);
        b.Report(true, ["a"]);
        b.Clear();
        Assert.Null(b.Take("a"));
    }
}
