namespace FluxKit;

// Keeps Do Not Disturb sync from sending a change back to the side that made
// it. known is the last state of this computer. A state from a computer sets
// it before this computer applies the state, so the report that follows does
// not count as a local change. fluxd, the Mac app, and the Android app have
// the same guard. DndGuard.swift of the Mac app has the same rules.
internal sealed class DndGuard(long settleMs = 3_000)
{
    readonly Lock gate = new();
    bool known;
    bool valid;
    // A state from a computer that this computer does not report yet.
    bool pending;
    // The end of the wait for the pending state, in milliseconds.
    long until;

    // Takes a state that this computer reports, at the time now in
    // milliseconds. It returns true when it is a local change that the
    // computers must get. The first state only sets the start value.
    public bool Local(bool on, long now)
    {
        lock (gate)
        {
            if (pending)
            {
                if (on == known)
                {
                    pending = false;
                    return false;
                }
                // This computer still reports the state from before the change.
                if (now < until)
                {
                    return false;
                }
                // The change from the computer did not apply. The local state wins.
                pending = false;
            }
            if (!valid)
            {
                known = on;
                valid = true;
                return false;
            }
            if (on == known)
            {
                return false;
            }
            known = on;
            return true;
        }
    }

    // Takes a state from a computer. It returns true when this computer must
    // apply it.
    public bool Remote(bool on, long now)
    {
        lock (gate)
        {
            if (valid && on == known)
            {
                return false;
            }
            known = on;
            valid = true;
            pending = true;
            until = now + settleMs;
            return true;
        }
    }

    // Starts the wait again after the apply of the state ends, because the
    // report can come only after the apply. fluxd does the same.
    public void Applied(bool on, long now)
    {
        lock (gate)
        {
            if (pending && known == on)
            {
                until = now + settleMs;
            }
        }
    }
}
