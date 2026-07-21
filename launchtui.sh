#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir"

export OPENCODE_SERVER_URL="${OPENCODE_SERVER_URL:-http://127.0.0.1:5179}"

server_pid=""

if ! curl --fail --silent --show-error "$OPENCODE_SERVER_URL/global/health" >/dev/null 2>&1; then
  server_log="$(mktemp /tmp/opencode-csharp-server.XXXXXX.log)"
  echo "Starting local OpenCode server..."
  ASPNETCORE_URLS="$OPENCODE_SERVER_URL" ./launchserver.sh >"$server_log" 2>&1 &
  server_pid=$!

  for _ in $(seq 1 100); do
    if curl --fail --silent --show-error "$OPENCODE_SERVER_URL/global/health" >/dev/null 2>&1; then
      break
    fi
    if ! kill -0 "$server_pid" 2>/dev/null; then
      echo "Server failed to start; log: $server_log" >&2
      exit 1
    fi
    sleep 0.1
  done

  if ! curl --fail --silent --show-error "$OPENCODE_SERVER_URL/global/health" >/dev/null 2>&1; then
    echo "Server did not become ready; log: $server_log" >&2
    exit 1
  fi
fi

cleanup() {
  if [[ -n "$server_pid" ]]; then
    kill "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

dotnet run --project opencode_cs/src/OpenCode.Tui -- "$@"
