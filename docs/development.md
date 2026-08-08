# Running Compendio locally

For working on Compendio. If you want to *install* it, [install.md](install.md) is the document you
want — this one deliberately does things the supported install modes do not.

---

## Prerequisites

- **.NET 10 SDK**
- **Node 22+** — contributors need it, users never do. The SPA is built at CI time and embedded into
  the binary.
- The [`Common.Mediator`](https://github.com/shernandezp/Common.Mediator) repository checked out
  **beside** this one, as `Mediator`. The server project references it by relative path.

```
<parent>/
├── Mediator/
└── Compendio/
```

---

## The short version

From the repository root:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Server
```

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server
```

Then open <http://localhost:8080> and sign in as **`admin` / `Compendio!Dev1`**.

### Set the environment variable

There is no `launchSettings.json`, so a bare `dotnet run` starts in **Production**. That is a working
instance, but it is not the one you want for development: `appsettings.Development.json` is not
loaded, so there is no seeded administrator and you get the setup wizard instead.

Development also turns on two things worth having:

- **`/openapi/v1.json`** is served. Production does not map it.
- **Container scope validation.** ASP.NET Core validates service lifetimes on build in Development
  and nowhere else, which is what catches a singleton capturing a scoped service — a class of bug
  that starts fine under test and throws on `dotnet run`. There is also a test for it
  (`ContainerValidationTests`), because finding it by running the app is finding it late.

### What Development gives you

| | |
|---|---|
| Sign-in | `admin` / `Compendio!Dev1`, from `appsettings.Development.json` |
| Data directory | `src/Server/data-dev/` — content, SQLite, logs, keys |
| Port | 8080, from `Urls` in `appsettings.json` |

The bootstrap password lives in the repository on purpose, and the file carrying it is
`CopyToPublishDirectory=Never` so it cannot reach a published build.

---

## Working on the UI

`dotnet run` builds the SPA into `wwwroot` on every start, which is fine for a one-off but slow to
iterate against. For hot reload, run two processes:

```bash
# terminal 1 — the server
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Server

# terminal 2 — Vite
cd src/client
npm run dev
```

Open the URL Vite prints (5173 by default). It proxies `/api`, `/health` and `/ready` to port 8080,
so the React side hot-reloads against the real server, the real database and real files on disk.

The proxy exists **only** for this. In every shipped configuration the SPA is served by Kestrel from
the same origin as the API, which is what makes `SameSite=Strict` cookies and no CORS the whole CSRF
posture.

To skip the client build when you are only touching the server:

```bash
dotnet build -p:SkipClientBuild=true
```

That leaves whatever is already in `wwwroot`. On a clean checkout there is nothing there, so the API
answers and the site 404s.

---

## Starting over

```powershell
Remove-Item -Recurse -Force src/Server/data-dev
```

```bash
rm -rf src/Server/data-dev
```

Deletes content, database, logs and keys. The next start seeds the administrator again in
Development, or shows the setup wizard in Production.

Deleting `data-dev/keys/` alone is worth doing once: the service should still start, non-encrypted
content should serve normally, and secure scopes should report `secure.unavailable`. A key problem
must not take the whole wiki down, and must not fail open.

---

## Tests

```bash
dotnet test                       # 258 server tests, 1 skipped

cd src/client
npm run check                     # i18n parity, tsc -b, vitest
```

The skipped server test creates a symbolic link, which needs Developer Mode or elevation on Windows.
Skipping is honest where pretending the assertion ran is not.

Some tests need real tools on `PATH`:

| Test | Needs |
|---|---|
| `GitMirrorTests` | `git`. It creates a bare repository in the temp directory and clones it back |
| Everything else | Nothing. The AI provider is a scripted stub, so no model is ever installed or called |

The git-mirror test leaves a `.git` directory inside the test content folder — exactly as enabling
the mirror does in production — and git marks its objects read-only, which is why the fixture clears
that attribute before deleting its temp directory.

---

## Trying the v1 features

Everything below is off or empty on a fresh instance, which is the intended state rather than a
broken screen.

### Lifecycle

Open any page → **Review and ownership**. Set an owner, a review interval, and whether the page needs
acknowledging. Then:

- The **stale banner** appears once the review date has passed. To see it without waiting, set
  `nextReviewDate` to a past date through `PUT /api/v1/pages/lifecycle` — the panel deliberately
  starts the clock from today, because that is what "review this every 90 days" means when a person
  types it.
- **Confirm reviewed** on the banner is the only thing that resets the clock. An ordinary save does
  not, and that is the point: fixing a typo is not a review.
- The **stale report** is at `/stale`, the **dashboard** at `/`.

### Acknowledgment

Tick *requires acknowledgment*, then confirm it from the page banner. **Acknowledgments** on the page
shows who has and who has not.

Save the page normally — still acknowledged. Save it with **Material revision** ticked — re-opened
for everybody. That checkbox is the whole re-open mechanism, and it is deliberately not a diff
heuristic.

### Notifications

The bell polls once a minute. The review scan runs two minutes after startup and then daily, so the
quickest way to see notification behaviour is `dotnet test --filter LifecycleTests`, which runs a
scan directly rather than waiting for the timer.

### AI

Administration → **Integrations**. With [Ollama](https://ollama.com) running locally:

| | |
|---|---|
| Base URL | `http://localhost:11434/v1` |
| Model | whatever you have pulled, e.g. `llama3.1` |
| API key | leave empty — Ollama does not want one |

**Test connection** issues a one-token completion and reports the model's own reply. Then the AI menu
appears on pages and in the editor, **Ask the wiki** appears in the header, **Draft with AI** appears
on the new-page screen, and a freshness check appears beside every row of the stale report.

With nothing configured, every AI endpoint returns `404 ai.disabled` and no AI control renders
anywhere. That is a review criterion, and there are tests on both sides of it.

Two things that surprise people working on this locally:

- **There is a daily budget, and it is on by default** — fifty requests per person over a rolling 24
  hours, even against a local Ollama where nothing is being charged. Raise it on the same screen, set
  it to `0`, or set `Ai:DefaultDailyPerUser` in `appsettings.Development.json`. Integration tests set
  it explicitly rather than relying on the default, so a test that makes a lot of AI calls does not
  start failing on a budget it never meant to exercise.
- **Selection-scoped actions need a selection in the editor.** Highlight a paragraph and the menu
  changes to *Improve selection*; the request then carries that Markdown — serialized out of
  ProseMirror, not read off the DOM — and accepting splices it back over the same range.

### Git mirror

Off unless `GitMirror:Enabled` and a remote URL are set, and enabling it creates a `.git` directory
inside your content folder. It pushes only — there is no pull and no merge back.

---

## Regenerating the API contract

`docs/openapi/v1.json` is committed and CI fails on a diff, so adding or changing an endpoint means
regenerating it. The server must run in **Development**, because that is the only environment that
serves the document:

```bash
dotnet build src/Server/Compendio.Server.csproj -c Release -p:SkipClientBuild=true

ASPNETCORE_ENVIRONMENT=Development DataDir="$(mktemp -d)" Urls=http://127.0.0.1:8099 \
  dotnet run --project src/Server --no-build -c Release &

curl -sf http://127.0.0.1:8099/openapi/v1.json -o /tmp/openapi.json
node scripts/format-openapi.mjs /tmp/openapi.json docs/openapi/v1.json
```

Formatting goes through `scripts/format-openapi.mjs` — the same script that produced the committed
copy — because two formatters would never agree on a document containing an accented character. It
also drops `servers`, which would otherwise record whichever port you generated on.

---

## Things that will confuse you once

- **A bare `dotnet run` gives you the setup wizard.** Set `ASPNETCORE_ENVIRONMENT=Development`.
- **The SPA 404s but the API answers.** `wwwroot` is empty — you built with `SkipClientBuild=true` on
  a clean checkout.
- **The port is 8080, not 5000.** It comes from `Urls` in `appsettings.json`.
- **Windows Defender and the content folder.** Real-time scanning makes the file watcher noisy and
  slow. Excluding the data directory is worth it on a development machine.
- **A stale `compendio` process holds the build outputs.** A test host or a previous `dotnet run` can
  keep `compendio.dll` locked and fail the next build with MSB3027; stop it and rebuild.
