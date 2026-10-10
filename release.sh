#!/usr/bin/env bash
# FreeMon release builder (Linux).
# Makes release/<version>/assets/:
#   freemon-<v>-linux-x64-portable.tar.gz, freemon-<v>-win-x64-portable.zip,
#   freemon_<v>-1_amd64.deb, freemon-<v>-1.x86_64.rpm, freemon-<v>-win-x64-setup.exe, SHA256SUMS
# and release/<version>/source/ — the project files to upload to GitHub.
# Usage: ./release.sh [https://github.com/<user>/<repo>]
# Nothing is deleted: if release/<version> exists the script stops.
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
ROOT="$PWD"

# .NET SDK: from PATH, or from an env file next to the projects folder (e.g. /appdev/env.sh or ~/devbox/env.sh)
if ! command -v dotnet >/dev/null 2>&1; then
  for envf in "$ROOT/../../env.sh" "$ROOT/../env.sh" "$HOME/devbox/env.sh"; do
    [ -f "$envf" ] && { . "$envf"; break; }
  done
fi
command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found"; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

VER="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' FreeMon.csproj | head -n1)"
[ -n "$VER" ] || { echo "Version not found in FreeMon.csproj"; exit 1; }
HOMEPAGE="${1:-https://github.com/}"
OUT="$ROOT/release/$VER"
if [ -e "$OUT" ]; then
  echo "$OUT already exists. Raise <Version> in FreeMon.csproj or move that folder away."
  exit 1
fi
B="$OUT/build"; A="$OUT/assets"
mkdir -p "$B" "$A"
echo "=== FreeMon $VER -> $OUT ==="

COMMON=(-c Release --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:EnableCompressionInSingleFile=true)
echo "--- publish linux-x64"
dotnet publish FreeMon.csproj -r linux-x64 "${COMMON[@]}" -p:IncludeNativeLibrariesForSelfExtract=false -o "$B/linux-x64"
echo "--- publish win-x64"
dotnet publish FreeMon.csproj -r win-x64 "${COMMON[@]}" -p:IncludeNativeLibrariesForSelfExtract=true -o "$B/win-x64"

DOCS=(LICENSE.txt THIRD_PARTY_NOTICES.txt README.md README.uk.md docs/MANUAL.md docs/MANUAL.uk.md)

echo "--- portable Linux archive"
PL="freemon-$VER-linux-x64"
mkdir -p "$B/$PL"
cp -a "$B/linux-x64/." "$B/$PL/"
cp "${DOCS[@]}" "$B/$PL/"
: > "$B/$PL/portable.flag"
tar -C "$B" -czf "$A/$PL-portable.tar.gz" "$PL"

echo "--- portable Windows archive"
PW="freemon-$VER-win-x64"
mkdir -p "$B/$PW"
cp "$B/win-x64/freemon.exe" "$B/$PW/"
cp "${DOCS[@]}" assets/freemon.ico "$B/$PW/"
: > "$B/$PW/portable.flag"
(cd "$B" && python3 -m zipfile -c "$A/$PW-portable.zip" "$PW")

echo "--- .deb / .rpm (nFPM)"
NFPM_VER=2.47.0
NFPM_SHA=0660ca602b2d2d2ae4781a06c692b3eeb9d437ffea05b831d76e41f4a3188783
NFPM_DIR="$ROOT/.tools/nfpm-$NFPM_VER"   # downloaded once, ignored by git
NFPM="$NFPM_DIR/nfpm"
if [ ! -x "$NFPM" ]; then
  mkdir -p "$NFPM_DIR"
  TGZ="$NFPM_DIR/nfpm.tar.gz"
  curl -fL --progress-bar -o "$TGZ" "https://github.com/goreleaser/nfpm/releases/download/v$NFPM_VER/nfpm_${NFPM_VER}_Linux_x86_64.tar.gz"
  echo "$NFPM_SHA  $TGZ" | sha256sum -c -
  tar -C "$NFPM_DIR" -xzf "$TGZ" nfpm
fi
sed -e "s|@VERSION@|$VER|" -e "s|@HOMEPAGE@|$HOMEPAGE|" -e "s|@BUILD@|$B/linux-x64|" -e "s|@ROOT@|$ROOT|g" \
  packaging/nfpm.yaml.in > "$B/nfpm.yaml"
"$NFPM" package -f "$B/nfpm.yaml" -p deb -t "$A/"
"$NFPM" package -f "$B/nfpm.yaml" -p rpm -t "$A/"

echo "--- Windows installer (NSIS)"
SETUP="freemon-$VER-win-x64-setup.exe"
NSIS_ARGS=(-V2 -DVERSION="$VER")
if command -v makensis >/dev/null 2>&1; then
  makensis "${NSIS_ARGS[@]}" -DSRCDIR="$B/win-x64" -DROOT="$ROOT" -DOUTFILE="$A/$SETUP" packaging/freemon.nsi
else
  CT=""
  command -v podman >/dev/null 2>&1 && CT=podman
  [ -z "$CT" ] && command -v docker >/dev/null 2>&1 && CT=docker
  if [ -n "$CT" ]; then
    echo "makensis not found — using $CT (debian:stable-slim + nsis)"
    "$CT" run --rm -v "$ROOT:/w:Z" -w /w docker.io/library/debian:stable-slim sh -c \
      "apt-get update -qq >/dev/null && apt-get install -y -qq nsis >/dev/null && \
       makensis ${NSIS_ARGS[*]} -DSRCDIR=/w/release/$VER/build/win-x64 -DROOT=/w -DOUTFILE=/w/release/$VER/assets/$SETUP packaging/freemon.nsi"
  else
    echo "!!! makensis, podman and docker not found — the Windows installer is skipped (portable zip is ready)."
  fi
fi

echo "--- SHA256SUMS"
(cd "$A" && sha256sum -- * > SHA256SUMS)

echo "--- source files for GitHub"
SRC="$OUT/source"
mkdir -p "$SRC"
cp -a FreeMon.csproj build.sh build.cmd release.sh .gitignore "${DOCS[@]}" sample-devices.csv src lang assets packaging docs "$SRC/"

cat > "$OUT/RELEASE_NOTES.md" <<NOTES
## FreeMon $VER

Network device availability monitoring for Windows and Linux.

### Downloads
| File | For |
| --- | --- |
| \`freemon-$VER-win-x64-setup.exe\` | Windows 10 / 11 x64 — installer |
| \`freemon-$VER-win-x64-portable.zip\` | Windows — portable (unpack, run \`freemon.exe\`; data in \`data\` next to it) |
| \`freemon_$VER-1_amd64.deb\` | Debian, Ubuntu and derivatives |
| \`freemon-$VER-1.x86_64.rpm\` | RHEL-compatible, Fedora, openSUSE / SLES |
| \`freemon-$VER-linux-x64-portable.tar.gz\` | any x64 Linux — portable (unpack, run \`./freemon\`) |
| \`SHA256SUMS\` | checksums: \`sha256sum -c SHA256SUMS\` |

The Windows binaries are not code-signed: Windows SmartScreen may show a warning ("More info → Run anyway").

### Changes
- (describe the changes of this release)
NOTES

echo
echo "Done:"
ls -lh "$A"
echo "Release notes: $OUT/RELEASE_NOTES.md"
echo "Source for GitHub: $SRC"
