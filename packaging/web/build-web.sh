#!/usr/bin/env bash
# Builds the Practical Music website for a Linux server.
#
#   ./packaging/web/build-web.sh               # linux-x64 (Debian 13 on a PC/VM)
#   ./packaging/web/build-web.sh linux-arm64   # e.g. a Raspberry Pi 4/5 with a 64-bit OS
#
# Output: dist/practicalmusic-web-<version>-<rid>.tar.gz
#   A self-contained build: the server does not need .NET installed. Copy the tarball to the server and follow
#   docs/INSTALL-Server-Debian.md (the folder also contains the SQL scripts, the systemd unit and the Apache site).
# Works on Linux and in Git Bash on Windows.
set -euo pipefail

RID="${1:-linux-x64}"
CONFIG="${CONFIG:-Release}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
DIST="$ROOT/dist"
STAGE="$DIST/web-$RID/practicalmusic-web"

echo "Practical Music website $VERSION - publishing $RID ($CONFIG)"
rm -rf "$STAGE"
mkdir -p "$STAGE/app"

dotnet publish "$ROOT/src/PracticalMusic.Web/PracticalMusic.Web.csproj" \
  -c "$CONFIG" -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none \
  -o "$STAGE/app"

# the development settings must not reach a server
rm -f "$STAGE/app/appsettings.Development.json"

mkdir -p "$STAGE/database" "$STAGE/server"
cp "$ROOT"/database/*.sql "$STAGE/database/"
cp "$ROOT/packaging/web/practicalmusic-web.service" "$ROOT/packaging/web/practicalmusic.russjudge.com.conf" \
   "$ROOT/packaging/web/appsettings.Production.example.json" "$STAGE/server/"
cp "$ROOT/docs/INSTALL-Server-Debian.md" "$ROOT/docs/EMAIL-SETUP.md" "$ROOT/docs/WEBSITE.md" "$STAGE/"
chmod +x "$STAGE/app/PracticalMusic.Web"

TARBALL="$DIST/practicalmusic-web-$VERSION-$RID.tar.gz"
tar -C "$DIST/web-$RID" -czf "$TARBALL" practicalmusic-web
echo "Done: $TARBALL"
