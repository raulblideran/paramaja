#!/usr/bin/env bash
# Runs every test suite. Pass --net to also run the real-swarm torrent test (Sintel, CC-BY).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="$HOME/.dotnet:$HOME/.cargo/bin:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

"$ROOT/tests/fixtures/make-fixtures.sh" >/dev/null
# --lib builds libparamaja_torrent.so itself, which the .NET tests load; tests and examples alone don't.
(cd "$ROOT/native/paramaja-torrent" && cargo build --lib --examples && cargo test)
if [[ "${1:-}" == "--net" ]]; then
  (cd "$ROOT/native/paramaja-torrent" && cargo test --test net_public -- --ignored --nocapture)
fi
# On GitHub Actions each failing test also becomes an annotation on the run.
LOGGER=()
if [[ -n "${GITHUB_ACTIONS:-}" ]]; then LOGGER=(--logger GitHubActions); fi
dotnet test "$ROOT/Paramaja.sln" "${LOGGER[@]}"
