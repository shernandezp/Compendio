# Installing Compendio

Three install modes, all first-class: a Windows Service, a systemd unit, or a container. The same
binary and the same behaviour in all three — the mode only changes how it is started and where it
puts its data by default.

The target is **under five minutes from this page to your first page**, on a machine you already
have, with no prerequisites to install first.

---

## Before you start

Nothing. That is the point.

No database server, no runtime to install, no account to create, no internet connection after the
download. If you want HTTPS, Compendio can issue its own certificate — see
[HTTPS](#https-without-a-certificate-authority).

---

## Windows

### The installer

1. Download `compendio-<version>-win-x64.zip`, then **unblock it before unzipping** and unzip it
   anywhere except `Program Files`:

   ```powershell
   Unblock-File .\compendio-<version>-win-x64.zip
   Expand-Archive .\compendio-<version>-win-x64.zip -DestinationPath C:\Compendio
   ```

   Unblocking the zip first matters: files extracted from a zip downloaded through a browser
   inherit its Mark of the Web, and Windows Server's default `RemoteSigned` execution policy
   refuses to run an unsigned script that carries it.

2. Right-click the Start button → **Terminal (Admin)**, then:

   ```powershell
   cd C:\Compendio
   powershell -ExecutionPolicy Bypass -File .\install-windows.ps1
   ```

   `-ExecutionPolicy Bypass` applies to that one command and changes nothing about the machine. It
   is written this way because it works whether or not step 1 was done.

   > **If that is still refused**, the execution policy comes from group policy rather than the
   > machine. `Get-ExecutionPolicy -List` shows where: if `MachinePolicy` or `UserPolicy` is set,
   > `-ExecutionPolicy` is ignored, and the script has to be unblocked
   > (`Unblock-File .\install-windows.ps1`) or the policy relaxed.

The script is in the zip. It asks three questions — where the data should live, which port, and
whether other computers should reach it — and every one has a default, so pressing Enter through it
is a valid install. Then it registers the Windows Service, grants it the one permission it needs,
opens the firewall if you said yes, starts it, waits until the wiki actually answers, and prints
the address and the administrator password to sign in with.

**Write that password down.** It is generated, shown once, and stored nowhere. If it is lost:

```powershell
.\compendio.exe reset-admin-password --password "<a new one>"
```

Run the script again any time — it reuses an existing service, leaves an existing data folder
alone, and never creates a second administrator.

For an unattended install:

```powershell
powershell -ExecutionPolicy Bypass -File .\install-windows.ps1 -DataDir 'D:\CompendioData' -Port 8080 -OpenFirewall -Unattended
```

### Without installing anything

To try it first, unzip and run it directly:

```powershell
.\compendio.exe
```

Then open <http://localhost:8080>. The setup wizard starts with a language picker and asks you to
create the administrator yourself. Data lands in a `data` folder beside the executable. Closing the
window stops it — that is what the service is for.

**Windows may show "Windows protected your PC"** on a file downloaded through a browser. Click
**More info → Run anyway**. Compendio is not code-signed; the checksum published beside the
download is the substitute, and the README says why. The installer script clears this marker for
you, so it only comes up on the direct-run path:

```powershell
$expected = (Get-Content .\compendio-<version>-win-x64.zip.sha256).Split(' ')[0]
$actual   = (Get-FileHash .\compendio-<version>-win-x64.zip -Algorithm SHA256).Hash.ToLower()
if ($expected -eq $actual) { 'OK' } else { 'MISMATCH — do not run it' }
```

### Removing it

```powershell
.\compendio.exe uninstall
```

**Uninstalling leaves your data untouched.** Your content folder, database and keys stay where they
are.

### Where the data goes

Never inside `Program Files` — the service account cannot write there, and Compendio refuses to
start rather than fail quietly later. The installer rejects that choice outright. Anywhere else is
fine; a second drive is better. The service runs as the virtual account `NT SERVICE\Compendio`,
never as LocalSystem, and writes to its data directory and nothing else on the machine.

### Logs

Event Viewer → Windows Logs → Application, source `Compendio`. Also as rolling files in
`<data>\logs`.

---

## Ubuntu, Debian, RHEL

### As an application

```bash
sudo mkdir -p /opt/compendio
sudo unzip compendio-<version>-linux-x64.zip -d /opt/compendio
sha256sum -c compendio-<version>-linux-x64.zip.sha256
sudo chmod +x /opt/compendio/compendio
/opt/compendio/compendio
```

Open <http://localhost:8080>.

### As a systemd unit

```bash
sudo /opt/compendio/compendio install
```

That writes `/etc/systemd/system/compendio.service`, creates a `compendio` system user, and starts
it. The unit is hardened: `NoNewPrivileges`, `ProtectSystem=strict`, `PrivateTmp`, and
`ReadWritePaths` limited to the data directory. `deploy/compendio.service` is a readable copy of
what you will get.

```bash
systemctl status compendio
journalctl -u compendio -f
```

`arm64` machines — including a Raspberry Pi 4 or 5 — use `compendio-<version>-linux-arm64.zip`.
Everything else is identical.

---

## Docker

```bash
cd deploy
docker compose up -d
```

Open <http://localhost:8080>.

### The three volumes

```yaml
volumes:
  - ./data/content:/data/content   # your pages, as Markdown files
  - ./data/db:/data/db             # users, permissions, history, search index, audit log
  - ./data/keys:/data/keys         # master encryption key and session key ring
```

`keys` is separate on purpose, and it is the one people forget.

- Lose it and **every encrypted page becomes unreadable**. There is no recovery: that is what
  encryption means.
- Fail to persist it and **every restart signs every user out**, because the session key ring lives
  there too.

Back it up, and back it up somewhere other than beside your content.

---

## HTTPS, without a certificate authority

Most small organizations have no PKI and no public hostname, so "supply a certificate" is not a real
option. Compendio issues its own:

```bash
compendio cert create
```

That writes a self-signed certificate to `<data>/keys/tls/`, valid for two years, covering this
machine's host name, its fully-qualified name and its LAN addresses. Then:

```
Tls:Enabled = true
Tls:Port    = 8443
```

and restart.

**Browsers will warn until the certificate is trusted.** That is expected and it is not a bug — a
self-signed certificate is exactly as strong as a purchased one for encryption, and exactly as weak
for proving who you are. To stop the warning inside your organization:

- **Windows**, per machine or by group policy:
  ```powershell
  Import-Certificate -FilePath '<data>\keys\tls\compendio-tls.pfx' -CertStoreLocation Cert:\LocalMachine\Root
  ```
- **Linux**:
  ```bash
  sudo cp compendio-tls.crt /usr/local/share/ca-certificates/
  sudo update-ca-certificates
  ```

`compendio cert create --renew` reissues it. `compendio doctor` warns 30 days before it expires.

If you *do* have a certificate, supply it instead:

```
Tls:Enabled          = true
Tls:CertificatePath  = /etc/ssl/compendio.pfx
Tls:CertificatePassword = …
```

### Behind a reverse proxy

Compendio works behind IIS, nginx or Caddy. Forward `X-Forwarded-For`, `X-Forwarded-Proto` and
`X-Forwarded-Host`, and set `Security:RequireHttps=true`.

**IIS in-process hosting is incompatible with single-file publishing.** Use out-of-process /
reverse-proxy mode.

---

## Configuration

`appsettings.json`, environment variables, or the command line — in increasing order of precedence.
Environment variables use the framework's `Section__Key` convention:

```bash
Instance__DefaultLanguage=en
Security__RequireHttps=true
Content__WatcherMode=Poll
```

A fresh install with no configuration at all must start, and does.

| Setting | Default | Notes |
|---|---|---|
| `DataDir` | `./data` | Resolved against the binary, not the working directory. Holds `content/`, `db/`, `logs/`, `keys/`. |
| `Content:Root` | `<DataDir>/content` | Where your `.md` files live. |
| `Content:WatcherMode` | `Auto` | `Auto` polls when the folder is on a network share, because change notifications are unreliable over SMB. |
| `Instance:DefaultLanguage` | `es` | The default for people who have not chosen one. |
| `Instance:DefaultAccess` | `Read` | `None` for a locked-down install. |
| `Security:RequireHttps` | `false` | Adds HSTS and marks the session cookie `Secure`. |
| `Security:MasterPassphrase` | *(unset)* | Encrypts the master key with a passphrase. Once set, encrypted folders will not open without it. |
| `History:RetentionDays` | `365` | Then thinned to one version per day, never below `MinVersionsKept`. |
| `Attachments:MaxSizeBytes` | 25 MB | |
| `Ai:DefaultDailyPerUser` | `50` | AI requests per person per rolling 24 h, until an admin sets one. `0` for no cap. |
| `Ai:DefaultDailyPerInstance` | `0` | The same across the whole instance. `0` for no cap. |
| `Ai:MaxInputCharacters` | `24000` | Characters of a page sent in one AI request. |
| `Ai:TimeoutSeconds` | `120` | Generous, because a local model on CPU is slow rather than broken. |

---

## The AI assistant, if you want one

Entirely optional. **With nothing configured here, no AI control appears anywhere in the product** —
not greyed out, not "upgrade to enable", absent. Everything else works exactly as it does now.

One OpenAI-compatible endpoint covers Ollama, Groq, OpenAI, Azure OpenAI, LM Studio and vLLM, so
there is one form to fill in. **Administration → Integrations:**

| Field | Ollama on this machine | A hosted provider |
|---|---|---|
| Base URL | `http://localhost:11434/v1` | e.g. `https://api.groq.com/openai/v1` |
| Model | `llama3.1` | whatever the provider calls it |
| API key | leave empty — Ollama does not want one | paste it; it is encrypted before it is stored |

**Test connection** sends a one-token request and shows you the model's own reply, so you know it
works before anyone else finds out it does not.

### What it can do

*Improve writing* · *Summarize* · *Draft a page from rough notes* · *Translate a page* ·
*Ask the wiki* · *Freshness hints*. Each one can be switched off individually on the same screen, and
a switched-off feature disappears rather than failing when used.

Freshness hints answer the question the review dates cannot: *overdue for review* and *actually out
of date* are different things, so the check sits both on the page and beside every row of the stale
report. It runs only when somebody asks — there is no background sweep of the whole wiki against a
paid endpoint.

Every result is a **proposal you accept or discard**. Nothing a model produces is written to disk
without a person deciding it should — except a translation, which becomes a real page carrying a
visible "machine-translated, unreviewed" badge that clears when a human saves it.

### Where your content goes

Page content is sent to the endpoint you configured, and the product says which one next to every AI
button rather than burying it here. **A local model — Ollama or LM Studio on your own server — means
nothing leaves the machine**, which for most organizations reading this is the only acceptable
answer, and it is why the setup above lists it first.

Two boundaries you control:

- **Allowed spaces** — restrict AI to specific top-level folders. Empty means all of them.
- **Encrypted folders are excluded by default** and stay that way until you switch on *Allow AI
  features to read these pages* for that folder, having been told which endpoint its contents would
  go to.

Retrieval for *Ask the wiki* is filtered by the asking user's own read permissions **before** any
page is read from disk, and every source it cites is re-checked before the answer is sent. Somebody
cannot ask the assistant for a page they could not open themselves.

### Keeping the cost bounded

A hosted endpoint charges per request. Two caps, both on **Administration → Integrations**, both
counted over a **rolling 24 hours** rather than a calendar day — so "8 of 50 used in the last 24
hours" is exactly what is enforced:

| Cap | Default | What it is for |
|---|---|---|
| Requests per person per day | **50** | A page loop, a retried batch, or somebody discovering the button |
| Requests for everyone per day | 0 — off | A second ceiling across the instance, for a metered endpoint |

Set either to `0` to remove it. The defaults can be changed for a fleet with `Ai:DefaultDailyPerUser`
and `Ai:DefaultDailyPerInstance`.

Three things worth knowing before you pick a number:

- **A request that fails at the provider still counts.** A timeout arrives *after* the model has
  generated tokens, so refunding failures would refund exactly the requests that cost the most — and
  would leave a retry loop as a free way to spend your endpoint.
- **A request refused for permissions costs nothing**, because it never reached the provider.
- **Test connection and `compendio doctor` are not charged.** A diagnostic that refused to run
  because the wiki had been busy would be a diagnostic nobody could trust.
- The same screen shows what the instance actually spent in the last 24 hours and who spent it, so
  the cap is a decision rather than a guess. It records that a request happened, never what it said.

`compendio doctor` reports the caps and how much of the instance one is gone, and warns when it is
spent — because "the AI buttons stopped working" is a support ticket whose likeliest cause is a spent
allowance rather than a broken endpoint.

Users are told plainly when they hit a cap, including roughly how long until it frees up. Nothing is
lost — the request simply did not run.

`Ai:MaxInputCharacters` (default 24 000) bounds the size of each request, so a 4 MB page cannot
become one large bill on its own.

---

## Startup checks

Compendio refuses to start on problems that cause silent, late damage, and warns about the rest.
Every message names the path, the account or the port involved.

| Check | What happens |
|---|---|
| Database on a network path | **Refuses to start.** SQLite's locking does not work over SMB or NFS, and the corruption shows up weeks later. |
| Data directory not writable | **Refuses**, naming the account it is running as. |
| Port in use | **Refuses**, naming the port. |
| Another instance on the same data directory | **Refuses**, naming the holding process. The lock is an OS file lock, so a hard kill needs no cleanup. |
| Content folder on a share | **Warns**, and switches the watcher to polling. |
| Database newer than the binary | **Refuses.** An accidental downgrade must not write. |

---

## Upgrading

Stop, replace the binary, start.

Migrations are automatic and idempotent, and the database is copied with `VACUUM INTO` **before** any
migration runs — so a failed upgrade is recoverable rather than a restore-from-backup conversation.

---

## Backup

```bash
compendio backup --out compendio-backup.zip
```

The archive holds your content folder as it is on disk plus a consistent copy of the database, taken
with `VACUUM INTO` so it restores cleanly even under write load.

**If any folder is encrypted, a passphrase is required:**

```bash
compendio backup --out backup.zip --secure-passphrase "…"
```

This is not bureaucracy. Without it there are only two possible archives and both are wrong: one
that leaves out the encryption key and restores into unreadable files, and one that stores the key
next to the ciphertext and gives away everything the encryption was for. So the master key is
rewrapped under a passphrase you supply and keep somewhere else.

```bash
compendio restore --in backup.zip --secure-passphrase "…"
```

Restoring on a *different* machine works: the key is re-protected for the new machine on the way in.

---

## When something is wrong

```bash
compendio doctor
```

It reports, in plain language and in your language: whether the data directory is writable and by
which account, disk space, database integrity and size, which files failed to parse and where,
watcher mode and why, index status and queue depth, whether every encryption key is readable, ACL
orphans and expiring tombstones, TLS certificate expiry, and the age of the last backup.

It never prints page content or anything decrypted, so the whole output is safe to paste into a
GitHub issue. `--json` for monitoring. Exit codes: `0` clean, `1` the command failed, `2` findings.
