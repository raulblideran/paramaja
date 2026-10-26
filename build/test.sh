#!/usr/bin/env bash
# Runs every test suite. Pass --net to also run the real-swarm torrent test (Sintel, CC-BY).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="$HOME/.dotnet:$HOME/.cargo/bin:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

"$ROOT/tests/fixtures/make-fixtures.sh" >/dev/null
(cd "$ROOT/native/paramaja-torrent" && cargo build --examples && cargo test)
if [[ "${1:-}" == "--net" ]]; then
  (cd "$ROOT/native/paramaja-torrent" && cargo test --test net_public -- --ignored --nocapture)
fi
dotnet test "$ROOT/Paramaja.sln"
