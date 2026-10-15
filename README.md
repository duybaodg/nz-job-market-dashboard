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
curl -s http://localhost:5050/api/admin/health    | python3 -m json.tool   # health state per source
./scripts/verify-deployment.sh http://localhost:5050                       # end-to-end smoke check
```

The **Crawler Health** page (`/health`, linked from the sidebar) turns the health
endpoint into summary cards and a sortable, filterable per-source table with
degraded/stale sources first. It is read-only: it never triggers a crawl or a
write.

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
GET /api/analytics/freshness         # posted + first-seen counts for the 2, 7 and 15 NZ-day windows
GET /api/analytics/history           # ?days=7|15|30 daily-snapshot history, weekly trend, coverage changes
POST /api/admin/crawl-runs
GET /api/admin/crawl-runs
GET /api/admin/crawl-runs/{id}
GET /api/admin/sources
GET /api/admin/health                  # per-source crawler health (30s in-memory cache, read-only)
GET /api/admin/employers               # canonical employers + aliases + job counts + unmatched names
POST /api/admin/employers              # { canonicalName, type, notes, actor } create a canonical
POST /api/admin/employers/{id}/aliases # { alias, actor } add an alias and rematch immediately
DELETE /api/admin/employers/{id}/aliases/{alias}
POST /api/admin/employers/seed         # one-off bootstrap from distinct company values (idempotent)
POST /api/admin/employers/match        # repair pass: re-resolve every job by exact/alias match
GET /api/admin/duplicates              # duplicate groups + review candidates (provenance)
POST /api/admin/dedup/run              # run the incremental, validated dedup pass now
```

## Job freshness windows (2 / 7 / 15 days)

Posting recency is a first-class dimension. The central `Application/Metrics`
module defines every period, count and salary calculation so cards, charts and
the API cannot silently diverge:

- Reporting intervals are **complete Pacific/Auckland local days**, inclusive
  start / exclusive end, so daylight-saving transitions are handled explicitly.
- **Newly posted** counts use `posted_date` only (a missing posted date is never
  substituted); **newly discovered** counts use `first_seen_at`. The dashboard
  and `GET /api/analytics/freshness` show both bases with an equal-duration
  previous-period comparison, and declare "comparison unavailable" when the
  prior period predates the 20-day retention window.
- `GET /api/jobs?days=N` keeps the convenience posted-within filter, using
  `posted_date` and falling back to `first_seen_at`; each result row labels which
  basis it used.
- `GET /api/analytics/overview` returns the active cohort, posted-date coverage,
  salary median with sample/coverage/exclusion metadata, identifiable-employer
  coverage and a fresh/stale/partial/unavailable data status.
- Advertised salary parsing keeps the raw text, currency and pay period
  (`salary_text`, `salary_currency`, `salary_period`) and flags questionable
  parses. Hourly/daily and non-NZD values are never annualised or mixed into the
  annual NZD median; groups below 5 comparable observations are suppressed.
- The lifecycle uses the same horizon: a job not seen for
  `CRAWLER_JOB_STALE_DAYS` (default **15**) is expired even if the source never
  marks it closed, and a job missing from `CRAWLER_MISSING_CRAWL_THRESHOLD`
  consecutive complete crawls (default 2) is expired sooner.
- Hub/landing-page records left behind by older worker versions are deactivated
  by `JobQualityGuard` at worker startup.

## Audience views (Find jobs / Market trends)

The frontend has two audience views, both driven by URL state so refresh,
copy-link and browser Back/Forward reproduce the exact view:

- **Find jobs (`/jobs`, default):** compact title + shared freshness indicator,
  primary filters (search, location, work mode, seniority, salary), matching
  count with selected-filter chips and Clear all, responsive job cards with
  sorting and pagination, then compact market context. Each card exposes the
  linked title; the hiring employer **and** the advertiser/recruiter separately;
  city/region/nationwide plus work mode; the advertised salary with its own
  currency and pay period or “Salary not advertised”; employment type and
  seniority; skill tags with a details overflow; the posted date or an explicit
  “First seen” label; and the closing date. Company, source, employment type,
  posted-date window, skill, role family, experience and programming language
  live under **More filters**. Pagination resets whenever a filter changes.
- **Market trends (`/trends`):** scope, reporting period, comparison period and
  freshness; four primary metrics (unique vacancies once a validated dedup pass
  exists, otherwise active tracked listings; new listings with an equal-period
  comparison; median advertised salary with sample and coverage; and hiring
  employers counted by admin-reviewed canonical employer, separated from
  agency-only/unknown listings); weekly new-listing trend; active-listing
  history; demand by role family (with seniority drill-down), region and skill;
  salary distribution and median salary by role family; and employer activity.
  Every chart has a visible table or ranked-bar alternative and can be operated
  by keyboard; chart bars apply a visible filter and link through to the matching
  Find-jobs results.

Advertiser vs hiring employer and role family are classified centrally in
`Application/Employers/EmployerClassifier` and
`Application/Jobs/RoleFamilyClassifier` and returned on every `JobDto`, so the
cards, charts and CSV export cannot diverge. A reviewed agency alias table keeps
agency-only listings from inventing the agency's client. On Find jobs every
source row is kept and cross-source duplicates are badged **“Also on …”**; the
canonical employer name is also returned on every job.

## Employer identity and listing deduplication (Round 8)

Both features are conservative and auditable: every decision is a data row that
can be reviewed, and no source row is ever deleted.

### Canonical employers (admin-reviewed)

`employers` + `employer_aliases` + `employer_audit_events` hold the reviewed
canonical employer model. Jobs gain a nullable `employer_canonical_id`; the raw
source `company` text is preserved. Matching is **exact after case/whitespace
normalisation or through a curated alias** — there is deliberately no fuzzy
merge, so names such as “Fisher Paykel” and “Fisher & Paykel” stay separate until
an admin adds an alias. Unmatched names create nothing and stay in the unknown
bucket.

The worker bootstraps canonicals once from the distinct source company values
(reviewed agencies are typed `Agency`, the rest `DirectEmployer`) and then runs
the match repair pass. `POST /api/admin/employers/{id}/aliases` adds an alias and
immediately rematches affected jobs; removing an alias returns them to the
unknown bucket. `GET /api/admin/employers` returns canonicals with aliases, live
job counts, and the distinct unmatched names sorted by count. Canonical employer
stats are surfaced on `GET /api/analytics/overview`.

### Listing deduplication v1

`duplicate_groups`, `duplicate_review_candidates` and `deduplication_runs` hold
the soft grouping (jobs gain a nullable `duplicate_group_id`). Groups are keyed
by normalised title + canonical employer and merged only on strong evidence:

1. Same source + same source job id (handled at save time).
2. Cross-source: identical normalised title, identical canonical employer,
   identical salary text (both present or both absent) and representative dates
   within 7 days. When the salary is absent on both sides the description
   similarity must also be ≥ 0.8 (the guard against legitimate twin openings).
3. Cross-source: same title + employer + location city, overlapping posting
   window and shingled-token description Jaccard similarity ≥ 0.8.

Anything weaker becomes a **review candidate** (title+employer matches that fail
the gates, salary conflicts, or clusters larger than the **4-listing cap**)
rather than a merge. Conflicting present salaries never merge. The canonical is
the earliest `first_seen_at`, preferring a listing with a posted date; the pass
is incremental and re-points the canonical when it expires. A `deduplication_run`
records whether the pass passed integrity validation, and **unique-vacancy counts
are only published once at least one validated pass exists** — `GET
/api/analytics/overview` returns both `activeListings` and `uniqueVacancies`
(plus the listing/merged/group/review-candidate summary), and
`GET /api/admin/duplicates` shows exactly which listings merged into which group.

## Daily snapshots and historical analysis

`ListingSnapshotService` writes one listing-level snapshot per Pacific/Auckland
day (`listing_snapshots` + `listing_snapshot_runs`, added by the reversible
`AddListingSnapshots` migration). This happens once at worker startup and on
every poll, and is idempotent per NZ day. Snapshot rows are self-contained and
are **not** deleted by the 20-day job retention purge, so history survives even
after the underlying job row is gone.

`GET /api/analytics/history?days=7|15|30` reads only those snapshots:

- **Weekly new listings** are grouped into Monday–Sunday NZ weeks by first-seen
  date. Weeks before snapshot coverage starts are omitted (they would be
  biased low), the first partial week and the current week are labelled
  incomplete, and weeks with missing snapshot days are flagged.
- **Active counts** are per snapshot day, with source-coverage changes
  annotated so an added or removed source cannot silently appear as demand
  growth. The active change compares the latest snapshot with the snapshot
  exactly seven days earlier, or declares the comparison unavailable.
- **New-listing comparisons** use equal-duration complete-day intervals and are
  declared unavailable when the previous period predates retained snapshots.

The Market trends page labels all of this; where daily snapshots are not yet
retained it falls back to today's retained rows and says so explicitly.
Filtered CSV export on Find jobs writes the applied filters, period, metric
definitions and extraction timestamp and reuses the same screen calculations.

## Skill extraction (shared taxonomy)
Technical skills are extracted from the **full** job description (languages,
frameworks, cloud, databases, data/BI, DevOps, testing, security,
integration/ERP and process keywords), not just a programming-language list.

- The vocabulary is a single data file: **`crawler/skills_taxonomy.json`**
  (272 skills across 10 skill types). It is read by both the Python scrapling
  worker (`crawler/skills_taxonomy.py`) and the .NET ingestion pipeline
  (`backend/src/Application/Jobs/SkillsTaxonomy.cs` + `SkillExtractor.cs`).
- Matching is case-insensitive alias matching with word boundaries. Every skill
  can declare `aliases`, `deny` patterns and a `contextAny` window to suppress
  false positives (`R` in "R&D", `Go` in "go to market", marketing "angular
  design", etc.). Results are normalised to canonical names and capped at **15
  per job**.
- `SkillBackfillService` runs once at worker startup to re-extract skills for
  every stored job from its title, summary and retained raw-page markdown. It is
  idempotent and unions with existing valid skills so a weaker stored signal
  never destroys data.
- `GET /api/analytics/top-skills` returns the top 15 by distinct active job
  across every skill type.

## Current Scope

Implemented: monorepo foundation, PostgreSQL schema and migrations, live-job lifecycle, API endpoints, the Find jobs and Market trends audience views, daily listing snapshots with retained-history analytics, scheduled registry-driven crawling of 67 NZ IT job sites, and optional Firecrawl plumbing.

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
