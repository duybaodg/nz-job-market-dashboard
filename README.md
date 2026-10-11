# NZ Job Market Intelligence Dashboard

Analytics dashboard for New Zealand job-market trends using live, normalized job listings. Crawling is registry-driven and covers the full NZ IT job website list with per-site strategies (public ATS APIs, Schema.org JSON-LD, and browser-rendered HTML). AI extraction and authentication are not implemented.

## Stack

- Frontend: React, TypeScript, Vite, Tailwind CSS, Recharts, TanStack Query, TanStack Table, Leaflet-ready structure
- Backend: .NET 9 Web API, EF Core, PostgreSQL
- Crawler: Python Scrapling worker, with Firecrawl plumbing available as optional fallback

## Local Development

```bash
cp .env.example .env                 # DATABASE_URL, SCRAPLING_*, ADZUNA_* etc.
docker compose up -d postgres

# Python crawler dependency (needed to crawl locally; skip if you only use Docker)
python3.11 -m venv crawler/.venv
crawler/.venv/bin/python -m pip install -e crawler

# API — applies EF migrations and syncs job_sources from the registry on startup
cd backend/src/Api
dotnet run --urls http://localhost:5050
```

Frontend, in another terminal:

```bash
cd frontend
npm install
npm run dev
```

Open `http://localhost:5173`.

Three processes matter: **postgres**, the **API** (read/write endpoints), and the
**worker** (the scheduler that actually crawls). The API does not crawl; see
[Crawling new data](#crawling-new-data).

## Useful Commands

```bash
dotnet build NzJobMarketDashboard.sln
dotnet test NzJobMarketDashboard.sln
cd frontend && npm run build
docker compose up --build
```

## Deploying this version

The crawler registry, scheduler priority, HTTP hardening and the 2/7/15-day
windows live in the API/worker images, so the stack must be rebuilt for them to
take effect:

```bash
docker compose up -d --build
./scripts/verify-deployment.sh http://localhost:5050
```

The verification script checks the API is up, that `job_sources` was synced from
the registry, that jobs are flowing, and that `/api/analytics/freshness` returns
the 2/7/15-day windows. On an older image those last two checks fail, which is
how you can tell a rebuild is still pending.

After the rebuild the worker reconciles `job_sources` with
`crawler/sources/sites.json` on startup and retires hub/landing-page records left
by earlier versions.

## Crawling new data

Crawling is done by the **worker** (`backend/src/Worker`), a .NET background
service that calls the Python `scrapling_worker.py` for each due source. There are
three ways to get new data in.

### 1. Scheduled crawling (normal operation)

Start the worker and leave it running:

```bash
# from the repository root
DATABASE_URL="Host=localhost;Port=55432;Database=nz_job_market;Username=postgres;Password=postgres" \
SCRAPLING_PYTHON_EXECUTABLE=python3 \
dotnet run --project backend/src/Worker
```

On startup it applies migrations, reconciles `job_sources` with
`crawler/sources/sites.json`, then loops: every `CRAWLER_POLL_SECONDS` (default 60)
it crawls each **enabled** source whose `last_crawled_at` is older than that
source's `crawl_frequency_minutes`. It logs one line per source, for example:

```
info: Worker.CrawlWorker[0] Crawl Completed for Hays NZ: Crawl run completed.
```

In Docker the worker is the `worker` service, so `docker compose up -d` starts it
alongside the API — no extra command is needed.

### 2. Crawl one source right now

Use the admin endpoint (the API must be running). Pass a **registered source name**
and the URL you want it to crawl — the source's configured parser is used
automatically:

```bash
# look up a source's URL and parser
curl -s http://localhost:5050/api/admin/sources | python3 -m json.tool

curl -s -X POST http://localhost:5050/api/admin/crawl-runs \
  -H 'Content-Type: application/json' \
  -d '{"source":"Xero","url":"https://api.ashbyhq.com/posting-api/job-board/xero"}'
```

The response reports `totalPages`, `totalJobsFound` and `totalJobsSaved`:

```json
{"crawlRun":{"source":"Xero","status":"Completed","totalJobsFound":11,"totalJobsSaved":0},
 "started":true,"message":"Crawl run completed."}
```

`totalJobsSaved` is `0` when those jobs already exist — deduplication by content
hash, source job id and URL means re-crawling is safe. An **unknown** source name
still runs, but falls back to the `generic` parser and stores jobs under that
literal name, so prefer a registered name.

### 3. Add or retune a source

1. Edit `crawler/sources/sites.json` (see `crawler/README.md` for the fields), for
   example set `"enabled": true`, a `jobs_url`, a `parser` and optionally
   `listingUrls` or `locationTerms`.
2. Restart the API or worker. The startup synchroniser upserts `job_sources`
   (and disables entries removed from the registry), so no migration is needed.
3. Optionally regenerate the seed migration for fresh databases:
   `python3 crawler/tools/generate_source_migration.py --write`.

To force a full refresh on the next cycle, clear the timestamps:

```bash
docker compose exec postgres psql -U postgres -d nz_job_market \
  -c 'UPDATE job_sources SET last_crawled_at = NULL;'
```

### Checking what happened

```bash
curl -s http://localhost:5050/api/admin/crawl-runs | python3 -m json.tool   # recent runs + errors
curl -s http://localhost:5050/api/admin/sources   | python3 -m json.tool   # enabled/disabled, parser, frequency
./scripts/verify-deployment.sh http://localhost:5050                       # end-to-end smoke check
```

Job-level results appear in the dashboard and via `GET /api/jobs`. `/api/jobs`
only returns jobs, not crawl errors, so check `/api/admin/crawl-runs` when a
source is unexpectedly empty.

### Running the Python crawler directly

For debugging one site without the .NET worker:

```bash
python3 crawler/adapters/scrapling/scrapling_worker.py \
  --source "Xero" --url "https://api.ashbyhq.com/posting-api/job-board/xero" \
  --parser ashby --fetcher-type http
```

It prints `{"rawPages": [...], "jobs": [...]}` to stdout. Add
`--fetcher-override browser` to force JavaScript rendering, or `--robots-txt-obey`
to honour `robots.txt`. Batch tools live in `crawler/tools/` — `coverage_report.py`
runs the real worker against many sources and reports which ones yield jobs.

## Implemented API

```http
GET /api/jobs                       # ?status=&region=&skill=&search=&days=2|7|15
GET /api/jobs/{id}
GET /api/analytics/overview
GET /api/analytics/top-skills
GET /api/analytics/jobs-by-region
GET /api/analytics/freshness         # active-job counts for the 2, 7 and 15 day windows
POST /api/admin/crawl-runs
GET /api/admin/crawl-runs
GET /api/admin/crawl-runs/{id}
GET /api/admin/sources
```

## Job freshness windows (2 / 7 / 15 days)

Posting recency is a first-class dimension:

- The dashboard's **Posted within** filter offers 2, 7 and 15 days, using
  `posted_date` and falling back to `first_seen_at` when a source omits it.
- `GET /api/jobs?days=N` applies the same window server-side.
- `GET /api/analytics/freshness` returns active-job counts for all three windows,
  rendered on the dashboard as the **Posting freshness** panel.
- The lifecycle uses the same horizon: a job not seen for
  `CRAWLER_JOB_STALE_DAYS` (default **15**) is expired even if the source never
  marks it closed, and a job missing from `CRAWLER_MISSING_CRAWL_THRESHOLD`
  consecutive complete crawls (default 2) is expired sooner.
- Hub/landing-page records left behind by older worker versions are deactivated
  by `JobQualityGuard` at worker startup.

## Current Scope

Implemented: monorepo foundation, PostgreSQL schema and migrations, live-job lifecycle, API endpoints, frontend overview dashboard, scheduled registry-driven crawling of 67 NZ IT job sites, and optional Firecrawl plumbing.

Crawling direction: the primary crawler is the Python Scrapling worker, driven by
`crawler/sources/sites.json`. Each source declares a strategy (`api`, `rss`,
`jsonld`, `dynamic`, `custom`, `generic` or `blocked`) and the worker resolves it
by source name, so the .NET adapter only needs the name and URL. Public ATS JSON
APIs (Workday, Ashby, BambooHR, Greenhouse, SmartRecruiters, Oracle Recruiting,
Salesforce Apex, Tribe, Adzuna) and SuccessFactors RSS feeds are crawled without a
browser; everything else is rendered and parsed with JSON-LD-first extraction.
Firecrawl remains disabled until `FIRECRAWL_API_KEY` is set and is fallback
plumbing, not the main crawler path.

See `crawler/README.md` for the registry format, strategy table, probe tooling and
how to add a source.

## Scheduler settings

| Variable | Default | Purpose |
| --- | --- | --- |
| `CRAWLER_POLL_SECONDS` | 60 | How often the worker looks for due sources |
| `CRAWLER_DELAY_SECONDS` | 30 | Pause between consecutive sources (`5` in `.env.example`) |
| `CRAWLER_MAX_PAGES_PER_RUN` | 50 | Page cap per source run |
| `SCRAPLING_DOWNLOAD_DELAY_SECONDS` | 5 | Pause between page fetches |
| `SCRAPLING_ROBOTS_TXT_OBEY` | true | Enforce `robots.txt` while crawling |
| `SCRAPLING_FETCHER_TYPE` | fetcher | Global fetcher; per-source `fetcher` overrides it |
| `CRAWLER_JOB_STALE_DAYS` | 15 | Expire jobs not seen for this many days |
| `CRAWLER_MISSING_CRAWL_THRESHOLD` | 2 | Expire after N complete crawls without the job |
| `CRAWLER_RETENTION_DAYS` | 20 | Hard-delete jobs and crawl history older than this many days |
| `ADZUNA_APP_ID` / `ADZUNA_APP_KEY` | – | Optional, required only for the Adzuna source |

Per-source scheduling comes from each row's `crawl_frequency_minutes`.

SEEK and Trade Me are deprioritised (`priority: "last"`, weekly). The crawl
scheduler reads that field from the registry and orders every due run so those
two are crawled **after** all other sources — verified end to end — and the other
52 enabled sources are refreshed first.

Only sources whose `terms_checked_at` is set are crawled. Sites that return 403/405/410/522
or whose `robots.txt` disallows crawling ship disabled pending review.

See [Crawling new data](#crawling-new-data) for how to start the worker, trigger a
single crawl, and check results.

Deferred: AI extraction and authentication.
