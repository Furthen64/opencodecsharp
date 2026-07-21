#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir"

export OPENCODE_DB="${OPENCODE_DB:-/tmp/opencode-smoke.db}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5179}"
export OpenCode__Model="${OpenCode__Model:-opencode/big-pickle}"

exec dotnet run --project opencode_cs/src/OpenCode.Server -- "$@"
