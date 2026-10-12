**English** · [Türkçe](README.tr.md)

# Kulvar

A multi-tenant B2B SaaS application in which small and medium-sized companies manage their **customer records and support tickets**.
Each company works in its own "lane" (*kulvar* in Turkish): one company's data can be neither read nor modified by another company's user —
this is enforced separately in the application code, in the EF Core filter **and** in PostgreSQL row-level security (RLS), and it is tested on real PostgreSQL.

**Owner / author:** TCoderr

> This is a portfolio project. It does not send real payments, emails or SMS; all demo data is synthetic. The payment provider and the mailbox are **simulations**.
> Known limitations: [docs/known-limitations.md](docs/known-limitations.md).

## What it does

- Sign-up, email verification, login/logout; server-side session (cookie) + CSRF protection; account lockout and rate limiting.
- Company (tenant) creation; roles: **Owner / Admin / Agent / Viewer**; invitations (single-use, time-limited, matched to a verified email);
  ownership transfer (with explicit acceptance, atomic); the last Owner is protected.
- Customers and support tickets (Open → In progress → Resolved → Closed, reopening), plain-text comments, audit log.
- Plans and entitlements: Trial (14 days) / Starter / Team; seat, customer and monthly ticket quotas **are not exceeded under concurrent requests**.
- Simulated billing: plan changes, signed (HMAC) webhooks, a state machine resilient to duplicate/out-of-order/late events, reconciliation and operator resolution.
- Durable outbox/inbox + lease-based workers; demo mailbox; CSV export (protected against formula injection).
- Company deletion: grace period → permanent purge → tombstone record.
- Platform operator panel: **metadata only**; it cannot read company content and cannot impersonate anyone.

## Architecture (summary)

| Layer | Choice |
| --- | --- |
| Backend | ASP.NET Core 10 (minimal API), modular monolith: Identity, Tenancy, Customers, Support, Billing, Operations |
| Data | PostgreSQL 18, EF Core 10; shared schema + `tenant_id` + **RLS (ENABLE + FORCE)**; composite foreign keys |
| Database roles | `kulvar_owner` (migrations only), `kulvar_app` (API), `kulvar_platform` (operator + webhook), `kulvar_worker` — none of them is superuser/BYPASSRLS |
| Identity | `__Host-`-prefixed HttpOnly cookie + server-side session table + antiforgery token (no JWT; rationale: [ADR 0003](docs/adr/0003-auth-and-session.md)) |
| Background jobs | Database outbox/inbox, lease + fencing token, fair per-tenant dispatch, retries + dead-letter queue |
| UI | React 19 + TypeScript + Vite, same origin; TanStack Query (cache key scoped to user + company) |
| Runtime | Docker Compose, HTTPS, read-only root filesystem, unprivileged user, Docker secret files |

Design documents: [docs/](docs/) (01–09) and architecture decisions: [docs/adr/](docs/adr/).
How tenant isolation is verified: [docs/tenant-isolation-verification.md](docs/tenant-isolation-verification.md).

## Requirements

- Docker Desktop (or Docker Engine + Compose v2)
- .NET SDK 10.0.401 (`global.json`) — for local build/test and the developer HTTPS certificate
- Node.js 24 + npm — only for developing/testing the UI locally (the Docker build uses its own Node image)
- Developer certificate on first use: `dotnet dev-certs https --check` (if missing, `dotnet dev-certs https --trust`)

No global tool installation is required. The stack opens only port `127.0.0.1:8843`; the Compose project name is `kulvar`.

## Running

### Windows (PowerShell)

```powershell
.\scripts\local-stack.ps1 init -SeedDemo          # secrets, certificate, demo passwords (under .local\; not printed to the screen)
.\scripts\local-stack.ps1 up -SeedDemo -Simulation # build + start + demo data
.\scripts\local-stack.ps1 status
.\scripts\local-stack.ps1 logs
.\scripts\local-stack.ps1 down                     # stops the stack; data (volumes) is KEPT
```

### Linux / macOS / Git Bash

```bash
./scripts/local-stack.sh init --seed-demo
./scripts/local-stack.sh up --seed-demo --simulation
./scripts/local-stack.sh status
./scripts/local-stack.sh logs
./scripts/local-stack.sh down
```

Application: **https://localhost:8843**

- `-Simulation` / `--simulation`: enables choosing the payment outcome (success, declined, timeout…) from the UI and triggering provider events. Accepted only in the `Development` environment.
- If you start without `-SeedDemo` you begin with an empty system; you sign up and get the verification link from the in-app **demo mailbox**.
- Different port / a second stack side by side: `-Project <name> -LocalDir <directory> -Port <n>` (bash: `--project --local-dir --port`).

### Deleting data (destructive, separate confirmation)

`down` does not delete any data. Deleting the database, key and export volumes requires explicit confirmation; the script deletes nothing
without first verifying (by label) that the volumes belong to this product:

```powershell
.\scripts\local-stack.ps1 reset -ConfirmReset kulvar
```

```bash
./scripts/local-stack.sh reset --confirm-reset kulvar
```

## Demo accounts

`init -SeedDemo` creates a random password for each account. **The passwords are not in this document**; they are in local files that never enter git:

```
.local/seed/Seed__Passwords__<key>          # e.g. .local/seed/Seed__Passwords__ayse.owner
```

| Key | Email | Akasya Mobilya (A) | Boreas Yazılım (B) |
| --- | --- | --- | --- |
| `ayse.owner` | ayse.owner@akasya.test | Owner | — |
| `ali.admin` | ali.admin@akasya.test | Admin | — |
| `asli.agent` | asli.agent@akasya.test | Agent | — |
| `arda.viewer` | arda.viewer@akasya.test | Viewer | — |
| `burak.owner` | burak.owner@boreas.test | — | Owner |
| `belma.agent` | belma.agent@boreas.test | — | Agent |
| `cem.ortak` | cem.ortak@serbest.test | Agent | Admin |
| `operator` | operator@kulvar.test | — (platform operator) | — (platform operator) |

- **Akasya Mobilya (A):** Team plan, 5 members, 6 customers, 7 tickets.
- **Boreas Yazılım (B):** Trial plan, 3 members (seat limit reached), 3 customers, 4 tickets.
- `cem.ortak` works in two companies with different roles; the account exists to show that permissions and data change when switching companies.
- `operator` is not a member of any company; it sees only the `/platform` panel (metadata).

All people, companies, emails (`.test`) and phone numbers are made up.

## Development and verification

```powershell
.\scripts\verify.ps1 -Web      # locked restore, Release build, unit + integration tests (real PostgreSQL 18), web gates
.\scripts\stack-smoke.ps1      # end-to-end smoke test on an isolated Docker stack (separate project/port); deletes its own resources when done
.\scripts\e2e.ps1              # real-browser (Playwright + axe) tests on an isolated stack
.\scripts\scan-secrets.ps1     # secret/signature scan + NuGet and npm vulnerability audit
```

- Integration tests start their own disposable PostgreSQL container (Testcontainers) and run **with the restricted runtime roles**;
  they do not touch the development database. SQLite and EF InMemory are not used.
- The verification scripts use Compose projects and ports separate from the development stack (`kulvar`).
- Results of the recorded verification run: [docs/final-verification.md](docs/final-verification.md); raw summaries: `artifacts/verification/`.
  They are local results and belong to the source state and fingerprint recorded in that document, before the repository was pushed (its
  "remote CI was not run" statement describes that moment). Later changes: this README was translated (`README.md` in English, Turkish text
  in `README.tr.md`); commits `84f5be4` and `1ca1abb` changed `scripts/local-stack.sh` (executable bit; secret file permissions on Linux) and
  the CI workflow. Because of these (and because `scripts/source-fingerprint.ps1` also counts `README.tr.md`), the fingerprint of the current
  tree is not the recorded value.
- Local checks after those changes (2026-10-12; not part of the recorded verification, and their logs are not kept in the repository): the CI
  stack step (`init`, migration on an empty database, demo seed) was run under Linux file permissions; the full browser E2E suite was run
  against that stack (21 / 21, 0 flaky, 0 skipped), and `scripts/stack-smoke.ps1` was run (33 / 33). Unit and integration tests were not
  re-run locally.
- CI definition: [.github/workflows/ci.yml](.github/workflows/ci.yml). Remote CI: [run 38185570488](https://github.com/TCoderr/multi-tenant-saas-platform/actions/runs/38185570488)
  on commit `1ca1abb` completed successfully (.NET unit + integration, web, and Docker stack + browser end-to-end jobs; checked 2026-10-12,
  UTC+3). The statement is tied to that commit: no result is claimed for later commits, including the one that adds this README text.
  Current state: [GitHub Actions](https://github.com/TCoderr/multi-tenant-saas-platform/actions).

To develop the UI locally (`src/web`): `npm ci`, then `npm run dev` (it forwards API requests to the running stack).

## Directory structure

```
src/Kulvar.Api/                 composition root: API, worker, migrator, seed (one image, different commands); EF migrations
src/Kulvar.Platform/            shared infrastructure: DbRunner (scoped transaction), tenant context, outbox, audit, HTTP helpers
src/Kulvar.Modules.*/           Identity, Tenancy, Customers, Support, Billing, Operations
src/web/                        React UI
tests/Kulvar.UnitTests/         pure rule tests
tests/Kulvar.IntegrationTests/  API, RLS, concurrency and migration tests on real PostgreSQL
db/bootstrap/                   database roles and schemas (cluster level, once)
scripts/                        local stack, verification and scan scripts
docs/                           requirements, architecture, data model, API, threat model, test plan, ADRs, reports
design/preview.html             static preview of the UI (Demo)
```

## Third-party components

Dependencies and license attributions: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Notes on the skills/sources used: [docs/skills-and-sources.md](docs/skills-and-sources.md).
