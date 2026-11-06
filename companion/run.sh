#!/usr/bin/env bash
set -euo pipefail
exec "$HOME/.local/share/venvs/python312/bin/python" -B "$(dirname "${BASH_SOURCE[0]}")/dashboard.py" "$@"
