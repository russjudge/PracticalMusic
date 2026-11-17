# Setting up e-mail for the Practical Music website

The website sends a handful of messages: *confirm your e-mail address*, *reset your password*, *you are invited*,
*X asked to join* (to the organization's managers) and *you now have access*. They go out as
`noreply@russjudge.com` (any address of a domain you control works). Volume is tiny - a few per day.

This guide gets russjudge.com sending reliably. You need access to the DNS of russjudge.com at **Dynu** and to the
website's settings file (`/etc/practicalmusic/appsettings.json`).

Until e-mail works, the website keeps going: with `"Mode": "Pickup"` (the default) every message is saved as a file
in `/var/lib/practicalmusic/mail-pickup` (open it to click the link), the administrator can confirm addresses
(*Admin → Users → Mark e-mail as confirmed*) and hand out one-time password links (*Show a password link*), and
nothing is lost - queued messages are listed under *Admin → E-mail*.

---

## Why not send straight from the server?

A server on a home (residential) connection cannot deliver mail to Gmail, Outlook & co. directly:

- most residential providers block outgoing **port 25** (Comcast, for example, states it does not support port 25 for
  residential customers);
- residential IP ranges are on the **Spamhaus PBL** ("addresses that should never send mail directly"), so big
  mailbox providers reject or junk such mail;
- receivers expect a matching **reverse DNS (PTR)** record for the sending IP, and only the owner of the IP block
  (your ISP) can set it.

The fix that always works: the website hands its mail to an **SMTP relay** (a "smarthost") over an authenticated,
encrypted connection on port **587** (or 465 / 2525). The relay delivers it. Two good choices follow: Dynu's own
relay (your DNS is already there) or a transactional e-mail service with a free tier.

## Before you begin: Dynu's DNS record limit

**A free Dynu account allows only 4 custom DNS records** (A, CNAME, MX, TXT … for your domain); the Member plan
($9.99/year) allows 200. Option A below adds four TXT records, option B three or four. If russjudge.com already uses
custom records (for example for `practicalmusic`), you will need the Member plan. Check under
*Control Panel → DDNS Services → russjudge.com → DNS Records*.

Adding a record at Dynu: *Control Panel → DDNS Services → click russjudge.com → DNS Records*, choose the type, fill in
**Node Name** (leave it **empty** for russjudge.com itself; otherwise only the part before `.russjudge.com`, such as
`_dmarc`), the **Text** (TXT) or **Hostname** (CNAME), keep the TTL, save. Always publish SPF as type **TXT** (the old
"SPF" record type is obsolete). A name that already holds a CNAME cannot hold TXT records too.

After each change you can check from any Linux/macOS terminal (or the server): `dig TXT russjudge.com +short`.

---

## Option A (recommended): Dynu Outbound SMTP Relay

Dynu's *Outbound SMTP Relay* costs $9.99 per year per domain and relays up to **200 messages a day** (plenty). It
includes DKIM signing. Facts below are from Dynu's product page, FAQ and tutorial (November 2026); the relay was also
probed directly: it answers on 587 and 2525 with STARTTLS and on 465 with TLS, with a valid certificate for
`relay.dynu.com`.

1. **Order the relay** for russjudge.com in the Dynu control panel (*Email Service → Outbound SMTP Relay*). There is no
   free trial; Dynu refunds within 14 days.

2. **Relay login.** The user name is `relay@relay.russjudge.com`. The password starts as your Dynu account password -
   set a separate one in the relay's settings (so your Dynu login is never in a configuration file).

3. **DKIM** (a signature that proves the mail is really from you). In the relay settings, run Dynu's DKIM wizard with:
   domain `relay.russjudge.com`, selector `mail`, key size 2048. It shows a public key (a long `v=DKIM1; k=rsa; p=…`
   text) and a private key.
   - Add a TXT record: Node `mail._domainkey.relay`, Text = the whole public key text.
   - Paste the private key into the relay's *DKIM Signing* section (canonicalization relaxed/relaxed, SHA-256) and enable it.

4. **SPF** (which servers may send for the domain). Add TXT records with the text `v=spf1 a:spf-relay.dynu.com ~all`:
   - Node `relay` (for `relay.russjudge.com`, as Dynu's tutorial describes), and
   - Node empty (for `russjudge.com` itself) - **unless russjudge.com already has an SPF record**: a name may have only
     one, so add `a:spf-relay.dynu.com` to the existing one instead (before `~all`), e.g.
     `v=spf1 a:spf-relay.dynu.com include:spf.example-provider.com ~all`.

   Use `a:spf-relay.dynu.com`. Dynu's FAQ also suggests `include:dynu.com`, but that alone uses all 10 DNS lookups SPF
   permits and does not list the relay's servers.

5. **DMARC** (tells receivers what to do with mail that fails the checks). TXT record, Node `_dmarc`, Text
   `v=DMARC1; p=none`. After a few weeks without problems you can change it to `v=DMARC1; p=quarantine`.
   Do not add `adkim=s` or `aspf=s` (strict alignment): Dynu signs as `relay.russjudge.com`, which only matches
   `russjudge.com` with the default relaxed alignment. (Reports with `rua=mailto:…` need a mailbox at russjudge.com;
   leave it out if you have none.)

6. **Website settings** - in `/etc/practicalmusic/appsettings.json`:

   ```json
   "Email": {
     "Mode": "Smtp",
     "FromAddress": "noreply@russjudge.com",
     "FromName": "Practical Music",
     "ReplyTo": "",
     "SmtpHost": "relay.dynu.com",
     "SmtpPort": 587,
     "SmtpSecurity": "StartTls",
     "SmtpUser": "relay@relay.russjudge.com",
     "SmtpPassword": "the relay password from step 2"
   }
   ```

   (Port 465 needs `"SmtpSecurity": "SslOnConnect"`; port 2525 works with `StartTls` if 587 is blocked.)
   Optionally set `ReplyTo` to a real mailbox, so replies to a notification reach a person.
   Then: `sudo systemctl restart practicalmusic-web`.

7. **Test** - see [Testing](#testing). Expect SPF and DKIM to pass for `relay.russjudge.com` / `russjudge.com`.

## Option B: a transactional e-mail service

These work from any connection (the website connects out to them) and have free tiers that cover a church or band.
Each asks you to add DNS records for russjudge.com - mostly CNAMEs, which avoids pasting long keys.

| Service | Free tier (Nov 2026) | SMTP host | Ports | DNS records they ask for |
|---|---|---|---|---|
| **SMTP2GO** | 1,000/month, 200/day | `mail.smtp2go.com` | 587, 2525, 8025 (STARTTLS); 465 (TLS) | 3 CNAMEs: return path, DKIM, link tracking (no SPF change needed) |
| **Brevo** | 300/day | `smtp-relay.brevo.com` | 587, 2525; 465 | `brevo-code` TXT, 2 DKIM CNAMEs, DMARC |
| **Mailjet** | 6,000/month, 200/day | `in-v3.mailjet.com` | 587, 2525; 465 | ownership record, SPF `include:spf.mailjet.com`, DKIM TXT |
| **Amazon SES** | pay per use ($0.10 per 1,000) | `email-smtp.<region>.amazonaws.com` | 587, 2587; 465 | 3 DKIM CNAMEs (+ optional MAIL FROM MX/TXT); starts in a "sandbox" |

Example with SMTP2GO:

1. Sign up (with an address at your own domain - they refuse free-mail addresses), add **russjudge.com** as a
   *sender domain*. It shows three CNAME records; add each at Dynu (Node = the part before `.russjudge.com`,
   Hostname = the target they show). Wait until SMTP2GO reports the domain as verified.
2. Create an *SMTP user*.
3. Website settings: `"SmtpHost": "mail.smtp2go.com"`, `"SmtpPort": 587`, `"SmtpSecurity": "StartTls"`, the SMTP user
   name and password, `"FromAddress": "noreply@russjudge.com"`, `"Mode": "Smtp"`; restart the service.
4. Add the DMARC record from option A, step 5.

## Option C: Postfix on the server (optional)

Only if you also want the server's own mail (cron, monitoring) to go out: install Postfix as a relay to option A or B,
and let the website talk to it on the server itself.

```
$ sudo apt install postfix libsasl2-modules        # choose "Satellite system"; libsasl2-modules is needed for the login
```

`/etc/postfix/main.cf` (the relevant lines):

```
relayhost = [relay.dynu.com]:587
smtp_sasl_auth_enable = yes
smtp_sasl_password_maps = hash:/etc/postfix/sasl_passwd
smtp_sasl_security_options = noanonymous
smtp_sasl_tls_security_options = noanonymous
smtp_tls_security_level = encrypt
inet_interfaces = loopback-only
```

```
$ echo "[relay.dynu.com]:587 relay@relay.russjudge.com:THE_RELAY_PASSWORD" | sudo tee /etc/postfix/sasl_passwd
$ sudo chmod 600 /etc/postfix/sasl_passwd && sudo postmap /etc/postfix/sasl_passwd && sudo systemctl restart postfix
```

Website settings: `"SmtpHost": "localhost"`, `"SmtpPort": 25`, `"SmtpSecurity": "None"`, no user/password.

---

## Testing

1. On the server: `cd /opt/practicalmusic && sudo -u practicalmusic ./PracticalMusic.Web send-test-email --to you@gmail.com`
   - it connects immediately and prints the relay's error if something is wrong. Or in the website:
   *Admin → E-mail → Send a test message*.
2. In Gmail open the message → ⋮ → *Show original*. You want **SPF: PASS**, **DKIM: PASS** and **DMARC: PASS**.
3. Check the DNS records: `dig TXT russjudge.com +short`, `dig TXT relay.russjudge.com +short`,
   `dig TXT mail._domainkey.relay.russjudge.com +short`, `dig TXT _dmarc.russjudge.com +short`.

## Troubleshooting

| Problem | Likely cause / fix |
|---|---|
| `Authentication failed` / `535` | wrong `SmtpUser`/`SmtpPassword` (Dynu: `relay@relay.russjudge.com` and the relay password) |
| Connection times out | outbound port blocked by the network: try `"SmtpPort": 2525` (StartTls) or 465 (SslOnConnect) |
| Mail lands in spam | DKIM not enabled or the TXT key wrong; SPF missing; check *Show original* |
| `DMARC: FAIL` | neither SPF nor DKIM aligned with russjudge.com - recheck steps 3-4; don't use strict alignment |
| Two SPF records | merge them into one TXT record (a second one makes SPF fail completely) |
| Dynu: account temporarily disabled | more than 200 relayed messages that day (ask Dynu for a higher limit) |
| Messages "queued" for a long time | *Admin → E-mail* shows the last error; *Retry now* resends after you fixed the settings |

Failed messages are retried after 1, 5, 15, 60, 240 and 720 minutes, then marked failed (retry them by hand).
Sent messages are kept in the outbox for 30 days, failed ones for 90.
