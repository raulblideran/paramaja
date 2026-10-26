#!/usr/bin/env bash
# Prints a version's release notes from CHANGELOG.md, as Markdown for the GitHub Release:
#   ./build/release-notes.sh 1.3.3 [linux-pending|linux-failed]
# The optional state adds a line about the Flatpak, which is attached after the Windows zip.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:?usage: release-notes.sh <version> [linux-pending|linux-failed]}"
LINUX="${2:-}"
REPO="${GITHUB_REPOSITORY:-raulblideran/paramaja}"

awk -v v="$VERSION" '
  /^## / { if (found) exit; if (index($0, "## " v " ") == 1) { found = 1; next } }
  found' "$ROOT/CHANGELOG.md" |
  # CHANGELOG.md wraps bullets onto indented lines; release pages keep line breaks,
  # so join each bullet back into one line.
  awk '/^  / { sub(/^ +/, " "); printf "%s", $0; next } { if (NR > 1) print ""; printf "%s", $0 } END { print "" }'

case "$LINUX" in
  linux-pending) printf '\n**Linux:** the Flatpak is still building and will be attached here in a few minutes.\n' ;;
  linux-failed) printf '\n**Linux:** the Flatpak build failed; it will be attached once fixed.\n' ;;
esac
printf '\nEarlier versions: [CHANGELOG.md](https://github.com/%s/blob/v%s/CHANGELOG.md)\n' "$REPO" "$VERSION"
