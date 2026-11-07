# Installing Practical Music on Windows

Supported: Windows 10 / 11, 64-bit. Nothing else needs to be installed first: the build is
self-contained (.NET runtime, PDF renderer and the LibVLC audio engine are included).

## Option A – Installer (recommended)

1. Run `PracticalMusic-<version>-Setup.exe`.
2. Follow the wizard. It installs to *Program Files\Practical Music*, adds a Start-menu entry and
   associates `.pmbook` files with the app.
3. Start **Practical Music** from the Start menu.

Uninstall from *Settings → Apps* like any other program.

## Option B – Portable zip

1. Unzip `PracticalMusic-<version>-win-x64.zip` anywhere (for example `C:\Apps\PracticalMusic`).
2. Double-click `PracticalMusic.exe`.
3. Optional: right-click → *Send to → Desktop* for a shortcut. You can also open a book directly:
   `PracticalMusic.exe "C:\path\to\book.pmbook"`.

## First run

- **Windows SmartScreen** may warn because the program is not code-signed. Choose *More info → Run anyway*.
- **Windows Firewall** asks whether to allow the app on private networks. Allow it, otherwise other devices
  on the network cannot be discovered and books cannot be sent or received. The app needs two inbound
  ports: **UDP 47312** (discovery and synchronized playback) and **TCP 47313** (book transfers). The prompt
  creates a rule for that copy of `PracticalMusic.exe` only and re-appears when the program moves; to allow
  the ports regardless of where the program lives, run in an elevated PowerShell:

  ```
  New-NetFirewallRule -DisplayName 'Practical Music discovery' -Direction Inbound -Protocol UDP -LocalPort 47312 -RemoteAddress LocalSubnet -Profile Private,Domain -Action Allow
  New-NetFirewallRule -DisplayName 'Practical Music transfer'  -Direction Inbound -Protocol TCP -LocalPort 47313 -RemoteAddress LocalSubnet -Profile Private,Domain -Action Allow
  ```

  If 47313 is taken, the app uses the next free port and shows it on the *Share* page (configurable as
  `"TransferPort"` in `%APPDATA%\PracticalMusic\settings.json`). Linux devices need the same two ports open
  (see INSTALL-Linux.md for the ufw commands).

## Building the packages yourself

Requires the .NET 10 SDK (and optionally Inno Setup 6 for the installer):

```
pwsh packaging\windows\build-windows.ps1
```

Outputs go to `dist\`.

## Where files live

- Settings, imported library and received books: `%APPDATA%\PracticalMusic`
- Cache (extracted media of opened books, downloads): `%LOCALAPPDATA%\PracticalMusic\cache` (safe to delete)
- A crash, if one ever happens, is logged to `%APPDATA%\PracticalMusic\crash.log`
