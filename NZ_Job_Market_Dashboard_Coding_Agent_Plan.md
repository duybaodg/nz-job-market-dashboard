# NZ Job Market Intelligence Dashboard
## Coding-Agent Implementation Plan

> Purpose: This document is designed to be given directly to a coding agent or used as a technical implementation guide for building the project.

---

# 1. Project Summary

Build a **New Zealand Job Market Intelligence Dashboard** that collects job data from multiple job sources, normalises the data, extracts skills and insights using AI, stores the results in PostgreSQL, and presents market trends in a React dashboard.

This project should not simply copy job boards. The main value is **analytics**, including:

- Which skills are most in demand
- Which regions have the most jobs
- Salary trends
- Graduate/junior role opportunities
- Company hiring trends
- Technology trends
- AI-generated weekly job market insights

---

# 2. Recommended Main Solution

Use a **hybrid API + crawler architecture**.

Different job sources should use different collection methods depending on availability and reliability.

```text
Trade Me Jobs API
SEEK careful metadata crawler
Kiwi Health Jobs crawler
Education Gazette crawler
MBIE public trend data
        ↓
Source adapters
        ↓
Raw data storage
        ↓
Normalisation pipeline
        ↓
AI extraction pipeline
        ↓
PostgreSQL analytics database
        ↓
.NET Web API
        ↓
React dashboard
```

## Why this solution is recommended

A single crawling method is not flexible enough because every job website has different rules, structure, pagination, anti-bot behaviour, and available data.

The hybrid approach gives the best balance:

- Use APIs where available
- Use Scrapling as the primary self-hosted crawler
- Keep Firecrawl as an optional fallback for quick experiments
- Store raw data first so extraction can be re-run later
- Normalise all data into one common schema
- Use AI only after collecting clean text
- Keep dashboard logic independent from crawler logic

---

# 3. Alternative Solutions

The coding agent should understand that this project can support multiple collection strategies.

## Option A: API-first

Use official APIs where available.

```text
Trade Me API → Normaliser → PostgreSQL → Dashboard
```

Best for:

- Reliability
- Legal safety
- Structured data

Limitations:

- Not all job sites provide public APIs
- API access may require approval
- API data may be limited

---

## Option B: Scrapling self-hosted crawler

Use Scrapling as a Python crawler service for job source pages.

```text
Scrapling fetchers/spiders → Raw page storage → Normaliser → PostgreSQL → Dashboard
```

Best for:

- Self-hosted crawling control
- Static, dynamic, and stealth browser-backed fetching
- Spider-based concurrent crawls
- Adaptive selectors when page structure changes
- Robots.txt-aware crawling when enabled

Limitations:

- Requires Python worker runtime
- Requires crawler maintenance
- Browser-backed modes need more CPU/RAM than plain HTTP

---

## Option C: Firecrawl Cloud fallback

Use Firecrawl API to scrape or crawl job pages and return Markdown/JSON.

```text
Firecrawl Cloud → AI extraction → PostgreSQL → Dashboard
```

Best for:

- Fast MVP
- Reducing scraping code
- AI-friendly text extraction

Limitations:

- Credit/API cost
- Vendor dependency
- May become expensive at scale

---

## Option D: Crawl4AI self-hosted

Use Crawl4AI on a VPS or Docker server.

```text
Crawl4AI → Markdown extraction → AI parser → PostgreSQL
```

Best for:

- Lower long-term cost
- Open-source control
- AI/RAG-friendly crawling

Limitations:

- Requires hosting
- Requires more DevOps work
- Crawler maintenance is your responsibility

---

## Option E: Playwright custom crawler

Use Playwright to control browser behaviour and extract HTML/text manually.

```text
Playwright → HTML parser → Normaliser → PostgreSQL
```

Best for:

- Maximum control
- JavaScript-heavy sites
- Learning and portfolio value

Limitations:

- More code
- More maintenance
- Site layout changes can break selectors

---

## Option F: Apify

Use Apify Actors for managed scraping.

```text
Apify Actor → Webhook/API → PostgreSQL → Dashboard
```

Best for:

- Ready-made scrapers
- Scheduling
- Proxy support

Limitations:

- Paid at scale
- Less control than custom crawling
- May still need custom normalisation

---

# 4. Final Implementation Strategy

Use this staged approach:

## Stage 1: MVP

Use:

- React + TypeScript + Vite
- Tailwind CSS
- shadcn/ui
- Recharts
- TanStack Table
- TanStack Query
- .NET Web API
- PostgreSQL
- Scrapling Python worker for crawling
- Firecrawl Cloud optional fallback
- OpenAI or Gemini for extraction
- Docker Compose for local development

## Stage 2: Hybrid Expansion

Add:

- Trade Me Jobs API connector
- Scrapling source spiders for SEEK, Kiwi Health Jobs, and Education Gazette
- Scrapling dynamic/stealth fetchers for difficult pages
- MBIE trend data ingestion
- Background worker scheduling

## Stage 3: Production-Ready Platform

Add:

- User accounts
- Saved searches
- Alerts
- CV upload
- Skill gap analysis
- Job recommendations
- Monitoring
- Error reporting
- Admin crawler dashboard

---

# 5. Technology Stack

## Frontend

Use:

- React
- TypeScript
- Vite
- Tailwind CSS
- shadcn/ui
- Recharts
- TanStack Table
- TanStack Query
- React Hook Form
- Zod
- Leaflet or Mapbox for NZ map

Reason:

- Fast development
- Great dashboard ecosystem
- Clean UI
- Good portfolio value

---

## Backend

Use:

- .NET 8 or .NET 9 Web API
- Entity Framework Core
- PostgreSQL
- BackgroundService or Hangfire
- REST API

Reason:

- Matches target .NET developer roles
- Strong typed backend
- Good for APIs and background jobs
- Strong portfolio relevance

---

## Crawler / Worker

Start with:

- Scrapling Python worker
- Scrapling Fetcher for simple HTTP pages
- Scrapling DynamicFetcher or StealthyFetcher for JavaScript-heavy or protected pages

Later add:

- Firecrawl fallback adapter if hosted extraction is useful
- Proxy/session configuration
- Scheduled spider orchestration

Reason:

- Scrapling gives long-term control without a hosted scraping dependency
- Scrapling supports requests, browser automation, sessions, spiders, and adaptive selectors in one framework
- Python crawler code should stay separate from the .NET API

---

## AI Layer

Use:

- OpenAI API or Gemini API

AI should extract:

- Skills
- Seniority
- Salary range
- Employment type
- Industry
- Remote/hybrid/on-site
- Summary
- Required experience

---

# 6. System Architecture

```text
frontend/
  React dashboard

backend/
  .NET Web API
  Authentication
  Analytics endpoints

worker/
  Scheduled crawling jobs
  AI extraction jobs
  Data normalisation jobs

database/
  PostgreSQL

crawler/
  Source adapters
  Scrapling fetchers
  Scrapling spiders
  Firecrawl fallback client

ai/
  Prompt templates
  JSON extraction
  Skill classification
```

---

# 7. Suggested Repository Structure

```text
nz-job-market-dashboard/

├── README.md
├── docker-compose.yml
├── .env.example
│
├── backend/
│   ├── src/
│   │   ├── Api/
│   │   ├── Application/
│   │   ├── Domain/
│   │   ├── Infrastructure/
│   │   └── Worker/
│   └── tests/
│
├── frontend/
│   ├── src/
│   │   ├── components/
│   │   ├── pages/
│   │   ├── features/
│   │   ├── hooks/
│   │   ├── lib/
│   │   ├── services/
│   │   └── types/
│   └── tests/
│
├── crawler/
│   ├── adapters/
│   │   ├── scrapling/
│   │   ├── firecrawl/
│   │   ├── trademe/
│   │   ├── seek/
│   │   ├── kiwi-health-jobs/
│   │   └── education-gazette/
│   ├── normalizers/
│   └── README.md
│
├── docs/
│   ├── 01-project-overview.md
│   ├── 02-architecture.md
│   ├── 03-database-design.md
│   ├── 04-api-design.md
│   ├── 05-crawler-design.md
│   ├── 06-ai-extraction.md
│   └── 07-roadmap.md
│
└── scripts/
    ├── seed-data/
    └── maintenance/
```

---

# 8. Data Collection Design

## Core principle

Each job source should have its own adapter.

Every adapter must return the same internal DTO.

```typescript
type RawJobInput = {
  source: string;
  sourceJobId?: string;
  title: string;
  company?: string;
  location?: string;
  salaryText?: string;
  descriptionText?: string;
  url: string;
  postedDate?: string;
  closingDate?: string;
};
```

## Source adapters

Create adapters:

```text
IJobSourceAdapter
  ├── TradeMeJobAdapter
  ├── SeekJobAdapter
  ├── KiwiHealthJobsAdapter
  ├── EducationGazetteAdapter
  ├── ScraplingAdapter
  ├── FirecrawlAdapter
  └── MbIeTrendDataAdapter
```

## Adapter interface

```csharp
public interface IJobSourceAdapter
{
    string SourceName { get; }

    Task<IReadOnlyList<RawJobInput>> FetchJobsAsync(
        JobSearchRequest request,
        CancellationToken cancellationToken);
}
```

---

# 9. Crawling Rules

The crawler must:

- Respect robots.txt where applicable
- Use low request rate
- Avoid aggressive crawling
- Avoid copying full job board content for public display
- Store original source URL
- Deduplicate listings
- Keep source-specific raw data for debugging
- Log crawl success/failure
- Support retry with backoff
- Support per-source enable/disable flags
- Use Scrapling robots.txt support for crawled sources where applicable
- Prefer Scrapling Fetcher first, then DynamicFetcher/StealthyFetcher only when needed

---

# 10. Scrapling MVP Flow

```text
1. User/admin defines search source URL
2. .NET API creates crawl run
3. Python Scrapling worker picks up or receives crawl request
4. Scrapling Fetcher/Spider fetches search and detail pages
5. Worker stores raw HTML/text/metadata
6. Worker converts pages into RawJobInput
7. Normalisation and deduplication save jobs to PostgreSQL
8. AI extraction enriches saved jobs later
```

## Scrapling worker responsibilities

- Accept a source URL
- Choose Fetcher, DynamicFetcher, or StealthyFetcher based on source config
- Respect per-source request delay and robots.txt setting
- Parse returned HTML/text with CSS/XPath selectors
- Extract job cards or job detail content
- Return `RawJobInput`
- Store crawl metadata
- Emit crawl-run stats and errors back to the API/database

---

# 11. Normalisation Pipeline

The normaliser converts messy data into consistent fields.

## Input

```json
{
  "title": "Junior Software Developer",
  "company": "ABC Ltd",
  "location": "Wellington",
  "salaryText": "$65,000 - $80,000",
  "descriptionText": "We are looking for a junior developer with React and SQL...",
  "url": "https://example.com/job/123"
}
```

## Output

```json
{
  "title": "Junior Software Developer",
  "company": "ABC Ltd",
  "region": "Wellington",
  "salaryMin": 65000,
  "salaryMax": 80000,
  "seniority": "Junior",
  "employmentType": "Full-time",
  "workMode": "Hybrid",
  "skills": ["React", "SQL"],
  "url": "https://example.com/job/123"
}
```

---

# 12. AI Extraction Design

## AI extraction prompt goal

Extract structured job information from job text.

## Required AI output schema

```json
{
  "title": "string",
  "company": "string",
  "location": "string",
  "region": "string",
  "salaryMin": 0,
  "salaryMax": 0,
  "employmentType": "Full-time | Part-time | Contract | Internship | Unknown",
  "seniority": "Graduate | Junior | Intermediate | Senior | Lead | Unknown",
  "workMode": "Remote | Hybrid | On-site | Unknown",
  "industry": "string",
  "skills": ["string"],
  "tools": ["string"],
  "programmingLanguages": ["string"],
  "cloudPlatforms": ["string"],
  "databases": ["string"],
  "summary": "string"
}
```

## AI extraction rules

- Return valid JSON only
- Do not invent salary if unavailable
- Use `Unknown` when unclear
- Extract skills only if explicitly mentioned or strongly implied
- Keep skill names standardised, for example:
  - `.NET`
  - `C#`
  - `React`
  - `TypeScript`
  - `SQL`
  - `PostgreSQL`
  - `AWS`
  - `Azure`

---

# 13. Deduplication Strategy

Use a hash to avoid duplicate jobs.

## Hash fields

```text
source + title + company + location + url
```

If URL is unavailable, use:

```text
title + company + location + posted_date
```

## Duplicate rules

- Same source and same source_job_id = duplicate
- Same canonical URL = duplicate
- Same title/company/location within 7 days = possible duplicate

---

# 14. Database Design

## jobs

```sql
CREATE TABLE jobs (
    id UUID PRIMARY KEY,
    source TEXT NOT NULL,
    source_job_id TEXT,
    title TEXT NOT NULL,
    company TEXT,
    location TEXT,
    region TEXT,
    salary_min NUMERIC,
    salary_max NUMERIC,
    employment_type TEXT,
    seniority TEXT,
    work_mode TEXT,
    industry TEXT,
    description_summary TEXT,
    url TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    posted_date TIMESTAMP,
    closing_date TIMESTAMP,
    first_seen_at TIMESTAMP NOT NULL,
    last_seen_at TIMESTAMP NOT NULL,
    created_at TIMESTAMP NOT NULL,
    updated_at TIMESTAMP NOT NULL
);
```

## raw_job_pages

```sql
CREATE TABLE raw_job_pages (
    id UUID PRIMARY KEY,
    source TEXT NOT NULL,
    url TEXT NOT NULL,
    raw_html TEXT,
    markdown TEXT,
    raw_json JSONB,
    crawled_at TIMESTAMP NOT NULL,
    crawl_run_id UUID
);
```

## job_skills

```sql
CREATE TABLE job_skills (
    id UUID PRIMARY KEY,
    job_id UUID NOT NULL REFERENCES jobs(id),
    skill_name TEXT NOT NULL,
    skill_type TEXT,
    confidence NUMERIC
);
```

## crawl_runs

```sql
CREATE TABLE crawl_runs (
    id UUID PRIMARY KEY,
    source TEXT NOT NULL,
    status TEXT NOT NULL,
    started_at TIMESTAMP NOT NULL,
    finished_at TIMESTAMP,
    total_pages INTEGER DEFAULT 0,
    total_jobs_found INTEGER DEFAULT 0,
    total_jobs_saved INTEGER DEFAULT 0,
    error_message TEXT
);
```

## job_sources

```sql
CREATE TABLE job_sources (
    id UUID PRIMARY KEY,
    name TEXT NOT NULL,
    base_url TEXT,
    method TEXT NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    crawl_frequency_minutes INTEGER DEFAULT 1440,
    created_at TIMESTAMP NOT NULL,
    updated_at TIMESTAMP NOT NULL
);
```

---

# 15. API Design

## Job endpoints

```http
GET /api/jobs
GET /api/jobs/{id}
GET /api/jobs/search
```

## Analytics endpoints

```http
GET /api/analytics/overview
GET /api/analytics/jobs-by-region
GET /api/analytics/jobs-over-time
GET /api/analytics/top-skills
GET /api/analytics/salary-ranges
GET /api/analytics/companies
GET /api/analytics/graduate-market
GET /api/analytics/technology-trends
```

## Crawler/admin endpoints

```http
POST /api/admin/crawl-runs
GET /api/admin/crawl-runs
GET /api/admin/crawl-runs/{id}
POST /api/admin/sources
PATCH /api/admin/sources/{id}
```

---

# 16. Frontend Dashboard Pages

## Page 1: Overview

Components:

- Total jobs card
- New jobs today card
- New jobs this week card
- Average salary card
- Jobs over time line chart
- Jobs by region bar chart
- Top skills bar chart
- Recent jobs table

---

## Page 2: Region Analysis

Components:

- NZ map
- Region filter
- Jobs by region chart
- Salary by region chart
- Top companies by region
- Top skills by region

---

## Page 3: Skill Demand

Components:

- Top requested skills
- Fastest-growing skills
- Skill trend over time
- Skills by role
- Skills by region

---

## Page 4: Salary Dashboard

Components:

- Salary range by role
- Salary by region
- Salary by seniority
- Remote vs hybrid vs on-site salary comparison

---

## Page 5: Company Dashboard

Components:

- Top hiring companies
- Company detail view
- Company job count trend
- Common skills by company

---

## Page 6: Graduate Dashboard

Components:

- Graduate jobs count
- Junior jobs count
- Internship jobs count
- Graduate salary estimate
- Top graduate skills
- Companies hiring graduates

---

## Page 7: AI Insights

Components:

- Weekly summary
- Hot skills
- Growing regions
- Salary observations
- Recommended skills to learn

---

# 17. Frontend Component Plan

```text
components/
  dashboard/
    StatCard.tsx
    ChartCard.tsx
    FilterBar.tsx
    DateRangePicker.tsx

  charts/
    JobsOverTimeChart.tsx
    JobsByRegionChart.tsx
    TopSkillsChart.tsx
    SalaryRangeChart.tsx
    CompanyHiringChart.tsx

  tables/
    JobsTable.tsx
    CompaniesTable.tsx
    SkillsTable.tsx

  maps/
    NzRegionMap.tsx

  layout/
    AppSidebar.tsx
    TopNav.tsx
    PageHeader.tsx
```

---

# 18. Backend Service Plan

```text
Application services:

JobService
AnalyticsService
CrawlerOrchestrationService
AiExtractionService
NormalisationService
DeduplicationService
SourceManagementService
```

## Responsibilities

### JobService

- Save jobs
- Search jobs
- Retrieve job details

### AnalyticsService

- Generate dashboard metrics
- Aggregate by region, skill, company, salary, seniority

### CrawlerOrchestrationService

- Start crawl runs
- Call source adapters
- Save raw pages
- Trigger normalisation

### AiExtractionService

- Send job text to LLM
- Validate JSON output
- Retry failed extraction

### NormalisationService

- Standardise region
- Standardise salary
- Standardise seniority
- Standardise skills

### DeduplicationService

- Generate hash
- Check duplicate jobs
- Update existing records

---

# 19. Background Job Plan

Use either:

- .NET BackgroundService for simple MVP
- Hangfire for production-style dashboard and retry visibility

## Jobs

```text
Daily crawl all active sources
Reprocess failed AI extractions
Refresh analytics cache
Clean old raw pages
Generate weekly AI summary
```

---

# 20. Environment Variables

```env
DATABASE_URL=
OPENAI_API_KEY=
GEMINI_API_KEY=
FIRECRAWL_API_KEY=
SCRAPLING_FETCHER_TYPE=fetcher
SCRAPLING_PYTHON_EXECUTABLE=python3
SCRAPLING_WORKER_SCRIPT=
SCRAPLING_ROBOTS_TXT_OBEY=true
SCRAPLING_CONCURRENCY=2
SCRAPLING_DOWNLOAD_DELAY_SECONDS=5
SCRAPLING_TIMEOUT_SECONDS=120
SCRAPLING_PROXY_URL=
TRADEME_CONSUMER_KEY=
TRADEME_CONSUMER_SECRET=
CRAWLER_MAX_PAGES_PER_RUN=50
CRAWLER_DELAY_SECONDS=5
AI_EXTRACTION_ENABLED=true
```

---

# 21. Local Development Setup

## Docker services

```yaml
services:
  postgres:
    image: postgres:16

  backend:
    build: ./backend

  frontend:
    build: ./frontend

  worker:
    build: ./backend
```

## Local commands

```bash
docker compose up -d
cd backend
dotnet ef database update
dotnet run

cd frontend
npm install
npm run dev
```

---

# 22. Implementation Milestones

## Milestone 1: Project foundation

Tasks:

- Create monorepo structure
- Create backend .NET Web API
- Create React Vite app
- Add PostgreSQL Docker Compose
- Add EF Core
- Add base entities
- Add migrations

Acceptance criteria:

- Backend runs locally
- Frontend runs locally
- PostgreSQL runs locally
- API can connect to database

---

## Milestone 2: Job data model

Tasks:

- Create Job entity
- Create JobSkill entity
- Create RawJobPage entity
- Create CrawlRun entity
- Create repositories
- Create seed data

Acceptance criteria:

- Jobs can be inserted
- Jobs can be listed from API
- Seed data appears in dashboard

---

## Milestone 3: Dashboard MVP

Tasks:

- Build layout
- Add overview cards
- Add jobs table
- Add jobs by region chart
- Add top skills chart
- Add salary chart
- Add date/region filters

Acceptance criteria:

- Dashboard displays seeded data
- Charts update based on filters
- UI is clean and responsive

---

## Milestone 4: Scrapling crawler integration

Tasks:

- Add Python crawler service
- Add Scrapling dependency and worker entrypoint
- Add source config table
- Create ScraplingJobAdapter or API bridge
- Create one source spider for a test source
- Crawl one test source
- Save raw page
- Extract basic job fields

Acceptance criteria:

- Admin can trigger crawl
- Crawl run is recorded
- Raw pages are saved
- Extracted jobs are stored
- Crawler respects configured request delay and robots.txt setting

---

## Milestone 5: AI extraction

Tasks:

- Create AI extraction prompt
- Send raw job text to LLM
- Validate JSON output
- Save extracted skills/salary/seniority
- Handle failed extraction

Acceptance criteria:

- Job skills are extracted
- Seniority is classified
- Salary is parsed when available
- Invalid JSON does not crash the system

---

## Milestone 6: Multi-source support

Tasks:

- Add Trade Me adapter
- Add SEEK adapter
- Add Kiwi Health Jobs adapter
- Add Education Gazette adapter
- Add per-source configuration
- Add source enable/disable setting

Acceptance criteria:

- At least two sources work
- Each source produces same internal schema
- Failed source does not stop other sources

---

## Milestone 7: Advanced analytics

Tasks:

- Add jobs over time
- Add skill trends
- Add company trends
- Add graduate market analytics
- Add region comparison
- Add cached analytics table if needed

Acceptance criteria:

- Dashboard can show trends
- Analytics endpoints are fast
- Filters work across charts

---

## Milestone 8: AI insights

Tasks:

- Generate weekly summary
- Generate skill recommendations
- Generate graduate market summary
- Store insight history

Acceptance criteria:

- AI insight page displays generated summaries
- Summary is based on database statistics
- Insights are not generated from invented data

---

## Milestone 9: Deployment

Tasks:

- Add production Dockerfile
- Add environment variable docs
- Deploy backend
- Deploy frontend
- Deploy database
- Add scheduled crawling

Acceptance criteria:

- App is accessible online
- Scheduled crawl works
- Database persists data
- Errors are logged

---

# 23. Testing Plan

## Backend tests

- JobService tests
- DeduplicationService tests
- NormalisationService tests
- AnalyticsService tests
- AI output validation tests

## Frontend tests

- Dashboard renders
- Filters update charts
- API error state appears
- Empty state appears

## Crawler tests

- Adapter returns correct schema
- Duplicate jobs are skipped
- Failed crawl is logged
- Rate limit delay is respected

---

# 24. Security and Compliance

Important rules:

- Do not expose API keys in frontend
- Store secrets in environment variables
- Do not publicly republish full job descriptions
- Always link to original job source
- Respect crawling limits
- Add user-agent identification where appropriate
- Keep crawl logs for debugging
- Disable sources that block or disallow crawling

---

# 25. Dashboard Metrics

## Overview metrics

- Total active jobs
- New jobs today
- New jobs this week
- Total companies
- Average salary
- Median salary
- Most active region
- Most requested skill

## Skill metrics

- Top skills
- Skill growth rate
- Skill by region
- Skill by seniority
- Skill by salary range

## Region metrics

- Jobs per region
- Salary per region
- Top companies per region
- Graduate roles per region

## Company metrics

- Open jobs
- Hiring trend
- Common skills
- Common seniority levels
- Locations

## Graduate metrics

- Graduate jobs
- Junior jobs
- Internship jobs
- Entry-level salary
- Top graduate employers
- Top graduate skills

---

# 26. Recommended First Build Scope

The first working version should only include:

- Manual seed data
- PostgreSQL
- .NET API
- React dashboard
- Overview page
- Skills chart
- Region chart
- Jobs table
- Scrapling test integration
- AI extraction for one job source

Do not start with every source. Build the pipeline first.

---

# 27. Coding Agent Instructions

The coding agent should follow these rules:

1. Build the project incrementally.
2. Do not implement all sources at once.
3. Start with seed data before crawler integration.
4. Keep crawler code separate from backend API logic.
5. Store raw data before normalising.
6. Use one common job schema for all sources.
7. Add tests for deduplication and normalisation early.
8. Keep API responses simple and typed.
9. Use TypeScript types that match backend DTOs.
10. Prefer clean architecture over quick hacks.

---

# 28. Suggested First Prompt for Coding Agent

```text
Build the foundation of the NZ Job Market Intelligence Dashboard.

Create a monorepo with:
- backend: .NET Web API
- frontend: React + TypeScript + Vite
- database: PostgreSQL using Docker Compose

Implement:
- Job entity
- JobSkill entity
- CrawlRun entity
- RawJobPage entity
- EF Core migrations
- Seed data
- GET /api/jobs
- GET /api/analytics/overview
- GET /api/analytics/top-skills
- GET /api/analytics/jobs-by-region
- Frontend overview dashboard with cards, bar charts, and jobs table

Do not implement real crawling yet. Use seed data first.
```

---

# 29. Suggested Second Prompt for Coding Agent

```text
Add Scrapling MVP integration.

Implement:
- Python crawler service using Scrapling
- IJobSourceAdapter interface
- ScraplingJobAdapter or API bridge from .NET to the Python worker
- CrawlRun tracking
- RawJobPage storage
- Manual endpoint: POST /api/admin/crawl-runs
- One source spider that converts fetched pages into RawJobInput
- Deduplication using content hash
- Save extracted jobs into the jobs table

Keep the crawler logic separate from controllers.
```

---

# 30. Suggested Third Prompt for Coding Agent

```text
Add AI extraction pipeline.

Implement:
- AiExtractionService
- JSON schema validation
- Prompt template for extracting skills, salary, seniority, work mode, and employment type
- Retry handling
- Save extracted skills into job_skills table
- Add analytics endpoints for skills and seniority

Use Unknown for missing values. Do not invent salary.
```

---

# 31. Final Recommendation

The best practical path is:

```text
Seed data dashboard
→ Scrapling crawler MVP
→ AI extraction
→ Multi-source adapters
→ Advanced analytics
→ Scrapling dynamic/stealth crawling and scheduling
→ Personalised job recommendation features
```

This gives a strong balance between:

- Fast delivery
- Real-world architecture
- Portfolio value
- Learning value
- Future scalability

The project should be built as a **data intelligence platform**, not just a job board.
