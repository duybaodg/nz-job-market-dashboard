# Crawler

Crawler implementation uses Scrapling as the primary Python runtime. The existing backend Firecrawl adapter is fallback plumbing only.

## Local Setup

```bash
python3.11 -m venv crawler/.venv
crawler/.venv/bin/python -m pip install -e crawler
```

Manual worker run:

```bash
crawler/.venv/bin/python crawler/adapters/scrapling/scrapling_worker.py \
  --source "Example Jobs" \
  --url https://example.com/jobs \
  --fetcher-type fetcher \
  --max-pages 20 \
  --delay-seconds 5 \
  --robots-txt-obey
```

The worker follows listing pagination and job-detail links sequentially. It writes JSON containing `rawPages` and normalized `jobs` to stdout. The .NET adapter retains the configured website name as the job source.

Available parsers are `generic`, `govt-nz`, `absolute-it`, `talent-army`, and `halter-ashby`. Halter uses Ashby's public JSON postings API and filters to New Zealand technology roles. Absolute IT and Talent Army remain disabled pending source-owner permission.

Planned adapters:

- `adapters/scrapling/`
- `adapters/firecrawl/`
- `adapters/trademe/`
- `adapters/seek/`
- `adapters/kiwi-health-jobs/`
- `adapters/education-gazette/`

Every adapter must return the shared `RawJobInput` schema and store raw pages before normalisation.

Scrapling worker implementation:

1. Python project metadata lives under `crawler/`.
2. The generic worker starts with Scrapling `Fetcher`.
3. The .NET API saves raw HTML/text and extracted `RawJobInput` rows.
4. Source config supports robots.txt behavior, fetcher type, timeout, and proxy URL.
