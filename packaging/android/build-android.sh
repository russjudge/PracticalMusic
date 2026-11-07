#!/usr/bin/env bash
# Builds the Android APK of Practical Music on Linux/macOS.
#
#   ./packaging/android/build-android.sh
#
# Output: dist/android/PracticalMusic-<version>.apk (signed with the .NET debug keystore unless
# PM_KEYSTORE / PM_KEYSTORE_PASS / PM_KEY_ALIAS / PM_KEY_PASS are set).
# Requirements: .NET 10 SDK + android workload (dotnet workload install android), Android SDK
# (ANDROID_HOME), JDK 17+ (JAVA_HOME).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
CONFIG="${CONFIG:-Release}"
DIST="$ROOT/dist/android"
mkdir -p "$DIST"

ARGS=(publish "$ROOT/src/PracticalMusic.Android/PracticalMusic.Android.csproj" -c "$CONFIG" -f net10.0-android -p:AndroidPackageFormat=apk)
[ -n "${JAVA_HOME:-}" ] && ARGS+=("-p:JavaSdkDirectory=$JAVA_HOME")
[ -n "${ANDROID_HOME:-}" ] && ARGS+=("-p:AndroidSdkDirectory=$ANDROID_HOME")
if [ -n "${PM_KEYSTORE:-}" ]; then
  ARGS+=("-p:AndroidKeyStore=true" "-p:AndroidSigningKeyStore=$PM_KEYSTORE" "-p:AndroidSigningStorePass=$PM_KEYSTORE_PASS"
         "-p:AndroidSigningKeyAlias=$PM_KEY_ALIAS" "-p:AndroidSigningKeyPass=$PM_KEY_PASS")
fi
dotnet "${ARGS[@]}"

APK="$(find "$ROOT/src/PracticalMusic.Android/bin/$CONFIG/net10.0-android" -name '*-Signed.apk' -print0 | xargs -0 ls -t | head -1)"
cp "$APK" "$DIST/PracticalMusic-$VERSION.apk"
echo "APK: $DIST/PracticalMusic-$VERSION.apk"
echo "Install on a connected device with: adb install -r \"$DIST/PracticalMusic-$VERSION.apk\""
