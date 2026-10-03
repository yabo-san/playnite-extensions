#!/usr/bin/env bash
# Packs the Release build into bin/Drop_<version>.pext. A .pext
# is a zip of the extension folder: the DLL, extension.yaml and the icon,
# without debug symbols.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/bin/Release/net462"
version="$(sed -n 's/^Version: *\([^ #]*\).*/\1/p' "$here/extension.yaml" | tr -d '\r')"
pext="$here/bin/Drop_${version}.pext"
test -f "$out/DropPlaynite.dll" || { echo "Build first: dotnet build -c Release" >&2; exit 1; }
rm -f "$pext"
py="$(command -v python3 || command -v python)"
(cd "$out" && "$py" -c 'import sys, zipfile; z = zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED); [z.write(f) for f in sys.argv[2:]]; z.close()' "$pext" DropPlaynite.dll extension.yaml icon.png)
echo "$pext"
