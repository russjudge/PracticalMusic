# Practical Music

Sheet music that follows the recording. Practical Music links PDF scores to MP3 recordings so that,
when you press play, the pages turn by themselves and the line being played is highlighted. Books can
be shared with other devices on the local network and played back in sync on all of them.

Author: **Russ Judge**. Built with the assistance of Claude (Anthropic).

Runs on **Windows**, **Linux** (tested target: Omarchy / Arch) and **Android**.

## Features

- Import PDF sheet music (several songs per PDF, several pages per song) and MP3/other audio files.
- Link recordings to songs; a song can have several recordings (vocals, piano, …).
- Drag and drop: drop a PDF anywhere in the Library to add it, drop an MP3 onto a page to link it to the
  song that starts on that page (a song is created there if needed).
- Timing editor: play the recording, press **"Page N starts now"** when the music reaches a new page, tap
  the page where the music is to add **markers**. Markers drive the highlight and tap-to-seek.
- Player: one page in portrait, two pages in landscape; automatic page turns; current segment highlighted;
  tap a spot on the page to jump there; previous/next song; play/pause/seek.
- Save as a self-contained `.pmbook` (PDF + audio embedded, easy to transfer) or a tiny links-only
  `.pmbook` (references to local files or http(s) URLs).
- Network: discover other devices, send a book to one device or **blast** it to all (recipients accept or
  reject), request a book from a device, and **lead/follow synchronized playback** across devices.
- About page: author credits, a quick-start guide, and a button that shows (and copies) the original
  prompt that was used to create the application (`src/PracticalMusic.App/Assets/original-prompt.txt`).

## Repository layout

```
src/PracticalMusic.Core      models, .pmbook packaging, timing engine, PDFium wrapper, networking (no UI)
src/PracticalMusic.App       shared Avalonia UI (views, view models, page canvas)
src/PracticalMusic.Desktop   Windows/Linux entry point, LibVLC audio
src/PracticalMusic.Android   Android entry point, MediaPlayer audio
tests/PracticalMusic.Core.Tests   xUnit tests (timing, packaging, PDF rendering, transfer, discovery/sync)
tools/TestDataGenerator      generates the app icons and a 10-page / 4-song test PDF (+ demo books)
packaging/                   build scripts: Windows (zip + Inno Setup), Linux (tar.gz + Omarchy PKGBUILD), Android (APK)
docs/                        installation guides
```

## Building from source

Prerequisites: .NET 10 SDK. For Android also `dotnet workload install android`, an Android SDK and JDK 17+.

```
dotnet build PracticalMusic.slnx                # everything except Android needs nothing else
dotnet test                                     # unit tests
dotnet run --project src/PracticalMusic.Desktop # run on Windows/Linux
```

Packaging (see the scripts for options):

| Target | Command | Output |
|---|---|---|
| Windows | `pwsh packaging/windows/build-windows.ps1` | `dist/PracticalMusic-<ver>-win-x64.zip` and, if Inno Setup is installed, `dist/PracticalMusic-<ver>-Setup.exe` |
| Linux (generic) | `./packaging/linux/build-linux.sh` | `dist/practical-music-<ver>-linux-x64.tar.gz` |
| Omarchy / Arch | `./packaging/linux/omarchy/build-omarchy.sh` | `dist/practical-music-<ver>-1-x86_64.pkg.tar.zst` |
| Android | `pwsh packaging/android/build-android.ps1` or `./packaging/android/build-android.sh` | `dist/android/PracticalMusic-<ver>.apk` |

Installation instructions: [docs/INSTALL-Windows.md](docs/INSTALL-Windows.md),
[docs/INSTALL-Linux.md](docs/INSTALL-Linux.md), [docs/INSTALL-Android.md](docs/INSTALL-Android.md).

## Using the app

The Library guides you through four steps; the step chips at the top of the Library show where you are.

1. **Add the PDF** – press *Add PDF…* (or drop a PDF file into the Library). Add more PDFs any time; the
   arrows reorder them.
2. **Identify the songs** – press *Add song*, enter the title and the first/last page of the song, and repeat
   for every song. Alternatively click the first page of a song in the page overview and choose *New song
   starts here*. Pages that belong to no song cannot be played. Select a song to *Rename* it, change its
   *Pages…* or *Remove* it.
3. **Add recordings** – select a song and press *Add MP3…*, or drop MP3 files straight onto the song in the
   song list (or onto one of its pages). *Add from URL…* accepts a direct link to an audio file
   (`https://…/song.mp3`); web pages such as Soundslice or YouTube cannot be used. A song can have several
   recordings; one plays at a time. If a linked file was moved, *Locate file…* points the recording at it again.
4. **Edit timing…** – with the song selected, press *Edit timing…* (enabled once the song has a recording).
   Play the recording; when the music reaches the top of the next page press *Page N starts now* (key **N**).
   Songs with more than one page show two pages side by side. Tap on the page at the spot the music is
   playing to drop a **marker** (switch *Tap adds marker* off to make taps seek instead). The highlight band
   runs from a marker to the next marker on the same line, or to the end of the line. Drag markers to adjust
   them; *Now* re-stamps a marker or page at the current time.
5. **Play** – pick a song (*Prev/Next song*, or *Play* in the Library). Landscape shows two pages, portrait
   one. Pages turn with the recording; tap a line to jump there. Keys: Space play/pause, ←/→ ±5 s,
   PageUp/PageDown turn pages, N/P next/previous song.
6. **Save** – *Save (with media)* writes a self-contained book; *Save (links only)* writes a small file that
   points at the PDF/MP3 locations (or URLs). Both use the `.pmbook` extension. On Windows, if *Controlled
   folder access* (ransomware protection) is enabled, allow Practical Music in Windows Security or save to a
   folder that is not protected (for example Downloads); the app tells you when a save was blocked.
7. **Share** – other devices running Practical Music on the same network appear automatically. *Send book*
   / *Blast book to all* offers the current book (with media); the receiving user accepts or rejects.
   *Request book* asks a device to send its book. A received book first lands safely in the app's
   `received` folder, then a save dialog asks where you want to keep it (it opens in the folder you last
   saved to, or Documents); that location becomes the book's file, so later saves go there without asking.
   Cancel the dialog and the book stays in the `received` folder. *Lead a session* makes this device the
   sync leader; on other devices press *Follow* next to the leader. When the leader presses play, all
   followers start the same song at the same instant, and pause/seek/song changes are mirrored.

## File format

A `.pmbook` is a ZIP archive containing `book.json` (songs, page ranges, recordings, page start times,
markers) and, for self-contained books, the media under `media/`. Links-only books keep absolute paths
or URLs in `book.json`; if a referenced file is missing, the app also looks next to the `.pmbook` file.

## Data locations

- Windows: `%APPDATA%\PracticalMusic` (settings, imported library, received books you did not save elsewhere), cache in `%LOCALAPPDATA%\PracticalMusic\cache`
- Linux: `~/.config/PracticalMusic`, cache in `~/.local/share/PracticalMusic/cache`
- Android: `Android/data/com.russjudge.practicalmusic/files` (received books are in `received/`)

## Networking notes

- Two inbound ports are needed on every device: **UDP 47312** (discovery and sync, broadcast) and
  **TCP 47313** (book transfers; the next free port is used if it is taken, and the *Share* page shows the
  actual one; `"TransferPort"` in `settings.json` changes it, `0` = any). Windows asks on the first run;
  Linux firewalls such as ufw (Omarchy's default, deny incoming) need two rules - `docs/INSTALL-Linux.md`
  and `docs/INSTALL-Windows.md` have the exact commands. All devices must be on the same subnet/Wi-Fi.
- Synchronization measures the clock offset between leader and followers with ping/pong round trips
  (broadcast on UDP 47312, so no extra port is needed) and schedules starts about 0.7 s in the future so
  every device begins together. Typical accuracy on Wi-Fi is within a few tens of milliseconds. Until the
  first reply arrives the follower uses the send time stamped on the leader's packets, so it never compares
  the two machines' raw clocks.

## Licensing of components

Avalonia UI (MIT), PDFium (BSD-3), LibVLC / LibVLCSharp (LGPL-2.1), CommunityToolkit.Mvvm (MIT),
SkiaSharp (MIT).
