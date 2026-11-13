# Auto-Marketbuddy

Sets retainer prices below the lowest competing listing.

Add this repository in Dalamud:

```
https://raw.githubusercontent.com/amarinne/Auto-Marketbuddy/main/repo.json
```

Open a retainer's sale list and run:

- `/amb preview` — show the price for each item.
- `/amb apply` — set the prices.
- `/amb stop` — cancel.

Only existing listings change. Stop cannot undo a price the game has already accepted. An item with no
competing listing of its quality keeps its price, and the run continues to the next item.

The window also shows a price-change history for the current game session. It includes manual runs,
AutoRetainer handoff runs, and price edits that you make in the retainer sale list. The history does
not request market data. Rows follow the retainer sale list from top to bottom. Use Clear to remove
the list. Each new baseline and each new row also adds one Information line to the Dalamud log.

## AutoRetainer

Auto-Marketbuddy can run as an AutoRetainer post-process step. Choose the mode in the window
under "During AutoRetainer", or use a command:

- `/amb autoretainer preview` reports prices without changing them.
- `/amb autoretainer apply` changes prices.
- `/amb autoretainer off` returns to manual control.

AutoRetainer decides which retainers it processes. At each retainer, AutoRetainer finishes its own
work, then waits for Auto-Marketbuddy. Auto-Marketbuddy opens the sale list, runs one normal preview
or apply, closes the sale list, returns the retainer menu, and lets AutoRetainer continue. The
retainer keeps its own slot, item, quality, quantity, and current-price checks. A failed run stops
without retrying; Stop releases AutoRetainer.

This handoff was verified against AutoRetainer 4.6.2.18. An AutoRetainer build without the same
post-process endpoints does not hand over; use `/amb preview` or `/amb apply` manually. If
Auto-Marketbuddy unloads after it asks AutoRetainer for a retainer but before AutoRetainer reaches
that retainer, AutoRetainer can wait for a callback that no longer exists. Do not unload the plugin
in the middle of an AutoRetainer pass.

Allagan Market and Marketbuddy may stay loaded. Auto-Marketbuddy pauses AutoRetainer through its
interoperability interface while a run is active and restores AutoRetainer afterward. If another
plugin has already paused AutoRetainer, that pause is left in place.
