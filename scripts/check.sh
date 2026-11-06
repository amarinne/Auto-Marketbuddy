#!/usr/bin/env bash
# Reuse shared tools and packages. Only task build products are disposable.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
work_root=${AMB_WORK_ROOT:-${TMPDIR:-/tmp}}
work=$(mktemp -d "$work_root/auto-marketbuddy-check.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.local/share/dotnet}"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOME/.local/share/dotnet-cli}" NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
export DALAMUD_HOME="${DALAMUD_HOME:-$HOME/.xlcore/dalamud/Hooks/15.0.3.9}"
if [[ ! -f "$DALAMUD_HOME/Dalamud.dll" || ! -f "$DALAMUD_HOME/FFXIVClientStructs.dll" ]]; then
    echo 'Set DALAMUD_HOME to the installed API 15 reference directory.' >&2
    exit 1
fi
dotnet_bin="${DOTNET_BIN:-$DOTNET_ROOT/dotnet}"
[[ -x $dotnet_bin ]] || dotnet_bin=$(command -v dotnet || true)
if [[ ! -x "$dotnet_bin" ]]; then
    echo "No .NET SDK found. Install .NET SDK 10.0.403 or set DOTNET_BIN." >&2
    exit 1
fi
actual_sdk=$("$dotnet_bin" --version)
[[ $actual_sdk == 10.0.403 ]] || { echo 'This checkout requires .NET SDK 10.0.403.' >&2; exit 1; }
echo ".NET SDK: $actual_sdk"
"$dotnet_bin" build AutoMarketbuddy/AutoMarketbuddy.csproj -c Release --artifacts-path "$work/artifacts" -p:RestoreLockedMode=true -warnaserror
package="$work/artifacts/bin/AutoMarketbuddy/release/AutoMarketbuddy/latest.zip"
"$dotnet_bin" run --project tests/AutoMarketbuddy.Checks.csproj -c Release --artifacts-path "$work/checks" -- "$package" "$PWD"
if [[ -n ${AMB_PACKAGE_DIR:-} ]]; then
    mkdir -p "$AMB_PACKAGE_DIR"
    cp "$work/artifacts/bin/AutoMarketbuddy/release/AutoMarketbuddy.dll" \
       "$work/artifacts/bin/AutoMarketbuddy/release/AutoMarketbuddy.json" \
       "$work/artifacts/bin/AutoMarketbuddy/release/AutoMarketbuddy.deps.json" \
       "$package" "$AMB_PACKAGE_DIR/"
    echo "Owner-requested package output: $AMB_PACKAGE_DIR"
fi
echo 'Checks passed. Temporary build outputs are removed on exit; shared tools and packages are preserved.'
