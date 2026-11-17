# Installing the Practical Music website on Debian 13 (Apache + MariaDB)

This guide sets up **https://practicalmusic.russjudge.com** on a Debian 13 ("trixie") server that already runs
Apache and MariaDB. For another host name, replace `practicalmusic.russjudge.com` wherever it appears; every
installation gets its own database, so another host simply repeats these steps on its own server.

How it fits together:

```
browser / app ──HTTPS 443──▶ Apache (TLS, Let's Encrypt) ──http://127.0.0.1:5080──▶ PracticalMusic.Web ──▶ MariaDB (local)
                                                                                        └──▶ /var/lib/practicalmusic (files)
```

The website is a self-contained ASP.NET Core program: **.NET does not have to be installed on the server.**

Time needed: about 30 minutes. Commands marked `$` run on the server, `PS>` on your Windows PC.

---

## 1. Before you start

- The DNS name must point at the server's public address. At Dynu (dynu.com), check that
  `practicalmusic.russjudge.com` is an A record (or an alias) for the server's IP, and that the Dynu update client
  keeps it current if the IP is dynamic.
- If the server is behind a router (home connection), forward **TCP 80 and 443** from the router to the server
  (port 80 is needed for Let's Encrypt and the redirect to HTTPS).
- You need `sudo` on the server.

## 2. Build the package (on your PC)

```
PS> cd C:\Development\PracticalMusic
PS> pwsh packaging\web\build-web.ps1
```

This creates `dist\practicalmusic-web-1.0.0-linux-x64.tar.gz` (for a 64-bit ARM server use
`-Runtime linux-arm64`). Copy it to the server, for example:

```
PS> scp dist\practicalmusic-web-1.0.0-linux-x64.tar.gz you@your-server:/tmp/
```

## 3. Install the program

```
$ sudo apt update
$ sudo apt install -y ca-certificates libssl3t64 libgssapi-krb5-2 certbot python3-certbot-apache
$ cd /tmp && tar -xzf practicalmusic-web-1.0.0-linux-x64.tar.gz
$ sudo useradd --system --user-group --home-dir /var/lib/practicalmusic --no-create-home --shell /usr/sbin/nologin practicalmusic
$ sudo mkdir -p /opt/practicalmusic /var/lib/practicalmusic /etc/practicalmusic
$ sudo cp -r /tmp/practicalmusic-web/app/. /opt/practicalmusic/
$ sudo chmod +x /opt/practicalmusic/PracticalMusic.Web
$ sudo chown -R root:root /opt/practicalmusic
$ sudo chown practicalmusic:practicalmusic /var/lib/practicalmusic
$ sudo chmod 750 /var/lib/practicalmusic
```

- `/opt/practicalmusic` - the program (replaced on every update; owned by root, read-only for the service).
- `/var/lib/practicalmusic` - uploaded PDFs and recordings, the page-image cache, the sign-in keys. **Back this up.**
- `/etc/practicalmusic/appsettings.json` - this server's settings and passwords (step 5).

## 4. Create the database and its user

Pick a strong password for the database account:

```
$ openssl rand -base64 30
```

Put it into the script - it appears twice, as `CHANGE_ME_TO_A_LONG_RANDOM_PASSWORD`:

```
$ nano /tmp/practicalmusic-web/database/01-create-database-and-user.sql
```

Then run the scripts as the MariaDB administrator (on Debian, `sudo mariadb` logs in as root without a password):

```
$ sudo mariadb < /tmp/practicalmusic-web/database/01-create-database-and-user.sql
$ sudo mariadb practicalmusic < /tmp/practicalmusic-web/database/02-schema.sql
$ sudo mariadb practicalmusic -e "SHOW TABLES; SELECT * FROM schema_version;"
```

You should see eleven tables and schema version 1. The account `practicalmusic@localhost` may only read and
write rows; it cannot change the tables (schema updates are applied by you with later scripts). Delete the edited
copy of the script afterwards: `rm /tmp/practicalmusic-web/database/01-create-database-and-user.sql`.

## 5. Configure

```
$ sudo cp /tmp/practicalmusic-web/server/appsettings.Production.example.json /etc/practicalmusic/appsettings.json
$ sudo nano /etc/practicalmusic/appsettings.json
$ sudo chown root:practicalmusic /etc/practicalmusic/appsettings.json
$ sudo chmod 640 /etc/practicalmusic/appsettings.json
```

Set at least:

| Setting | Value |
|---|---|
| `ConnectionStrings:Default` | replace `THE_PASSWORD_FROM_01-…` with the database password from step 4 |
| `Site:BaseUrl` | `https://practicalmusic.russjudge.com` (used in e-mail links and in downloaded books; **required** - the website refuses to start without it) |
| `Site:DataDir` | `/var/lib/practicalmusic` |
| `Email:*` | leave `"Mode": "Pickup"` for now - see [EMAIL-SETUP.md](EMAIL-SETUP.md) |

Other settings (defaults in `/opt/practicalmusic/appsettings.json`, override them in the `/etc` file):
`Site:MaxUploadMegabytes` (600), `Site:PageCacheMegabytes` (2048), `Site:RequireEmailVerification` (true),
`Site:PublicDownloads` (true), `Site:ApiTokenDays` (365 - how long an app stays signed in),
`Site:AuthRequestsPerMinute` (20 - sign-in attempts per minute from one address).

While `Email:Mode` is `Pickup`, every message is written as an `.eml` file to `/var/lib/practicalmusic/mail-pickup`
instead of being sent - handy while e-mail is not set up yet (you can open the files and click the links).

## 6. Check, create the administrator, start

The program finds `/etc/practicalmusic/appsettings.json` by itself. Run the commands from the program folder:

```
$ cd /opt/practicalmusic
$ sudo -u practicalmusic ./PracticalMusic.Web check
$ sudo -u practicalmusic ./PracticalMusic.Web create-admin --email you@example.com --name "Russ Judge"
```

`check` tests the data folder, the database (and its schema version), the PDF engine and the e-mail settings.
`create-admin` asks for a password (at least 8 characters) and creates the website administrator - the account
that may do everything, including setting up organizations. (Alternative: sign up on the website and run
`database/03-promote-site-admin.sql`.)

Install and start the service:

```
$ sudo cp /tmp/practicalmusic-web/server/practicalmusic-web.service /etc/systemd/system/
$ sudo systemctl daemon-reload
$ sudo systemctl enable --now practicalmusic-web
$ systemctl status practicalmusic-web
$ curl -s http://127.0.0.1:5080/healthz        # prints: ok
```

Logs: `journalctl -u practicalmusic-web -f`.

## 7. Apache and the HTTPS certificate

```
$ sudo a2enmod proxy proxy_http headers ssl rewrite
$ sudo systemctl restart apache2
$ sudo certbot certonly --apache -d practicalmusic.russjudge.com
$ sudo cp /tmp/practicalmusic-web/server/practicalmusic.russjudge.com.conf /etc/apache2/sites-available/
$ sudo a2ensite practicalmusic.russjudge.com
$ sudo apachectl configtest && sudo systemctl reload apache2
```

- `certbot certonly` only obtains the certificate (it needs DNS pointing here and port 80 reachable); the site file
  then uses it. Certificates renew automatically (`systemctl list-timers | grep certbot`; test with
  `sudo certbot renew --dry-run`).
- The site file forwards everything, including the WebSocket used for synchronized playback
  (`ProxyPass / http://127.0.0.1:5080/ upgrade=websocket`, supported since Apache 2.4.47; Debian 13 ships 2.4.6x),
  sets `X-Forwarded-Proto: https`, allows large uploads, and redirects http to https.
- Other sites on the same Apache (e.g. russjudge.com itself) are not affected.

Open **https://practicalmusic.russjudge.com**, sign in with the administrator account, then:

1. **Admin → Organizations**: set up the first organization (a church or a band) and, optionally, its first
   manager (people without an account receive an invitation e-mail to choose a password).
2. **Admin → E-mail**: send a test message once e-mail is configured ([EMAIL-SETUP.md](EMAIL-SETUP.md)).
3. Optional: put the app installers (`dist\PracticalMusic-1.0.0-win-x64.zip`, the Linux tarball, the Android `.apk`)
   into `/var/lib/practicalmusic/downloads/`; the website's **Downloads** page offers them.

## 8. Firewall

The website needs only what a web server needs. **No extra port has to be opened for synchronized playback** - it
travels over the same HTTPS connection (port 443) as the pages.

| Where | Allow | Why |
|---|---|---|
| Server, inbound | TCP **443** | the website, the apps' sign-in/downloads/uploads, synchronized playback (WebSocket) |
| Server, inbound | TCP **80** | Let's Encrypt renewals and the redirect to https |
| Server, inbound | TCP 22 from your LAN | SSH (administration) |
| Router (home connection) | forward TCP 80 and 443 to the server | so the internet can reach it |
| Server, outbound | TCP 443, and TCP **587** (or 465/2525) | Let's Encrypt; the e-mail relay ([EMAIL-SETUP.md](EMAIL-SETUP.md)) |
| Not open to the network | TCP 5080, TCP 3306 | the website program and MariaDB only listen on the server itself |

Debian 13 has the `nftables` firewall installed but not active (it allows everything). To turn it on with exactly
the ports above, write `/etc/nftables.conf` (adjust the LAN `192.168.1.0/24`):

```
#!/usr/sbin/nft -f
flush ruleset
table inet filter {
  chain input {
    type filter hook input priority 0; policy drop;
    ct state established,related accept
    iif lo accept
    ip protocol icmp accept
    ip6 nexthdr ipv6-icmp accept
    tcp dport { 80, 443 } accept
    ip saddr 192.168.1.0/24 tcp dport 22 accept
  }
  chain forward { type filter hook forward priority 0; policy drop; }
  chain output  { type filter hook output priority 0; policy accept; }
}
```

```
$ sudo nft -c -f /etc/nftables.conf && sudo systemctl enable --now nftables
```

(Keep an SSH session open while testing. If you prefer **ufw**: `sudo apt install ufw`,
`sudo ufw allow from 192.168.1.0/24 to any port 22 proto tcp`, `sudo ufw allow 80/tcp`, `sudo ufw allow 443/tcp`,
`sudo ufw enable`.)

On the **devices** nothing changes for the website: browsers and apps only make outgoing HTTPS connections. The apps'
own local-network features (discovery, sending books, LAN sync) still need UDP 47312 and TCP 47313 on each device, as
described in INSTALL-Windows.md / INSTALL-Linux.md.

## 9. Updating to a new version

```
PS> pwsh packaging\web\build-web.ps1            # then copy the tarball to the server
$ cd /tmp && rm -rf practicalmusic-web && tar -xzf practicalmusic-web-<version>-linux-x64.tar.gz
$ sudo systemctl stop practicalmusic-web
$ sudo rsync -a --delete /tmp/practicalmusic-web/app/ /opt/practicalmusic/
$ sudo chmod +x /opt/practicalmusic/PracticalMusic.Web
$ sudo systemctl start practicalmusic-web
```

If a release adds `database/04-….sql` (or later), run it with `sudo mariadb practicalmusic < …` before starting;
the website refuses to start against an outdated schema and says which script is missing. Settings in `/etc` and
the data in `/var/lib/practicalmusic` are never touched by an update.

## 10. Backups

What to keep: the database, the media files, the sign-in keys and the settings. The `cache` folder can be rebuilt.

```
$ sudo mkdir -p /var/backups/practicalmusic
$ sudo sh -c 'mariadb-dump --single-transaction --routines practicalmusic | gzip > /var/backups/practicalmusic/db-$(date +%F).sql.gz'
$ sudo tar -czf /var/backups/practicalmusic/files-$(date +%F).tar.gz -C /var/lib/practicalmusic media keys downloads -C /etc practicalmusic
```

Put these two lines in a nightly cron job (`sudo crontab -e`) and copy `/var/backups/practicalmusic` off the server.
To restore: create the database and user (step 4, script 01 only), `gunzip -c db-….sql.gz | sudo mariadb practicalmusic`,
and unpack the files archive back into `/var/lib/practicalmusic` (owner `practicalmusic`).

## 11. Troubleshooting

| Symptom | Look at |
|---|---|
| Browser shows 502/503 | `systemctl status practicalmusic-web`, `journalctl -u practicalmusic-web -n 100` |
| Service stops right after start | the log names the problem: unreachable database, missing tables (step 4), unreadable config |
| "Connecting…" never ends on the Share page | Apache must forward WebSockets: `upgrade=websocket` in the site file and `a2enmod proxy_http` |
| Uploads of large books fail | `LimitRequestBody 0` in the site file; `Site:MaxUploadMegabytes` |
| Page images do not appear | `./PracticalMusic.Web check` (PDF engine line) |
| Links in e-mails point to the wrong address | `Site:BaseUrl` |
| No e-mails arrive | Admin → E-mail shows each attempt and its error; [EMAIL-SETUP.md](EMAIL-SETUP.md) |
| Someone cannot get e-mail at all | Admin → Users → the person → *Show a password link* (or `./PracticalMusic.Web password-link --email …`) |

## Hosting it elsewhere

The same package runs on any 64-bit Linux with glibc (or `dotnet publish -r win-x64` for Windows). Each host needs:
MariaDB 10.6+ or MySQL 8 with the two SQL scripts applied to its own database; a reverse proxy that terminates
HTTPS and passes WebSockets; and its own `appsettings.json` (connection string, `Site:BaseUrl`, `Site:DataDir`, e-mail).
Installations are completely independent: accounts, organizations and books are not shared between hosts.

nginx instead of Apache:

```
map $http_upgrade $connection_upgrade { default upgrade; '' close; }
server {
    listen 443 ssl;  server_name practicalmusic.example.org;
    # ssl_certificate …; ssl_certificate_key …;
    client_max_body_size 650m;
    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 600s;
    }
}
```

If the proxy runs on a different machine than the website, list its address in `Site:TrustedProxies`
(e.g. `["192.168.1.5"]`) and change `Urls` (in the `/etc` settings file) from `http://127.0.0.1:5080` to an address the
proxy can reach.

To try the website on a development PC without any database: `dotnet run --project src/PracticalMusic.Web`
starts it at http://localhost:5080 in demo mode (everything in memory; sample accounts `admin@example.com`,
`manager@example.com`, `member@example.com`, password `demo-password-123`).
