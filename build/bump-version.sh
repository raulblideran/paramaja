#!/usr/bin/env bash
# Sets the app version everywhere it lives, so builds shared with friends are identifiable:
#   ./build/bump-version.sh 1.2.0 "Short summary of the release"
# Updates the project file (exe version, zip name, in-app About), adds a CHANGELOG.md
# section and a metainfo release entry (the Flatpak's version). Fill in the changelog after.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:?usage: bump-version.sh <major.minor.patch> [summary]}"
SUMMARY="${2:-}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must look like 1.2.3" >&2; exit 1; }
DATE="$(date +%F)"
CSPROJ="$ROOT/src/Paramaja.App/Paramaja.App.csproj"
META="$ROOT/packaging/io.github.raulblideran.Paramaja.metainfo.xml"

CURRENT=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$CSPROJ")
if [[ "$CURRENT" == "$VERSION" ]]; then echo "already at $VERSION"; exit 0; fi

sed -i "s|<Version>$CURRENT</Version>|<Version>$VERSION</Version>|" "$CSPROJ"

python3 - "$ROOT/CHANGELOG.md" "$META" "$VERSION" "$DATE" "$SUMMARY" <<'PY'
import sys, html
changelog, meta, version, date, summary = sys.argv[1:]
item = summary or "Describe this release"
s = open(changelog, encoding="utf-8").read()
i = s.index("\n## ")
s = s[:i] + f"\n## {version} — {date}\n\n- {item}\n" + s[i:]
open(changelog, "w", encoding="utf-8").write(s)
m = open(meta, encoding="utf-8").read()
m = m.replace("  <releases>\n", f'  <releases>\n    <release version="{version}" date="{date}">\n'
              f'      <description>\n        <p>{html.escape(item)}</p>\n      </description>\n    </release>\n', 1)
open(meta, "w", encoding="utf-8").write(m)
PY
echo "Version $CURRENT -> $VERSION. Edit CHANGELOG.md to list the changes, then ./build/publish-win.sh"
