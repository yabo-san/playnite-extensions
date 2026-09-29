#!/usr/bin/env bash
# Packs a theme folder into <theme>/bin/<Id>_<version>.pthm. A .pthm is a zip of
# the theme folder with theme.yaml at the root, which is what Playnite's Toolbox
# produces; doing it here keeps CI off Windows and off a Playnite download.
# Usage: scripts/pack-theme.sh YaboTheme
set -euo pipefail
theme="${1:?theme folder}"
root="$(cd "$(dirname "$0")/.." && pwd)"
dir="$root/$theme"
test -f "$dir/theme.yaml" || { echo "no theme.yaml in $theme" >&2; exit 1; }
id="$(sed -n 's/^Id: *\([^ #]*\).*/\1/p' "$dir/theme.yaml" | tr -d '\r')"
version="$(sed -n 's/^Version: *\([^ #]*\).*/\1/p' "$dir/theme.yaml" | tr -d '\r')"
out="$dir/bin/${id}_${version}.pthm"
mkdir -p "$dir/bin"
rm -f "$out"
(cd "$dir" && python3 - "$out" <<'EOF'
import os, sys, zipfile
out = sys.argv[1]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for base, dirs, files in os.walk("."):
        dirs[:] = [d for d in dirs if d not in ("bin", ".git")]
        for f in files:
            p = os.path.join(base, f)
            z.write(p, os.path.relpath(p, "."))
EOF
)
echo "$out"
