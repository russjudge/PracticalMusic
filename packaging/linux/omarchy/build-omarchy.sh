#!/usr/bin/env bash
# Builds and (optionally) installs the Arch/Omarchy package.
#
#   ./packaging/linux/omarchy/build-omarchy.sh          # builds practical-music-<ver>-1-x86_64.pkg.tar.zst into dist/
#   ./packaging/linux/omarchy/build-omarchy.sh --install
#
# Needs: base-devel (makepkg), dotnet-sdk. Install them on Omarchy with:
#   sudo pacman -S --needed base-devel dotnet-sdk
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
mkdir -p "$ROOT/dist"
cd "$HERE"
rm -rf pkg src
PKGDEST="$ROOT/dist" makepkg -f --noconfirm "$@"
rm -rf pkg src
echo "Package written to $ROOT/dist (install with: sudo pacman -U dist/practical-music-*.pkg.tar.zst)"
