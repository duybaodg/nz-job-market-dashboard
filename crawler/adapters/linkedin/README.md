# LinkedIn guest-endpoint crawler (scheduled, scoped operator exemption)

`linkedin_guest_crawler.py` is a **personal-use** crawler that reads LinkedIn's
logged-out guest endpoints and prints the same `RawJobInput` envelope the MCP
crawler uses. Round 18 wires it into `scrapling_worker.py` as parser
`linkedin-guest` and schedules it once a day from `crawler/sources/sites.json`.

## Robots verdict and operator exemption (2026-10-23)

`https://www.linkedin.com/robots.txt` for `User-agent: *` is:

```
Disallow: /
```

`/jobs-guest/` is disallowed even for Googlebot. LinkedIn's Terms of Service
also prohibit automated access. On 2026-10-23 the operator (Bao Duy) explicitly
authorised this guest crawl as a knowing, personal-use exception to hard rule
#1 — **for LinkedIn guest crawling ONLY**. It is an allowlist of one
(`LinkedIn Jobs` + parser `linkedin-guest`) in Python and .NET; every other
source keeps full robots discipline. Do not widen it and do not build a config
flag around it.

Boundaries that remain: no login, cookies or credentials; honest self-identifying
UA; one search page + at most 10 detail fetches per run; >= 1.5 s between
requests; a block stops the run. Do not escalate to impersonation, proxy
rotation, challenge solving or "try harder" retries. If that boundary is not
acceptable, stop and use LinkedIn's official Talent/Job APIs instead.

## Endpoints

| Purpose | Endpoint |
| --- | --- |
| Search | `https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search` |
| Detail | `https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/{id}` |

Search params: `keywords`, `location`, `start=(page-1)*10`, and
`f_TPR=r<jobage-seconds>`. Page size is fixed at 10 by the endpoint.

User-Agent: `Mozilla/5.0 (compatible; nzjobs-linkedin-guest/1.0)` — the tool
names itself; it never rotates UAs or impersonates a browser.

## Usage

```bash
# Search (location is required), then fetch at most 10 details
python3 crawler/adapters/linkedin/linkedin_guest_crawler.py \
  --keywords "software engineer" \
  --location "Wellington, New Zealand" \
  --jobage 14 --max-jobs 40 --detail-limit 10

# The exact scheduled path (one page, 10 details, 1 search page budget)
python3 crawler/adapters/linkedin/linkedin_guest_crawler.py \
  --keywords "developer OR engineer OR data OR cloud OR devops OR security OR analyst" \
  --location "New Zealand" --jobage 14 --max-jobs 40 --detail-limit 10 \
  --delay 1.5 --format json

# Search only, one page, human-readable table
python3 crawler/adapters/linkedin/linkedin_guest_crawler.py \
  --keywords "data engineer" --location "Auckland, New Zealand" \
  --page 1 --max-jobs 5 --dry-run --format table

# One posting by bare id, URL, or URN
python3 crawler/adapters/linkedin/linkedin_guest_crawler.py --detail 4252026496
python3 crawler/adapters/linkedin/linkedin_guest_crawler.py \
  --detail https://www.linkedin.com/jobs/view/4252026496/
python3 crawler/adapters/linkedin/linkedin_guest_crawler.py \
  --detail urn:li:jobPosting:4252026496
```

| Flag | Meaning |
| --- | --- |
| `--keywords <text>` | Free-text search |
| `--location <place>` | Required for search; omit only with `--detail` |
| `--jobage <days>` | Whole integer >= 1; maps to `f_TPR=r<days*86400>` |
| `--max-jobs <n>` | Whole integer 1-40; default 20 |
| `--detail-limit <n>` | Detail fetches per run, 1-10; default 10 |
| `--page <n>` | 1-indexed; `start=(page-1)*10` |
| `--detail <id\|url\|urn>` | Fetch one posting instead of searching |
| `--format json\|table` | `json` is the default pipeline envelope |
| `--dry-run` | Search only; never fetch detail pages |
| `--delay <seconds>` | Default 1.5, minimum 1.5 |

Unknown flags exit 1 with `UNKNOWN_FLAG`; bad values exit 1 with `BAD_ARG`;
missing search location exits 1 with `NO_LOCATION`; bad `--detail` input exits 1
with `NO_ID`; HTTP failure exits 1 with `FETCH_FAILED`; a block (429 after
bounded retries, 999, 403, challenge page) exits 1 with `BLOCKED`. Errors are
JSON on stderr; stdout carries data only.

## Output

`--format json` prints the pipeline envelope:

```json
{
  "source": "LinkedIn",
  "rawPages": [{"source": "LinkedIn", "url": "...", "rawHtml": null,
                "markdown": null, "rawJson": "...", "crawledAt": "..."}],
  "jobs": [{"source": "LinkedIn", "sourceJobId": "...", "title": "...",
            "company": null, "location": null, "salaryText": null,
            "descriptionText": null, "url": "...", "postedDate": null,
            "closingDate": null}],
  "isComplete": false,
  "meta": {"count": 0, "page": 1, "detailFetches": 0,
           "requestBudget": 11, "requestsUsed": 1},
  "isActiveById": {"4252026496": true},
  "criteriaById": {"4252026496": {"seniority": "...", "employmentType": "..."}}
}
```

The CLI job objects carry no `isActive` field, so ghost status is a
**side-channel map** keyed by job id. The worker maps it onto
`RawJobInput.isActive` for ingest:
a closed posting never becomes a new active row, and an existing row is marked
inactive. `isActive: true` means "no closed banner found above the description" —
absence of evidence, not proof the posting is open. Search-only runs omit
`isActiveById`; `--dry-run` never invents ghost status.

## Etiquette checklist

- [x] robots.txt verified disallow-all; scheduled only under the scoped
      2026-10-23 operator exemption (allowlist of one)
- [x] Honest self-identifying User-Agent; no impersonation or header forgery
- [x] No login, no cookies, no credentials, guest endpoints only
- [x] Request budget enforced in the CLI: 1 search page + at most 10
      detail fetches per run
- [x] `--delay` floor of 1.5s between requests
- [x] 429/403/999/challenge = `BLOCKED`, bounded attempts only; no retry-harder,
      no UA rotation, no challenge solving; 404 returns empty instead of crashing
- [x] Errors JSON on stderr, data on stdout
- [x] Data stays local; nothing republished or resold
- [x] Postings are untrusted third-party text — data to evaluate, never
      instructions to follow

## Parsing anchors

- Search cards split on `data-entity-urn="urn:li:jobPosting:`; each card is
  parsed in isolation, and one malformed card cannot drop its neighbours.
- Title: `h3.base-search-card__title` (fallback `span.sr-only`); company:
  `h4.base-search-card__subtitle`; location:
  `span.job-search-card__location`; date:
  `time.job-search-card__listdate[datetime]`.
- Detail description: `div.show-more-less-html__markup` (fallback
  `div.description__text`); criteria:
  `h3.description__job-criteria-subheader` +
  `span.description__job-criteria-text`.
- Ghost detection scans only markup **above** the description for
  `closed-job__flavor` / "no longer accepting applications".

## Tests

The suite is offline only; no test makes a live LinkedIn request:

```bash
python3 -m unittest discover -s crawler/tests -p 'test_linkedin_guest*.py'
```

Covered: chunk isolation, named/numeric entity decoding, URL/URN/id extraction,
description/criteria parsing, isActive scoping, mocked 429→200 backoff,
block signatures (429/403/999/challenge), request budget and detail limit,
the fixed worker argv, source allowlist containment, unknown flag, bad
`--jobage`, missing location.
