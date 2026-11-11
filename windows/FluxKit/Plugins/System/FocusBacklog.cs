using System.Text.Json.Nodes;

namespace FluxKit;

// The Do Not Disturb change that paired computers did not get yet. A change
// can come while a computer has no link, or while Flux does not run. The
// change then waits here for each computer that was paired at the change,
// until that computer connects. The state lives in the settings, so that it
// also waits across a restart of Flux.
//
// Only a change counts: the first state that Flux sees sets the start value,
// and a state that equals the last one does nothing. A computer that pairs
// after the change does not get it. FocusBacklog.swift of the Mac app has the
// same rules.
internal sealed class FocusBacklog(FluxSettings settings)
{
    // The last Do Not Disturb state that Flux saw, also in an earlier run.
    internal const string FocusKey = "dnd.focus";

    // The state that computers still need.
    internal const string UnsentKey = "dnd.unsent";

    // The IDs of the computers that still need UnsentKey.
    internal const string WaitingKey = "dnd.waiting";

    readonly Lock gate = new();

    // Takes a state. It returns true for a change, which then waits for each
    // computer in paired, the computers that are paired now. keep false
    // records the state but keeps no change, for example while the sync is
    // off.
    public bool Report(bool on, IReadOnlyList<string> paired, bool keep = true)
    {
        lock (gate)
        {
            var last = Json.Bool(settings.Node(FocusKey));
            settings.Set(FocusKey, on);
            if (last is null || last == on)
            {
                return false;
            }
            if (!keep)
            {
                ClearLocked();
                return false;
            }
            Save(on, paired);
            return true;
        }
    }

    // Records the computers that got the state on.
    public void Reached(IReadOnlyList<string> ids, bool on)
    {
        lock (gate)
        {
            if (Json.Bool(settings.Node(UnsentKey)) != on)
            {
                return;
            }
            Save(on, Waiting().Where(id => !ids.Contains(id)).ToArray());
        }
    }

    // Returns the state that the computer did not get yet, and records that
    // it gets it now. It returns null when nothing waits for it.
    public bool? Take(string id)
    {
        lock (gate)
        {
            if (Json.Bool(settings.Node(UnsentKey)) is not { } on)
            {
                return null;
            }
            var waiting = Waiting();
            if (!waiting.Contains(id))
            {
                return null;
            }
            Save(on, waiting.Where(w => w != id).ToArray());
            return on;
        }
    }

    // Stops the wait of a computer that was unpaired, so that it gets no old
    // change when it pairs again.
    public void Forget(string id)
    {
        lock (gate)
        {
            if (Json.Bool(settings.Node(UnsentKey)) is not { } on)
            {
                return;
            }
            Save(on, Waiting().Where(w => w != id).ToArray());
        }
    }

    // Forgets the change that waits.
    public void Clear()
    {
        lock (gate)
        {
            ClearLocked();
        }
    }

    IReadOnlyList<string> Waiting() => Json.Strings(settings.Node(WaitingKey));

    // Stores the change and the computers that wait for it. It forgets the
    // change when no computer waits.
    void Save(bool on, IReadOnlyList<string> waiting)
    {
        if (waiting.Count == 0)
        {
            ClearLocked();
            return;
        }
        settings.Set(UnsentKey, on);
        settings.Set(WaitingKey, new JsonArray(waiting.Select(id => (JsonNode)id).ToArray()));
    }

    void ClearLocked()
    {
        settings.Set(UnsentKey, null);
        settings.Set(WaitingKey, null);
    }
}
