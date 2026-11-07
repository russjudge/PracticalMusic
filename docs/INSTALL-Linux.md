# Installing Practical Music on Linux

Tested target: **Omarchy** (Arch Linux, Hyprland). The generic tarball works on any x86_64 (or arm64)
distribution with glibc.

Runtime requirements:

- **vlc** (provides `libvlc`, used for audio playback): `sudo pacman -S vlc` on Arch/Omarchy,
  `sudo apt install vlc` on Debian/Ubuntu, `sudo dnf install vlc` on Fedora.
- If `pacman` fails with a 404 error, the package database is stale - refresh it first with
  `sudo pacman -Syu`, then retry the install.
- An X11 server or **XWayland** (Avalonia draws through X11). Omarchy's Hyprland setup ships with
  `xorg-xwayland`; if it is missing: `sudo pacman -S xorg-xwayland`.
- fontconfig and the usual X11 client libraries (present on every desktop install).

## Option A – Omarchy / Arch package (recommended on Omarchy)

Build and install the package from the source tree:

```
sudo pacman -S --needed base-devel dotnet-sdk vlc
./packaging/linux/omarchy/build-omarchy.sh --install
```

(`build-omarchy.sh` runs `makepkg`; without `--install` the package is written to `dist/` and can be
installed with `sudo pacman -U dist/practical-music-*.pkg.tar.zst`.)

This installs to `/opt/practical-music`, adds `practical-music` to the PATH, a launcher in the app menu
and the `.pmbook` MIME type. Remove with `sudo pacman -R practical-music`.

## Option B – Tarball (any distribution)

1. Extract: `tar -xzf practical-music-<version>-linux-x64.tar.gz`
2. Either run it in place: `./practical-music/practical-music`
   or install for the current user (no root): `./practical-music/install-user.sh`
   (copies to `~/.local/opt/practical-music`, adds a launcher to the app menu and `~/.local/bin`).
3. Make sure `vlc` is installed (see above).

If starting the app fails with "Permission denied", the tarball lost the execute permission on
its binaries (this can happen when the tarball was built on Windows). Restore it with
`chmod +x practical-music/practical-music practical-music/app/PracticalMusic`, or install with
`bash practical-music/install-user.sh` - the installer also restores the execute permission on
the installed copy automatically.

To open a book from the terminal: `practical-music ~/Music/songs.pmbook`.

## Omarchy / Hyprland notes

- The window opens as an XWayland client. If the app does not appear, check that XWayland is enabled in
  Hyprland (`xwayland { enabled = true }`, the default).
- For a tidy fullscreen performance view use Hyprland's fullscreen keybinding (default `SUPER+F` on
  Omarchy) once the *Play* page is showing.
- Audio goes through PipeWire/PulseAudio via libvlc; no extra configuration is needed.
- Firewall: Omarchy runs **ufw** with *deny incoming* by default - see the next section, nothing works
  across machines until the two ports are opened.

## Firewall

Practical Music needs exactly two inbound ports from your LAN, and nothing else:

| Purpose | Port |
|---|---|
| Discovery and synchronized playback (UDP broadcast, incl. the clock ping/pong) | **UDP 47312** |
| Book transfers (*Send book*, *Request book*) | **TCP 47313** |

With a firewall that denies incoming traffic (ufw on Omarchy) the symptoms are: this machine sees others
but is not seen by them, *Request book* / *Send book* fail with "The operation was canceled", and a follower
cannot measure the leader's clock. Open the two ports for your LAN only (replace `192.168.1.0/24` with your
subnet - `ip -4 addr` shows it; `install-user.sh` prints these commands with the subnet filled in when it
detects an active ufw):

```
sudo ufw allow in from 192.168.1.0/24 to any port 47312 proto udp comment 'Practical Music discovery'
sudo ufw allow in from 192.168.1.0/24 to any port 47313 proto tcp comment 'Practical Music transfer'
```

Check with `sudo ufw status numbered`. For firewalld: `sudo firewall-cmd --permanent --add-port=47312/udp
--add-port=47313/tcp && sudo firewall-cmd --reload`.

The same two ports must be open on the other devices. On a Windows PC run in an elevated PowerShell (these
rules are independent of where the program is installed, unlike the per-program rule Windows offers on
first start):

```
New-NetFirewallRule -DisplayName 'Practical Music discovery' -Direction Inbound -Protocol UDP -LocalPort 47312 -RemoteAddress LocalSubnet -Profile Private,Domain -Action Allow
New-NetFirewallRule -DisplayName 'Practical Music transfer'  -Direction Inbound -Protocol TCP -LocalPort 47313 -RemoteAddress LocalSubnet -Profile Private,Domain -Action Allow
```

If TCP 47313 is already taken on a machine, the app uses the next free port (47314, 47315, …) and shows the
actual port on the *Share* page; open that one instead. The port can be changed in `settings.json`
(`"TransferPort": 47313`; `0` lets the system choose). Discovery always uses UDP 47312.

## Building the packages yourself

Requires the .NET 10 SDK (`sudo pacman -S dotnet-sdk` on Arch):

```
./packaging/linux/build-linux.sh             # generic tar.gz in dist/
./packaging/linux/omarchy/build-omarchy.sh   # Arch package in dist/
```

Cross-building the Linux tarball from Windows also works (`bash packaging/linux/build-linux.sh` in Git
Bash); only the Arch package has to be built on Arch because it uses `makepkg`.

## Where files live

- Settings, imported library, received books: `~/.config/PracticalMusic`
- Cache: `~/.local/share/PracticalMusic/cache` (safe to delete)
- Crash log: `~/.config/PracticalMusic/crash.log`
