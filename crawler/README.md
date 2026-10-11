# Crawler

The crawler is registry-driven. Every NZ IT job site we crawl is declared in
[`sources/sites.json`](sources/sites.json), and the Python worker picks the right
strategy from that registry. Scrapling (with a real browser) is the primary
runtime; public JSON APIs and server-rendered pages use a built-in `urllib`
fetcher so the worker still runs where Playwright is unavailable.

## Local Setup

```bash
python3.11 -m venv crawler/.venv
crawler/.venv/bin/python -m pip install -e crawler
```

Run the full test suite (no network, no browser required):

```bash
cd crawler && python3 -m unittest discover -s tests
```

Manual worker run:

```bash
crawler/.venv/bin/python crawler/adapters/scrapling/scrapling_worker.py \
  --source "Jade Software" \
  --url https://www.jadeworld.com/careers \
  --parser bamboohr \
  --fetcher-type http
```

The worker writes JSON containing `rawPages` and normalised `jobs` to stdout. The
.NET adapter keeps the configured source name as the job `source`.

## Running the crawler

The Python script crawls one URL; the **.NET worker** is the scheduler that loops
over every enabled source in `job_sources`. Run it from the repository root:

```bash
DATABASE_URL="Host=localhost;Port=55432;Database=nz_job_market;Username=postgres;Password=postgres" \
SCRAPLING_PYTHON_EXECUTABLE=python3 \
dotnet run --project backend/src/Worker
```

It applies migrations, reconciles `job_sources` with `sources/sites.json`, then
crawls each due source every `CRAWLER_POLL_SECONDS` (default 60), pacing sources
with `CRAWLER_DELAY_SECONDS` and capping pages with `CRAWLER_MAX_PAGES_PER_RUN`.
`SCRAPLING_WORKER_SCRIPT` is optional when the worker is started from the
repository root or from `backend/src/Worker` — both resolve the script path.

To crawl a single source immediately without waiting for the scheduler:

```bash
curl -s -X POST http://localhost:5050/api/admin/crawl-runs \
  -H 'Content-Type: application/json' \
  -d '{"source":"Hays NZ","url":"https://www.hays.net.nz/job-search?q=technology"}'
```

The source name selects the parser from the registry; an unregistered name still
runs but falls back to `generic`. Recent runs and their errors are at
`GET /api/admin/crawl-runs`; see the main `README.md` for the full
[Crawling new data](../../README.md#crawling-new-data) walkthrough.

To check many sources at once (no database writes), use the coverage tool:

```bash
python3 crawler/tools/coverage_report.py --report /tmp/coverage.json
```

## Crawl strategies

| Strategy | How it works | Examples |
| --- | --- | --- |
| `custom` | Hand-written, unit-tested HTML/feed parser | NZ Government Jobs, MPI (`jobtools`), Absolute IT, Talent Army, Beyond Recruitment, Consult, Comspek, Salt, Digital Garage, JobSeek, Hays (`job-search` + JSON-LD), ANZ/One NZ/LIC (`pageup` microdata), Trade Me Jobs, SEEK, Halter |
| `api` | Official public ATS JSON postings API | Jade Software (BambooHR), DOC Careers (Workday), Halter (Ashby), Xero (Ashby), Fisher & Paykel (Workday), EROAD (Workday), Pushpay (Greenhouse), Air New Zealand (SmartRecruiters), Inland Revenue (Oracle Recruiting), NZ Defence (Salesforce Apex), Adzuna, Tribe Recruitment (`/api/jobs`) |
| `jsonld` | Schema.org `JobPosting` JSON-LD on the listing/detail page | Gallagher |
| `rss` | Job RSS/XML feed listing every vacancy | Spark NZ, Fonterra (SuccessFactors `SearchJobs/feed`) |
| `dynamic` | Browser-render the listing, follow job links, extract JSON-LD → HTML heuristics | company career pages and government sites |
| `blocked` | Site returns 403/405/410/522 to bots; disabled until reviewed | Indeed, Jora, Datacom, Westpac, … |

Some sources need a dedicated parser because they use a common ATS without a JSON
API: **PageUp** boards (ANZ, One NZ, LIC) expose Schema.org *microdata*, handled by
the `pageup` parser, and BNZ's board is an **Eightfold** SPA rendered by the browser.

Rendering is what unlocks the JavaScript-only career sites: BNZ's board is an
Eightfold SPA (`nab.eightfold.ai/careers?microsite=bnz`), GradConnection, Recruit
IT, Serko, Potentia and Randstad all expose their postings only after the page
runs. In production Scrapling's browser does the rendering; locally the worker
falls back to a Chromium `--dump-dom` render, which is how the coverage numbers
below were measured.

Extraction order for every detail page is: **Schema.org JSON-LD → site parser →
generic HTML heuristics**. Results are filtered to IT/technology roles by
`looks_like_it_job`.

## Registry fields

```jsonc
{
  "name": "Jade Software",
  "tier": 4,
  "homepage": "https://www.jadeworld.com/careers",
  "jobs_url": "https://www.jadeworld.com/careers-at-jade",
  "platform": "bamboohr",
  "parser": "bamboohr",
  "strategy": "api",
  "fetcher": "http",
  "token": "jadeworld",          // public ATS board token
  "company": "Jade Software",
  "enabled": true,
  "frequency_minutes": 1440,
  "robotsAllows": true,
  "locationTerms": ["New Zealand", "Auckland", "Wellington"]  // optional region filter
}
```

A per-site `fetcher` (`http`, `fetcher`, `dynamic`, `stealth`, `browser`) overrides the
global `SCRAPLING_FETCHER_TYPE`, so static sites stay cheap while JavaScript-only
career pages get a browser. The worker degrades gracefully: Scrapling browser →
local Chromium/Chrome `--dump-dom` render → static HTTP. A `--fetcher-override`
flag forces a fetcher for local diagnostics.

`robotsAllows` records the probe result; the worker additionally enforces
`robots.txt` at runtime when `SCRAPLING_ROBOTS_TXT_OBEY=true`. Sites that
disallow crawling are shipped disabled pending a terms review.

Duplicate entries in the source list are consolidated: the "Trade Me" company
page points at the same `/c/jobs` board as "Trade Me Jobs", so it ships disabled
and that board is crawled once, last.

`priority: "last"` marks deprioritised sources (SEEK and Trade Me): they stay
enabled but the crawl scheduler orders every due run so they are crawled **after**
all other sources, and they are only crawled weekly. Trade Me is read through
JSON-LD detail pages with tracking queries stripped (its robots.txt disallows
query URLs), and SEEK is read from listing pages only (its robots.txt disallows
`*/job/` detail pages).

## Compliance and robustness

- `robots.py` implements Google-style `*`/`$` matching (the stdlib parser ignores
  wildcards and would crawl disallowed pages). Disallowed pagination URLs are
  skipped without failing the rest of the source.
- HTTP responses with unsupported encodings (brotli without a decoder) are
  retried with identity encoding instead of being parsed as text.
- Redirect responses are followed manually when urllib will not (HTML-escaped
  `Location`), and a redirect with **no** `Location` but a real body is treated as
  a normal response — Hays returns its search page that way.
- `429`/`503` responses honour `Retry-After` with exponential backoff for JSON
  APIs; page fetches use a bounded timeout so one slow site cannot stall a run.
- Hub/landing pages ("Careers - BNZ", "3 jobs found") and blog pages reached
  through job-looking links are filtered out of the job table.
- The IT keyword filter recognises enterprise platforms (Workday, Salesforce,
  ServiceNow, Kubernetes, Snowflake, …) without matching words like "sales".
  Generic/rendered/HTML-fed pages must carry the technology signal in the
  **title** (full-page descriptions are full of boilerplate that would
  over-match), and a bare "engineer" is not technology context — a civil or
  mechanical engineer no longer qualifies.
- `locationTerms` in the registry narrows global boards (e.g. ANZ's AU/NZ list)
  to New Zealand roles.

## Tools

| Tool | Purpose |
| --- | --- |
| `tools/probe_sources.py --deep` | Probe every site: HTTP status, JSON-LD, ATS fingerprints, sitemap job URLs, robots. Writes a report and (with `--write`) updates the registry. |
| `tools/discover_boards.py --scan-js` | Find the real job-board/ATS endpoint behind a careers landing page (scans links, iframes and same-host JS bundles). |
| `tools/discover_apis.py` | Look for a JSON job API behind a JavaScript-only site (probes `/api/...`, `wp-json`, `.json` endpoints and JS bundles). |
| `tools/render_boards.py` | Browser-render each source and find its real ATS board (Eightfold, Workday, PageUp, …) or same-host job links. |
| `tools/coverage_report.py` | Run the real worker against every source and report jobs found / failures. `--include-last` includes deprioritised sources. |
| `tools/generate_source_migration.py --write` | Render the EF Core seed migration for `job_sources` from the registry. `--check` fails if they drift. |

The API and worker also run a **registry sync at startup** (`SourceRegistrySynchronizer`):
`job_sources` is reconciled with `sites.json` (insert/update/disable) on every boot, so
adding or retuning a site is a registry edit plus a restart — no new migration needed.
The seed migration remains for a fresh database.

Re-run the probe when site markup changes:

```bash
python3 crawler/tools/probe_sources.py --deep --report /tmp/probe.json --write
python3 crawler/tools/generate_source_migration.py --write
```

## Adzuna credentials

The Adzuna aggregator needs a free API key. Set `ADZUNA_APP_ID` and
`ADZUNA_APP_KEY`; without them the Adzuna source fails cleanly and the worker
continues with the remaining sources.

## Adding a source

1. Add the site to `sources/sites.json` (or run the probe with `--write`).
2. If it needs a bespoke parser, add `is_job_link`/`parse_*_detail` branches in
   `adapters/scrapling/scrapling_worker.py` and unit tests in `tests/`.
3. Regenerate the migration so fresh databases match the registry:
   `python3 crawler/tools/generate_source_migration.py --write`.
4. Restart the API or worker. The startup synchroniser upserts `job_sources` from
   the registry (no migration needed on an existing database), and the worker
   starts crawling the new source when it is due.
