using System;

namespace AutoMarketbuddy.Services;

// AutoRetainer exposes a cooperative suppression flag. Hold it for a run so
// its retainer, voyage, and confirmation automation stays idle while this
// plugin drives the sale list.
public sealed class AutoRetainerSuppression(Func<bool> isLoaded, Func<bool> getSuppressed,
    Action<bool> setSuppressed)
{
    private bool held;

    public bool IsHeld => held;

    // Leave a suppression owned by another plugin alone and do not clear it later.
    // Re-assert the pause at each step so a mid-run AutoRetainer reload cannot resume it.
    public void Acquire()
    {
        if (!isLoaded() || getSuppressed()) return;
        setSuppressed(true);
        held = true;
    }

    public void Release()
    {
        if (!held) return;
        held = false;
        setSuppressed(false);
    }
}
