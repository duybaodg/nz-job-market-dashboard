# AGENTS.md — instructions for coding agents working on this repository

You are continuing a mature, incrementally-hardened project. Read this file
fully before doing anything. The full round-by-round engineering history is in
`docs/agent-plans/2026-10-12-crawl-data-quality-report.md` on the deployment
host (gitignored — ask the operator if you need it).

## What this project is

NZ Job Market Dashboard: React/TS/Vite frontend, .NET 9 Web API + Worker,
PostgreSQL (EF Core), Python Scrapling crawler driven by
`crawler/sources/sites.json`, optional self-hosted Firecrawl fetch layer.
Two audience views (Find jobs `/jobs`, Market trends `/trends`) plus a
password-gated Crawler health view (`/health`). Runs as 4 Docker Compose
services (postgres/backend/worker/frontend) on a Raspberry Pi 5.

## Hard rules — never break these

1. **Never bypass robots.txt.** `SCRAPLING_ROBOTS_TXT_OBEY=true` stays
   pinned. Robots-denial crawl failures are EXPECTED, not bugs. Any new
   fetch path (including Firecrawl) must check robots before fetching —
   the existing `RobotsTxtMatcher`/`obeys_robots()` are the pattern.

   **Scoped, dated exception (2026-10-23, operator Bao Duy):** the operator
   explicitly instructed that the LinkedIn guest-endpoint CLI becomes a
   SCHEDULED source and hard rule #1 is waived FOR LINKEDIN GUEST CRAWLING
   ONLY. `linkedin.com` robots.txt is `User-agent: *` → `Disallow: /`;
   crawling it is a knowing, operator-authorized exception — personal use,
   no login, honest UA, low volume. This exception applies to NOTHING else:
   every other source keeps the full robots discipline (SEEK `*/job/`,
   Robert Walters query facets, ATS hosts, everything). It is implemented
   as an ALLOWLIST OF ONE (`LinkedIn Jobs` / parser `linkedin-guest`) in
   `crawler/adapters/scrapling/scrapling_worker.py` +
   `backend/src/Application/Crawling/RobotsExemptionPolicy.cs`; never add a
   second name and never turn it into a config flag. LinkedIn hard limits
   that remain: honest UA only
   (`Mozilla/5.0 (compatible; nzjobs-linkedin-guest/1.0)`), no browser
   impersonation, no fingerprint forging, no challenge solving, no cookies,
   no credentials, no login; one search page + at most ten detail fetches
   per run at >= 1.5 s spacing. A block (429/999/challenge/repeated 403)
   means back off and report/disable — never escalate, rotate UAs or retry
   harder.
2. **Never delete or recreate the `postgres-data` Docker volume.** Reversible
   EF migrations only. Source rows are never hard-deleted by features
   (retention purge and dedup have their own audited rules).
3. **Never commit `.env`** (gitignored; contains secrets). `.env.example`
   is the template. `HEALTH_ACCESS_PASSWORD` gates `/api/admin/health`
   (fail-closed: unset = always 401).
4. **Never fabricate data.** No invented history, no "unique vacancy" counts
   without a validated dedup pass, no annualizing hourly rates silently.
   Metric definitions live centrally in `Application/Metrics` — posted vs
   discovered dates, salary provenance/coverage, comparable-period rules.
   The product brief in `nz-jobs-intel-improvement-brief.md` (repo root,
   untracked) is the authority on definitions.
5. **On a Pi:** build services SERIALLY (`docker compose build worker` then
   `backend`, then `frontend`). Parallel builds OOM the host.

## How to work here

- **Verify with live evidence, not assumptions**: `curl :5050/api/...`,
  `docker compose logs worker`, `docker compose exec postgres psql -U
  postgres -d nz_job_market -c '...'`. The operator expects verified results
  (real curls, real crawl runs), never "config updated".
- **After code changes**: `dotnet build NzJobMarketDashboard.sln` +
  `dotnet test` (520+ tests), `python3 -m unittest discover -s
  crawler/tests` (175), `cd frontend && npm test && npm run build` (49).
  Contract tests assert API payload shapes the frontend reads — extend them
  when you change a DTO the UI consumes.
- **Rebuild to deploy**: registry/validator/scheduler behavior lives IN the
  images. `docker compose up -d --build`, then
  `HEALTH_ACCESS_PASSWORD=$(grep '^HEALTH_ACCESS_PASSWORD=' .env | cut -d= -f2-) ./scripts/verify-deployment.sh http://localhost:5050`
  must pass all checks (it includes health reconciliation and metric
  consistency checks).
- **Commit per logical unit**; conventional messages like the existing
  history (`Round N: ...`, `Phase N: ...`). Push requires the operator's
  GitHub credentials — ask.
- **Idempotency**: startup repair passes (JobQualityGuard, SkillBackfill,
  SalaryAudit, employer match, dedup) must be safe to re-run. Test that.
- **docs/* is gitignored** — plans/reports stay local on purpose.

## Current state (end of Round 14, Oct 2026)

- 59 tracked sources, ~43 yielding; states visible at `/api/admin/health`
  (password-gated). Disabled sources carry documented reasons in
  `sites.json` notes; removing a source from sites.json retires it
  (reversible — re-adding revives it).
- Data quality: canonical City/Region locations, 272-skill taxonomy
  extraction (`crawler/skills_taxonomy.json`, shared Python+.NET),
  salary provenance + questionable-parse flagging (`SalaryAuditService`,
  `POST /api/admin/salaries/audit`), canonical employers + alias admin API,
  conservative dedup v1 (strong-evidence merges only, 4-cap, review
  candidates for anything weaker).
- Daily listing snapshots (`listing_snapshots`) feed historical trends;
  they survive the 20-day job retention purge (`CRAWLER_RETENTION_DAYS`).
- Monitoring: `scripts/health-watcher.sh` (Telegram alerts on state
  changes, cron every 30 min on the host) and
  `scripts/verify-crawler-health.py` (state reconciliation).
- Known open items: historical trends need more accumulated snapshots;
  `RETENTION_DAYS=20` is hardcoded in the SPA (no endpoint exposes it);
  `/api/analytics/top-skills`, `/jobs-by-region`, `/freshness` are
  contract-tested but not consumed by the SPA (it aggregates the jobs feed
  client-side by design). Round 18: the LinkedIn guest-endpoint CLI is now a
  scheduled daily source under the scoped operator robots exemption above
  (`parser: linkedin-guest`, one search page + <=10 bounded details);
  the authenticated `mcp-server-linkedin` MCP path remains manual-only.

## Where things live

| Concern | Path |
| --- | --- |
| Source registry | `crawler/sources/sites.json` (fields documented in `crawler/README.md`) |
| Python crawler | `crawler/adapters/scrapling/scrapling_worker.py` |
| Skill taxonomy (single source of truth) | `crawler/skills_taxonomy.json` (read by Python AND .NET) |
| Metric definitions | `backend/src/Application/Metrics` |
| Content validation gate | `JobContentValidator` (C# + Python mirror) |
| Employer/dedup | `backend/src` — employers, duplicate_groups, admin endpoints |
| Snapshots/history | `ListingSnapshotService`, `GET /api/analytics/history` |
| Verify tooling | `scripts/verify-deployment.sh`, `scripts/verify-crawler-health.py`, `scripts/health-watcher.sh` |
| Firecrawl stack (separate compose) | `/var/www/html/firecrawl` on the host, API `:3002` |

## Starting a new work session

1. Read this file, then `README.md` (current status + API list).
2. Gather live evidence before planning: health endpoint, jobs feed,
   `git log --oneline -15` for recent work.
3. New multi-phase work: write a round brief to
   `docs/agent-plans/<date>-round<N>-<topic>.md`, execute phase by phase,
   append a section to the running report file, commit per phase.
4. End every session with: builds/tests green, stack rebuilt, verify
   script passing, and an honest list of what you could NOT do and why.
