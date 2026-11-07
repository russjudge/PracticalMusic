# Installing Practical Music on Android

Supported: Android 7.0 (API 24) and newer, arm64 and x86_64 devices (phones and tablets). A tablet in
landscape shows two pages side by side; portrait shows one page.

The app is distributed as an APK (`PracticalMusic-<version>.apk`) and sideloaded; it is not in the Play
Store.

## Install on the device

1. Copy the APK to the device (USB, cloud drive, e-mail, or a web link) and tap it in the file manager,
   **or** from a computer with the Android platform tools: `adb install -r PracticalMusic-<version>.apk`.
2. Android asks to allow installs from this source (*Settings → Apps → Special access → Install unknown
   apps*). Allow it for the file manager / browser you used.
3. If Play Protect warns that the app is from an unknown developer, choose *Install anyway*. The APK is
   signed with a personal key, not a Play Store key.
4. Open **Practical Music** from the launcher.

Updating: install the newer APK over the old one (same signing key). Data and settings are kept.

## Using the app on Android

- *Add PDF* / *Add MP3* open the system file picker; picked files are **copied** into the app's private
  storage so the book keeps working even if the original is moved or deleted.
- *Open…* / *Save…* use the system document picker, so books can live in Downloads, Drive, etc.
- Received books land in `Android/data/com.russjudge.practicalmusic/files/received/` (visible in a file
  manager on most devices).
- Keep the device on the same Wi-Fi as the other devices for sharing and synchronized playback. If other
  devices are not found, check that the Wi-Fi network does not have *AP/client isolation* enabled (common
  on guest networks).
- The screen stays on while a recording is playing.
- The back gesture/button goes back from the timing editor to the Library, closes dialogs, and otherwise
  returns to the Library page.

## Building the APK yourself

Requirements: .NET 10 SDK, the Android workload (`dotnet workload install android`), an Android SDK
(platform 35 or newer, build-tools) and a JDK 17+ (JDK 21 recommended).

```
pwsh packaging/android/build-android.ps1        # Windows
./packaging/android/build-android.sh            # Linux / macOS
```

The script auto-detects the JDK and SDK on Windows; otherwise set `JAVA_HOME` and `ANDROID_HOME`.
The APK is written to `dist/android/PracticalMusic-<version>.apk`, signed with the .NET debug key.
To sign with your own key, set `PM_KEYSTORE`, `PM_KEYSTORE_PASS`, `PM_KEY_ALIAS` and `PM_KEY_PASS`
before running the script (create a key with `keytool -genkeypair -v -keystore release.keystore
-alias practicalmusic -keyalg RSA -keysize 2048 -validity 10000`).

## Known limitations

- Drag-and-drop from other apps is not available on Android; use the *Attach MP3 here…* button on a page
  (after selecting it) or *Add MP3 to song*.
- The bundled SkiaSharp native library is not yet built for 16 KB memory pages; on future Android
  versions that enforce 16 KB pages a newer Avalonia/SkiaSharp release will be needed.
