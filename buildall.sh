#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir"

dotnet restore opencode_cs/OpenCode.slnx
dotnet build opencode_cs/OpenCode.slnx --no-restore
