# The Practical Music website

The website brings Practical Music to every device with a browser - in particular **iPhones and iPads**, without an
App Store - and connects the Windows, Linux and Android apps to a shared, organization-wide library.

- Installation: [INSTALL-Server-Debian.md](INSTALL-Server-Debian.md) · E-mail: [EMAIL-SETUP.md](EMAIL-SETUP.md)
- Source: `src/PracticalMusic.Web` (ASP.NET Core, .NET 10), database scripts in `database/`

## What it does

| In the apps | On the website |
|---|---|
| Library: add PDFs, define songs (page ranges), add recordings (files or web links), reorder/remove | the same, in the browser (files are uploaded to the organization) |
| Edit timing: "Page N starts now", tap to add markers, drag markers, page-time and marker lists, highlight height | the same |
| Player: one/two pages, automatic page turns, highlight, tap to seek, full display, keyboard keys | the same; full display uses the whole screen (Add to Home Screen on iOS for no browser bars) |
| Save as .pmbook | **Save** stores a new revision on the website; **⬇ .pmbook** downloads it for the apps |
| Share on the LAN: send/request books, lead/follow sync | the organization's library replaces sending files; lead/follow sync **through the website** with browsers *and* apps |

Pages render on the server with PDFium (the PDF engine of the apps), so every browser gets identical page images
without downloading whole PDFs. Recordings stream with range requests (what iOS Safari requires).

## People, organizations and permissions

- **Website administrator** - may do everything: set up organizations (*Admin → Organizations*), manage every
  organization and user, see the e-mail outbox and the activity log. Created with `PracticalMusic.Web create-admin`.
- **Organization** - a church, band, choir … with its own books. Only the administrator creates organizations.
- **Manager** (per organization) - maintains the organization's books and files (create, edit, upload, delete) and its
  people: approves or declines join requests, adds people, changes roles, edits members' contact details, removes them.
  An organization always keeps at least one manager.
  - *Adding a new address* creates the account and e-mails an invitation link to choose a password; access starts at once.
  - *Adding someone who already has an account* sends them an invitation that they accept (or decline) under
    *Account → My organizations*: a manager cannot attach another person's account without their consent, and until
    they accept, the manager sees only the e-mail address they typed. (When the website administrator adds someone,
    it takes effect right away.)
- **Member** (per organization) - reads and plays the books (read only), in the browser or in the apps.

One person may belong to several organizations, with a different role in each. Books are only visible to members of
their organization; every request is checked against it.

**Signing up:** *Create an account* asks for the organization (drop-down of the organizations that accept requests),
name, e-mail address, phone and mailing address (optional), password, and an optional message to the managers. The
person confirms the address via e-mail; then the managers are notified. When a manager approves, the person gets an
e-mail and has access. People who already have an account ask to join further organizations under *Account → My
organizations*. *Forgot password* sends a one-time link (valid 60 minutes).

## The apps and the website

The Windows, Linux and Android apps work on their own exactly as before. Their new **🌐 Online** page:

- **Sign in** with the website account (e-mail address and password). The app keeps a revocable sign-in token, never the
  password; *Account → Apps & devices* on the website lists and signs out devices.
- **Books of the organization** - *Download & open*, *Update & open* when a newer revision exists, *Download all
  updates*. Downloaded books are ordinary .pmbook files; they remember where they came from (an `origin` entry in
  book.json: website, organization, book, revision, fingerprint), so the app can tell "up to date", "newer revision on
  the website" and "changed on this device" - also after the book was saved elsewhere or sent to another device.
- **Upload this book** (managers) - stores the open book with its PDFs and recordings as a new revision. If somebody
  changed the book on the website in the meantime, the app says so and asks before overwriting; a book that already
  exists can be replaced or uploaded as a separate copy.
- **Play together through the website** - see below.

## Synchronized playback

Browsers cannot take part in the apps' local-network protocol (UDP broadcast, raw TCP), so the website runs the
session: everyone signed in to the same organization - browsers and apps - is connected to the server over a
WebSocket (`/ws/sync`, inside the normal HTTPS connection). **Anyone can lead**, including a browser; the others follow.

- Each device measures its clock offset to the server with ping/pong round trips (the shortest round trip wins - the
  same method the apps use on the LAN). The leader stamps its play/pause/seek/song changes with server time and
  schedules starts 0.7 s ahead; followers convert to their own clock and start at the same instant. Small drifts are
  corrected by playing 3 % faster or slower for a moment (browsers), larger ones with a seek.
- **An app that leads, leads on its local network and through the website at the same time.** So a band with
  Windows/Android apps on the Wi-Fi and a singer with an iPad in Safari all follow one leader.
- Followers open the leader's book automatically when it is in the organization's library.
- **Firewalls:** nothing to open on any device - only outgoing HTTPS. On the server only 80/443 (see the install guide).
  The apps' LAN features keep needing UDP 47312 / TCP 47313 on the local network, as before.
- iPhone/iPad: browsers only start audio after a tap. Tapping *Follow* (or the page) grants that; if the browser still
  refuses, a "Tap to allow playback" button appears.

## iPhone and iPad

Open the website in Safari, sign in, then *Share → Add to Home Screen*. Practical Music then starts full screen from
the home screen like an app (it is a web app; nothing goes through the App Store). The screen stays on while music
plays (iOS 16.4 or newer).

## Security in short

- HTTPS only (Apache terminates TLS; HSTS); cookies are HttpOnly, SameSite and Secure.
- Passwords: PBKDF2-HMAC-SHA512 (100,000 iterations, per-user salt); minimum 8 characters, common passwords refused.
- 10 wrong passwords lock an account for 15 minutes; sign-in, sign-up and reset requests are rate-limited per address
  (an IPv6 /64 counts as one address); *Forgot password*, sign-up, sign-in and adding members never reveal whether an
  address is registered - a wrong password, an unknown address and a locked account get the same answer.
- Request sizes: forms and sign-in accept at most 256 KB, book saves 24 MB; only the two upload endpoints accept
  `Site:MaxUploadMegabytes`, and only after checking that the user manages the organization.
- Synchronized playback: at most 8 connections per person, 300 per organization and 3000 in total, 40 messages per
  second per connection; a device that stops reading is disconnected; access is checked again every 30 seconds (a
  removed member, a disabled account, a changed password or a signed-out app drops out of the session).
- E-mail links and app tokens are random 256-bit values; only their SHA-256 hashes are stored. Changing or resetting a
  password signs out all other browsers (reset also signs out the apps).
- Changes from browsers need the anti-forgery token; the sync socket rejects other websites (Origin check).
- Strict Content-Security-Policy (no inline scripts), user text is always encoded, all SQL is parameterized.
- Uploads are checked by content (PDF must open in PDFium; audio must look like audio), stored by SHA-256 outside the
  web root, and served only to members of the owning organization. Uploaded .pmbook files are read defensively
  (size limits, no paths taken from the archive). The apps now also refuse package entries that would escape their
  cache folder.
- The database account can only read/write rows; the website runs as an unprivileged system user under a hardened
  systemd unit.

## Developing and testing

```
dotnet run --project src/PracticalMusic.Web        # http://localhost:5080, demo mode (in memory, sample accounts)
dotnet test tests/PracticalMusic.Web.Tests         # website: pages, API, e-mail flows, sync hub, the apps' client code
dotnet test tests/PracticalMusic.Core.Tests        # shared code
```

Demo accounts (password `demo-password-123`): `admin@example.com`, `manager@example.com`, `member@example.com`; set
`Demo:BookPath` in `appsettings.Development.json` to a .pmbook to have a sample book. Messages land in
`src/PracticalMusic.Web/data-dev/mail-pickup`.

The website tests run against an in-memory store. To run the same tests against MariaDB, create an empty test database
with `database/02-schema.sql` (change `USE practicalmusic` to the test database's name) and a user with rights on it,
then:

```
PS> $env:PM_TEST_MARIADB = "Server=localhost;Database=practicalmusic_test;User ID=pmtest;Password=…"
PS> dotnet test tests/PracticalMusic.Web.Tests
```

The browser code has its own tests (timing, sync math and model - with the same expectations as the C# tests - plus a
load check of every module), runnable without build tools:

```
chrome --headless=new --allow-file-access-from-files --virtual-time-budget=8000 --dump-dom tests/web-js/run-tests.html
```

### Layout

```
src/PracticalMusic.Web/
  Program.cs              configuration, services, security headers, pipeline
  Cli.cs                  create-admin, check, send-test-email, password-link
  Api/ApiEndpoints.cs     JSON API for the browser app and the apps (/api/…)
  Services/               accounts, memberships, e-mail (outbox + sender), books, packages, media, page rendering,
                          sync hub, maintenance
  Data/                   storage interfaces; MariaDb/ (Dapper) and Memory/ (tests, demo)
  Pages/                  sign-in/up, account, organization, administration pages (Razor)
  wwwroot/js/app/         the music app in the browser (library, player, timing editor, sync) - ES modules, no build step
src/PracticalMusic.Core/Online/   protocol shared by website and apps: API types, OnlineClient, OnlineSyncClient, SyncWire
database/                 SQL: database + user, schema, promote administrator
packaging/web/            build-web.ps1/.sh, systemd unit, Apache site, settings template
```

### API overview (JSON, camelCase)

| | |
|---|---|
| `POST /api/auth/login` | apps: e-mail + password → bearer token (+ user and organizations) |
| `POST /api/auth/logout` · `GET /api/me` · `GET /api/orgs` | |
| `GET /api/orgs/{org}/books` | book list (id, uid, title, revision, fingerprint, …) |
| `GET /api/books/{id}` · `/summary` · `/package` | book with media map · summary · .pmbook download (headers `X-PM-Revision`, `X-PM-Fingerprint`) |
| `POST /api/orgs/{org}/books` · `PUT /api/books/{id}` · `DELETE /api/books/{id}` | create / update (with `baseRevision`; 409 on conflict) / delete - managers |
| `POST /api/orgs/{org}/books/package?baseRevision=&force=&asNew=` | upload a .pmbook - managers |
| `POST /api/orgs/{org}/media` | upload a PDF or audio file - managers |
| `GET /api/media/{id}/file` · `GET /api/media/{id}/pages/{n}?w=` | the file (range requests) · a page as PNG |
| `GET /ws/sync` | WebSocket for synchronized playback (protocol in `SyncWire.cs`) |
