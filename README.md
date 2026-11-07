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

Only existing listings change. Stop cannot undo a price the game has already accepted.

Allagan Market and Marketbuddy may stay loaded. Auto-Marketbuddy pauses AutoRetainer through its
interoperability interface while a run is active and restores AutoRetainer afterward. If another
plugin has already paused AutoRetainer, that pause is left in place.
