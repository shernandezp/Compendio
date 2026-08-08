# Compendio

**A Markdown folder that is the database of record**, with an editor a non-technical person can use,
permissions, search, and a five-minute install.

Your pages are `.md` files in a folder you own. Open that folder in VS Code, back it up with the
tool you already use, copy it to a USB stick — it is yours, and Compendio is the thing that makes it
pleasant to read and write. There is no database server to install, no external service to sign up
for, and nothing to migrate out of later.

- **Spanish and English**, complete, from the first release.
- **Permissions that are explainable**: two states per folder, no deny rules, and folders you cannot
  see are absent rather than greyed out.
- **Search that obeys them**, in-process, with no external engine.
- **Encrypted folders** for the router credentials and the incident post-mortems.
- **One file to download.** Windows Service, systemd unit, or a container — same binary, same
  behaviour.

Licensed under **AGPL-3.0-or-later**.

---

## Install in five minutes

### Windows

1. Download `compendio-<version>-win-x64.zip` from the releases page and unzip it.
2. **Verify the checksum** (see [Unsigned releases](#unsigned-releases) below):
   ```powershell
   Get-FileHash .\compendio-<version>-win-x64.zip -Algorithm SHA256
   ```
   Compare it with `SHA256SUMS.txt` beside the download.
3. Run it:
   ```powershell
   .\compendio.exe
   ```
4. Open <http://localhost:8080> and follow the wizard.

To run it as a service instead, from an elevated prompt:

```powershell
.\compendio.exe install
```

### Ubuntu / Debian

```bash
unzip compendio-<version>-linux-x64.zip -d /opt/compendio
sha256sum -c SHA256SUMS.txt
chmod +x /opt/compendio/compendio
/opt/compendio/compendio
```

Then open <http://localhost:8080>. To run it as a systemd unit:

```bash
sudo /opt/compendio/compendio install
```

### Docker

```bash
cd deploy
docker compose up -d
```

Then open <http://localhost:8080>.

The compose file mounts **three** volumes and `keys` is one of them, on purpose: it holds the master
encryption key and the session key ring. Losing it makes every encrypted page unreadable and signs
every user out on every restart.

---

## Unsigned releases

Windows releases ship **unsigned**, and buying a code-signing certificate is not part of the plan.

The consequence is real: on first run of a downloaded executable, SmartScreen shows *"Windows
protected your PC"* and you have to click **More info → Run anyway**. Nothing is wrong; the binary
runs identically either way.

What we publish instead is a SHA-256 checksum beside every artifact, which is the honest substitute
for a signature and is a stronger check than a signature you did not verify. Note that a
*self-signed* code-signing certificate would not remove the dialog — SmartScreen reputation comes
from a publicly trusted CA. It is genuinely useful in exactly one case: an organization can sign the
binary with its own certificate and push that certificate to its machines' Trusted Publishers store
by group policy. That is your PKI and your decision, and it costs nothing either.

---

## What you get

| | |
|---|---|
| **Editing** | Rich text by default. Nobody sees `##`, `**` or `\|---\|` unless they ask for the Markdown view. Paste from Word, Outlook, Confluence or a web page and it becomes clean Markdown. |
| **The folder** | Every page is a `.md` file with YAML front matter. Edit one in VS Code and it shows up in the browser within two seconds. |
| **Permissions** | Access rules attach to folders. A folder either inherits — and can only *add* access — or is restricted to exactly the people and groups you list. There are no deny rules, because deny rules are where permission systems stop being explainable. |
| **Search** | SQLite FTS5, in-process. `sesion` finds *sesión*; `192.168.1.1` and `VPN-Site-A` survive as searchable units. The permission check is part of the query, so a result count never tells you about pages you cannot open. |
| **Encrypted folders** | AES-256-GCM, keys the instance generates for itself. Only administrators can edit inside one. |
| **History** | A snapshot on every change, including changes made in the content folder. Restoring writes a new version, so a mistaken restore is itself undoable. |
| **Bilingual** | Spanish and English throughout, including the setup wizard and the CLI. Pages can be tagged with a language and linked to their translations. |
| **AI, optional** | Off until you paste in an endpoint, and *absent* until then rather than greyed out. One OpenAI-compatible URL covers Ollama, Groq, OpenAI, Azure OpenAI and LM Studio — point it at Ollama on your own server and nothing leaves the machine. Improve writing, draft a page from rough notes, summarize, translate, ask the wiki. Every result is a proposal you accept or discard. Daily per-person and per-instance caps, so a metered endpoint cannot surprise you. [Setting it up](docs/install.md#the-ai-assistant-if-you-want-one) |
| **HTTPS** | `compendio cert create` issues a certificate for this machine. No certificate authority, no internet, no purchase. |

---

## What it refuses to be

No SMTP. No real-time collaborative editing. No plugin marketplace. No native mobile apps. No
multi-tenancy. **No required external service, ever** — not for search, not for AI, not for
authentication.

---

## Commands

```
compendio                       Start the server
compendio install|uninstall     Register as a Windows Service or systemd unit
compendio doctor [--json]       Check this instance and report what is wrong, in plain language
compendio backup --out <file>   Content plus a consistent database copy
compendio restore --in <file>
compendio reindex               Rebuild the search index from the content folder
compendio cert create           Issue a self-signed TLS certificate
compendio reset-admin-password  Local-console recovery — there is no email
```

Every verb is scriptable: no prompt that a flag or an environment variable cannot answer.

---

## Building from source

You need the .NET 10 SDK and Node 22+, and the [`Common.Mediator`][mediator] repository checked out
beside this one.

```bash
git clone https://github.com/shernandezp/Common.Mediator.git Mediator
git clone https://github.com/shernandezp/Compendio.git
cd Compendio

dotnet build            # builds the SPA into src/Server/wwwroot too
dotnet test

# Development, not Production: there is no launchSettings.json, so a bare `dotnet run` starts
# without appsettings.Development.json and shows the setup wizard instead of the seeded admin.
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server
```

Then open <http://localhost:8080> and sign in as `admin` / `Compendio!Dev1`.

To skip the client build (useful when only touching the server):

```bash
dotnet build -p:SkipClientBuild=true
```

**[docs/development.md](docs/development.md)** covers the rest: hot reload against a running server,
resetting the data directory, which tests need `git` on `PATH`, how to exercise each v1 feature, and
how to regenerate the committed API contract.

[mediator]: https://github.com/shernandezp/Common.Mediator

---

## Documentation

| | |
|---|---|
| [docs/install.md](docs/install.md) | The five-minute install, in detail, per platform |
| [docs/development.md](docs/development.md) | Running it locally, hot reload, tests, trying each feature |
| [docs/api.md](docs/api.md) | The HTTP API |
| [docs/translating.md](docs/translating.md) | Adding a language |
| [project-overview.md](project-overview.md) | What Compendio is, and why |
| [compendio-implementation/v0-implementation.md](compendio-implementation/v0-implementation.md) | How v0 was built, and every decision behind it |
| [compendio-implementation/v1-implementation.md](compendio-implementation/v1-implementation.md) | The same for v1: lifecycle, AI, and what was deliberately left out |

---

## Licence

Compendio is free software under the [GNU Affero General Public License, version 3 or
later](https://www.gnu.org/licenses/agpl-3.0.html). Because it is a network-accessible program, the
licence requires the running instance to offer its source: it does, from `GET /api/v1/about` and
from the footer of every page.
