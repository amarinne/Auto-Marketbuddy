# XIV Companion

This native desktop window shows the existing Dalamud log and read-only game state. It runs independently of Auto-Marketbuddy.

The default Overview shows AMB's current operation, game conditions, and plugin health. The AMB Trace shows the captured retainer ID, world ID, inventory slot, item ID, quality, quantity, and original price. It shows comparison age, eligible and excluded listing counts, proposed price, confirmation status, and the last 30 transitions. It distinguishes a submitted confirmation from local price verification. Local verification does not prove server persistence.

Comparisons show their source, Cache or In-game, and age. Cached responses expire five minutes after capture. Apply fetches missing or expired entries in game.

The Plugins tab shows installed versions, API levels, loaded state, API mismatch and banned flags, recent errors, and slow callbacks. Counts cover only the retained log. They do not establish that a plugin is currently failing. The Game tab retains the basic player, location, target, and cast information.

The log view supports severity, plugin/source, Relevant/AMB/Everything views, search, pause, follow, and clear view. Relevant is the default. It keeps AMB/bridge activity, warnings, errors, and slow callbacks, and hides routine profile metadata and unrelated informational messages. Repository Errors Only is on by default; repository errors still appear. Selecting an exact source reveals its routine messages. Everything plus disabling Repository Errors Only reveals all retained messages permitted by the severity filter.

Full Metadata is off by default. Compact rows omit timestamps, timezone, and informational severity prefixes. Full Metadata restores the original log lines. Filtering never changes the source file. The view retains at most 2,000 lines in memory. Clear View does not change the log file. The reader handles file replacement, truncation, and incomplete UTF-8 lines. Stack trace lines inherit the severity and source of their parent entry.

The state view shows the player, world, job, level, HP, MP, position, location, active condition flags, target, and current casts. It clears these fields when the bridge stops, reports an error, or has no fresh snapshot for three seconds. Casts use action IDs; positions use game coordinates.

## Open the window

```sh
./companion/run.sh
```

The launcher uses the shared Python environment at `$HOME/.local/share/venvs/python312`. Tkinter is the only GUI dependency. No server, network listener, or project environment is required.

The default inputs are `$HOME/.xlcore/logs/dalamud.log` and `$HOME/.xlcore/pluginConfigs/XivCompanionBridge/state.json`. Use `--log PATH` and `--state PATH` to override them.

The same readers support terminal access:

```sh
./companion/run.sh --state-once
./companion/run.sh --diagnostics-once
./companion/run.sh --logs-once
./companion/run.sh --logs-once --scope AMB --level Warnings+
./companion/run.sh --logs-once --scope Everything --level All --include-repository --metadata
```

`--state-once` returns exit code 1 when fresh state is unavailable. The log command prints a bounded recent tail. Neither command writes to the game or its files.

## State bridge

`bridge/` contains a separate API 15 plugin, `XivCompanionBridge` version `0.2.0.0`. It runs without Auto-Marketbuddy, Marketbuddy, Allagan Market, or another third-party plugin. AMB operation data requires the diagnostic AMB build. Missing, unavailable, malformed, or stale operation data is shown explicitly; the dashboard does not invent an idle or safe state.

The bridge reads public Dalamud services on the framework thread at most twice per second. It refreshes the plugin inventory every two seconds and observes the visibility of six market/dialog windows. It copies managed values into an immutable snapshot. A background worker serializes the bridge snapshot and replaces `state.json` through a temporary file. A bounded queue keeps only the newest pending snapshot. File access never runs in the framework callback.

When AMB is loaded, the bridge calls the optional `AutoMarketbuddy.Diagnostics.V1` endpoint. AMB serializes a bounded, copied managed snapshot; the endpoint cannot read game memory or issue controls. AMB records diagnostic snapshots twice per second and unregisters the endpoint on unload. This adds no dependency to AMB's transaction path and does not change its lifecycle model.

The snapshot contains character and target names, position, state flags, plugin inventory, visible window names, and optional AMB operation evidence. It stays in the local plugin configuration directory. Listing targets include a retainer ID; they omit the character content ID. The bridge does not capture chat, credentials, or session arguments. The dashboard opens both input files for reading only.

The bridge uses no hooks, direct native pointer access, injected input, or market commands. Its only IPC call reads the owned AMB diagnostic endpoint. Unload removes the update subscription, publishes a stopped snapshot, and awaits the writer. The dashboard can close independently; unloading the bridge is separate. Stop an AMB run through `/aundercut stop` or AMB's in-game Stop control. The outside dashboard has no inbound game-control route.

## Build and verify

```sh
COMPANION_UI_CHECK=1 ./companion/check.sh
```

The window smoke check requires the owner's desktop session. Omit `COMPANION_UI_CHECK` for the file-reader and bridge checks without a display.

The script uses the shared .NET SDK 10.0.403, shared CLI state, and default NuGet cache. It uses the same API 15 references as `scripts/check.sh`. It builds with the package lock and treats warnings as errors. Build products and test fixtures stay under `${TMPDIR:-/tmp}` and are removed on exit.

The checks cover queue replacement, shutdown, atomic output, file failures, assembly and manifest identity, and the actual C# bridge and AMB diagnostic types consumed by Python. Python checks cover log rotation, truncation, incomplete lines, malformed data, independent bridge/AMB freshness, source and severity filters, compact/raw formatting, stack traces, pause, clear, and window shutdown.

Run `./scripts/check.sh` for AMB behavior and package checks. They also verify that reading diagnostics cannot advance controls, pricing evidence uses the decision filter, confirmation state survives Stop, and a restart clears old evidence.

To retain the three plugin files for an owner-controlled test:

```sh
COMPANION_PACKAGE_DIR=/tmp/xiv-companion-owner-test ./companion/check.sh
```

## First game test

1. Reach a safe idle location or the title screen before loading new code.
2. Register `XivCompanionBridge.dll` with Dalamud's development-plugin mechanism and load only this bridge.
3. Open the desktop window and check that its connection becomes green within three seconds.
4. Check plugin versions and visibility observations against Dalamud. Use Game to compare player and location.
5. Unload the bridge and verify that the cards clear and the window reports `Bridge stopped`.
6. Reload it and verify that fresh state resumes. Close the desktop window to check independent shutdown.

Do not load or reload development plugins during a duty, cutscene, or other active gameplay. The bridge's compilation and offline checks do not prove its live Dalamud lifecycle. Log collection works before the bridge is loaded. No other plugin must be enabled for this test.

Arrange a separate owner-controlled AMB Preview test to verify a real retainer target and pricing trace. Keep competing retainer automation disabled. Preview opens market windows but does not submit a price. Do not run Apply as part of companion verification. A bridge upgrade alone cannot provide operation data from an older AMB build.
