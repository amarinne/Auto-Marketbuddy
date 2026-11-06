#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
work_root=${COMPANION_WORK_ROOT:-${TMPDIR:-/tmp}}
work=$(mktemp -d "$work_root/xiv-companion-check.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.local/share/dotnet}" DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOME/.local/share/dotnet-cli}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}" DALAMUD_HOME="${DALAMUD_HOME:-$HOME/.xlcore/dalamud/Hooks/15.0.3.9}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export COMPANION_TEST_DIR="$work" COMPANION_CONTRACT="$work/fixtures/contract.json"
"${DOTNET_BIN:-$DOTNET_ROOT/dotnet}" build companion/bridge/XivCompanionBridge.csproj -c Release --artifacts-path "$work/artifacts" -p:RestoreLockedMode=true -warnaserror
"${DOTNET_BIN:-$DOTNET_ROOT/dotnet}" run --project companion/tests/BridgeChecks.csproj -c Release --artifacts-path "$work/checks" -- \
    "$work/fixtures" "$work/artifacts/bin/XivCompanionBridge/release/XivCompanionBridge.dll" \
    "$work/artifacts/bin/XivCompanionBridge/release/XivCompanionBridge.json"
"$HOME/.local/share/venvs/python312/bin/python" -B -m unittest discover -s companion/tests -p 'test_*.py' -v
if [[ -n ${COMPANION_PACKAGE_DIR:-} ]]; then
    mkdir -p "$COMPANION_PACKAGE_DIR"
    cp "$work/artifacts/bin/XivCompanionBridge/release/XivCompanionBridge.dll" \
       "$work/artifacts/bin/XivCompanionBridge/release/XivCompanionBridge.json" \
       "$work/artifacts/bin/XivCompanionBridge/release/XivCompanionBridge.deps.json" "$COMPANION_PACKAGE_DIR/"
fi
echo 'Companion checks passed. Temporary outputs are removed; shared tools and packages are preserved.'
