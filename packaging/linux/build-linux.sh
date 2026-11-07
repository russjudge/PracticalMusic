#!/usr/bin/env bash
# Builds the Linux distribution of Practical Music.
#
#   ./packaging/linux/build-linux.sh            # linux-x64 tarball in dist/
#   ./packaging/linux/build-linux.sh linux-arm64
#
# Output: dist/practical-music-<version>-<rid>.tar.gz
#   Contains a self-contained build (no .NET runtime needed), a launcher script, a .desktop
#   file and the icon. Runtime dependency: libvlc (package "vlc") for audio playback.
# For an Arch/Omarchy package see packaging/linux/omarchy/PKGBUILD (run build-omarchy.sh).
set -euo pipefail

RID="${1:-linux-x64}"
CONFIG="${CONFIG:-Release}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
DIST="$ROOT/dist"
STAGE="$DIST/$RID/practical-music"

echo "Practical Music $VERSION - publishing $RID ($CONFIG)"
rm -rf "$STAGE"
mkdir -p "$STAGE/app"

dotnet publish "$ROOT/src/PracticalMusic.Desktop/PracticalMusic.Desktop.csproj" \
  -c "$CONFIG" -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none \
  -o "$STAGE/app"

cp "$ROOT/packaging/linux/practical-music.desktop" "$STAGE/"
cp "$ROOT/packaging/app-icon-512.png" "$STAGE/practical-music.png"
cp "$ROOT/README.md" "$ROOT/docs/INSTALL-Linux.md" "$STAGE/"
[ -f "$ROOT/LICENSE" ] && cp "$ROOT/LICENSE" "$STAGE/" || true

cat > "$STAGE/practical-music" <<'EOF'
#!/usr/bin/env bash
# Launcher: runs the self-contained build from the folder this script lives in.
DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
exec "$DIR/app/PracticalMusic" "$@"
EOF
chmod +x "$STAGE/practical-music" "$STAGE/app/PracticalMusic"

cat > "$STAGE/install-user.sh" <<'EOF'
#!/usr/bin/env bash
# Installs Practical Music for the current user (no root needed): ~/.local/opt + menu entry.
set -e
SRC="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
TARGET="$HOME/.local/opt/practical-music"
mkdir -p "$TARGET" "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/512x512/apps"
rm -rf "$TARGET/app"
cp -r "$SRC/app" "$TARGET/app"
cp "$SRC/practical-music" "$TARGET/practical-music"
chmod +x "$TARGET/practical-music" "$TARGET/app/PracticalMusic"
chmod +x "$TARGET/app/createdump" 2>/dev/null || true
ln -sf "$TARGET/practical-music" "$HOME/.local/bin/practical-music"
cp "$SRC/practical-music.png" "$HOME/.local/share/icons/hicolor/512x512/apps/practical-music.png"
sed "s|^Exec=.*|Exec=$TARGET/practical-music %f|" "$SRC/practical-music.desktop" > "$HOME/.local/share/applications/practical-music.desktop"
update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
echo "Installed to $TARGET. Make sure 'vlc' (libvlc) is installed for audio playback."

# Firewall: the app needs two inbound ports from the LAN (UDP 47312 discovery/sync, TCP 47313 transfers).
# With ufw active (Omarchy: default deny incoming) nothing works across machines until they are opened.
# Only print the commands - never run sudo on the user's behalf.
ufw_active=0
if command -v ufw >/dev/null 2>&1; then
  if systemctl is-active --quiet ufw 2>/dev/null || grep -qs '^ENABLED=yes' /etc/ufw/ufw.conf; then ufw_active=1; fi
fi
if [ "$ufw_active" = 1 ]; then
  # Detect this machine's LAN subnet (network address/prefix); fall back to a common one.
  subnet="192.168.1.0/24"
  cidr="$(ip -4 -o addr show scope global 2>/dev/null | awk '{print $4; exit}')"
  if [ -n "$cidr" ]; then
    ip4="${cidr%/*}"; bits="${cidr#*/}"
    IFS=. read -r a b c d <<<"$ip4"
    if [ "$bits" -ge 1 ] && [ "$bits" -le 32 ] 2>/dev/null; then
      n=$(( ((a<<24) | (b<<16) | (c<<8) | d) & ((0xFFFFFFFF << (32-bits)) & 0xFFFFFFFF) ))
      subnet="$(( (n>>24)&255 )).$(( (n>>16)&255 )).$(( (n>>8)&255 )).$(( n&255 ))/$bits"
    fi
  fi
  cat <<MSG

The ufw firewall is active on this machine. Practical Music needs two inbound ports from your LAN,
otherwise other devices cannot find this one and sending/requesting books fails. Run:

  sudo ufw allow in from $subnet to any port 47312 proto udp comment 'Practical Music discovery'
  sudo ufw allow in from $subnet to any port 47313 proto tcp comment 'Practical Music transfer'

($subnet was detected from this machine's address - adjust it if your LAN uses a different range.)
MSG
fi
EOF
chmod +x "$STAGE/install-user.sh"

mkdir -p "$DIST"
TARBALL="$DIST/practical-music-$VERSION-$RID.tar.gz"
rm -f "$TARBALL"
tar -C "$DIST/$RID" -czf "$TARBALL" practical-music
echo "Tarball: $TARBALL"
