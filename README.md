# NZ Job Market Intelligence Dashboard

Analytics dashboard for New Zealand job-market trends using live, normalized job listings. NZ Government Jobs and Halter crawling are active; AI extraction and authentication are not implemented.

## Stack

- Frontend: React, TypeScript, Vite, Tailwind CSS, Recharts, TanStack Query, TanStack Table, Leaflet-ready structure
- Backend: .NET 9 Web API, EF Core, PostgreSQL
- Crawler: Python Scrapling worker, with Firecrawl plumbing available as optional fallback

## Local Development

```bash
cp .env.example .env
python3.11 -m venv crawler/.venv
crawler/.venv/bin/python -m pip install -e crawler
docker compose up -d postgres
cd backend/src/Api
dotnet ef database update --project ../Infrastructure/Infrastructure.csproj
dotnet run --urls http://localhost:5050
```

In another terminal:

```bash
cd frontend
npm install
npm run dev
```

Open `http://localhost:5173`.

## Useful Commands

```bash
dotnet build NzJobMarketDashboard.sln
dotnet test NzJobMarketDashboard.sln
cd frontend && npm run build
docker compose up --build
```

## Implemented API

```http
GET /api/jobs
GET /api/jobs/{id}
GET /api/analytics/overview
GET /api/analytics/top-skills
GET /api/analytics/jobs-by-region
POST /api/admin/crawl-runs
GET /api/admin/crawl-runs
GET /api/admin/crawl-runs/{id}
```

## Current Scope

Implemented: monorepo foundation, PostgreSQL schema and migrations, live-job lifecycle, API endpoints, frontend overview dashboard, scheduled Scrapling crawling, NZ Government Jobs parsing, and optional Firecrawl plumbing.

Current crawler direction: the primary crawler is the Python Scrapling worker. Firecrawl remains disabled until `FIRECRAWL_API_KEY` is set and should be treated as fallback plumbing, not the main crawler path. The manual endpoint accepts:

```json
{
  "source": "Scrapling",
  "url": "https://example.com/job-page"
}
```

Before using `source: "Scrapling"` locally, install the crawler dependency:

```bash
python3.11 -m venv crawler/.venv
crawler/.venv/bin/python -m pip install -e crawler
```

Next: expand to eight approved priority sources following `docs/09-priority-source-expansion-spec.md`. Deferred: AI extraction, authentication, and deployment.

## Scheduled Crawling

See `docs/crawling-spec.md`. Add an approved source to `job_sources` with `method = 'Scrapling'`, `parser = 'generic'`, a listing URL in `base_url`, a completed `terms_checked_at`, and `enabled = true`. The worker processes due sources sequentially using `CRAWLER_DELAY_SECONDS`; Scrapling waits `SCRAPLING_DOWNLOAD_DELAY_SECONDS` between pages and stops at `CRAWLER_MAX_PAGES_PER_RUN`.
