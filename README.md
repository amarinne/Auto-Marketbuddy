# Auto-Marketbuddy

Auto-Marketbuddy previews or sets the prices of existing FFXIV retainer listings to a target price. The local 1.1.0.0 candidate targets Dalamud API 15 and .NET 10. It does not require Marketbuddy or Allagan Market.

Open a retainer's sale list, then use `/amb` (or `/aundercut`):

- `preview` reads fresh in-game comparisons and shows proposed prices. `start` is an alias for preview.
- `apply` uses cached comparisons and fetches missing or expired entries in game. The window has the same controls.
- `stop` cancels the run. Closing the control window also stops it.
- `status` prints the current state and error.

The control window opens when the retainer sale list opens and closes when you leave it. Turn that off with the "Open/close with retainer sell list" checkbox in the window if you prefer to open and close it manually. The "Exit retainer when finish" checkbox closes the sell list and the retainer menu when a run completes, leaving you at retainer selection. Results scroll in a fixed-height list that follows the newest entry.

The plugin subtracts the configured gil amount once from the lowest competing unit price of the same quality, and Apply sets each listing to that target — lowering it or raising it as needed. It excludes mannequins and retainers registered to the current character. It leaves listings unchanged when no matching competitor exists or the current price already equals the target.

When the market shows no competing listing at all, the plugin can instead price from the newest completed sale of the same item and quality, minus the same undercut. That fallback is off by default; enable "Use recent sale price when no listings" in the window to use it. With it off, an empty market leaves the listing unchanged. Sale-history prices are per unit, and mannequin sales are ignored.

Comparisons stay cached for five minutes per item and quality, across retainers on the same character and world. Reuse does not extend expiry. Preview refreshes each distinct item and quality once for the current retainer. Apply recalculates each target with the current undercut amount and still verifies the listing before confirmation. Logout, character/world change, and plugin reload clear the cache.

Each run binds to one character, world, retainer, and sale-list instance. It opens items by inventory slot, checks item identity, quantity, and current price before confirmation, and waits for the local listing price to match before continuing. Missing data, changed targets, unsupported controls, and timeouts stop the run without retries. Stop cannot undo a price confirmation already sent to the game.

Marketbuddy may stay loaded. At the start of a run the plugin takes Marketbuddy's own interop lock (`Marketbuddy.Lock`, released at the end of the run), so Marketbuddy's auto-open, clipboard-paste, and click-to-set-price handlers stay out while Auto-Marketbuddy drives the same price editor it uses itself — set the asking price, then confirm. If Marketbuddy is loaded but its lock is unavailable, the run stops before touching prices. Keep Allagan Market, AutoRetainer, and other retainer automation disabled during a run; the plugin refuses to start or continue while those are loaded. It does not change another plugin's configuration.

## Build and checks

On Linux x64, run:

```sh
./scripts/check.sh
```

The script uses the shared .NET SDK 10.0.403 at `$HOME/.local/share/dotnet` (`DOTNET_ROOT`) and `Dalamud.NET.Sdk/15.0.0`. It reuses CLI state at `$HOME/.local/share/dotnet-cli` and packages at `$HOME/.nuget/packages`, honours `DOTNET_BIN` and `DOTNET_ROOT`, and stops if no .NET SDK is available; it never downloads one. Set `DALAMUD_HOME` to the API 15 reference directory; the default is `$HOME/.xlcore/dalamud/Hooks/15.0.3.9`.

The script builds Release with the package lock, treats warnings as errors, runs offline behavior checks, and checks the ZIP manifest against the assembly, source manifest, and catalog. Per-task build products stay under `${TMPDIR:-/tmp}` (override with `AMB_WORK_ROOT`) and are removed on exit. Shared tools, CLI state, and packages are preserved. No plugin is installed or enabled.

For a later owner-controlled development test, retain only the package files with:

```sh
AMB_PACKAGE_DIR=/tmp/amb-owner-test ./scripts/check.sh
```

The equivalent build command with the pinned SDK available is:

```sh
DOTNET_ROOT="$HOME/.local/share/dotnet" \
DOTNET_CLI_HOME="$HOME/.local/share/dotnet-cli" \
NUGET_PACKAGES="$HOME/.nuget/packages" \
DALAMUD_HOME="$HOME/.xlcore/dalamud/Hooks/15.0.3.9" \
"$HOME/.local/share/dotnet/dotnet" build \
  AutoMarketbuddy/AutoMarketbuddy.csproj -c Release \
  --artifacts-path /tmp/amb-build -p:RestoreLockedMode=true -warnaserror
```

See [verification](docs/verification.md) for the acceptance sequence and native boundaries that offline checks cannot prove.

An optional [outside companion window](companion/README.md) shows Dalamud logs and read-only game state. Its separate state bridge is independent of Auto-Marketbuddy.

The companion also shows AMB's phase, wait reason, deadline, exact listing target, pricing evidence, confirmation status, and recent transitions. AMB exposes copied diagnostic state through the optional read-only `AutoMarketbuddy.Diagnostics.V1` endpoint. No companion is required to preview, apply, or stop a run.

## Release

1.1.0 is published as a GitHub release with `latest.zip`. `repo.json` describes 1.1.0.0/API 15 and points at the versioned `v1.1.0` asset. The obsolete checked-in `latest.zip` was removed; build the package from source with `./scripts/check.sh`.
