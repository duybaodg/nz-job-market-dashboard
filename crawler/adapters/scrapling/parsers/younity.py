"""Younity IT jobs (WordPress + JobAdder widget) parsing.

The public board at https://younity.co.nz/it-jobs/ is a WordPress page whose
listing area is an empty ``#ja-jobs-widget`` div. The page's inline
``_jaJobsSettings`` config carries the JobAdder board key, and the widget calls
two JSONP endpoints on apps.jobadder.com:

* ``/widgets/V1/Jobs/RenderJobList`` — HTML cards (title, classifications,
  summary) plus a pager summary.
* ``/widgets/V1/Jobs/RenderJobDetails`` — full description, bullet points
  (sometimes the salary) and classifications for one job id.

Robots verdicts, probed 2026-11-07 with the honest crawler UA
``Mozilla/5.0 (compatible; NZJobMarketDashboardBot/0.2; ...)``:

* https://younity.co.nz/robots.txt — ``User-agent: *`` -> empty ``Disallow:``
  (allow all); ``/it-jobs/`` is 200.
* https://apps.jobadder.com/robots.txt — ``Disallow: /`` followed by
  ``Allow: /widgets/v1/*`` and ``Allow: /widgets/V1/*``. Both list and detail
  endpoints were verified through the worker's ``obeys_robots`` matcher.

The crawl branch still gates EVERY request (landing page, list pages, detail
pages) through ``obeys_robots`` before fetching; this allow-all result is not
treated as permission to skip the gate.
"""
from __future__ import annotations

import json
import re
from typing import Any
from urllib.parse import urlencode

from models import RawJobInput
from normalisation import clean_text, extract_salary, html_group

JOBADDER_WIDGET_BASE = "https://apps.jobadder.com/widgets/V1/Jobs"
YOUNITY_LISTING_PAGE = "https://younity.co.nz/it-jobs/"
YOUNITY_COMPANY = "Younity"

#: The public widget uses ``?ja-job=<id>`` to open a detail view on the board.
YOUNITY_JOB_URL_TEMPLATE = YOUNITY_LISTING_PAGE + "?ja-job={job_id}"

_JSONP_RE = re.compile(r"^\s*[^()\s]+\((.*)\)\s*;?\s*$", re.S)
_JOB_CARD_START_RE = re.compile(r'<div class="job(?: alt)?">', re.I)
_CLASSIFICATIONS_RE = re.compile(
    r'<ul class="classifications">(.*?)</ul>', re.I | re.S
)
_EMPLOYMENT_TYPES = {
    "permanent",
    "contract",
    "part-time",
    "part time",
    "full-time",
    "full time",
    "fixed term",
    "fixed-term",
    "casual",
    "internship",
}


def decode_jobadder_jsonp(payload: str) -> str:
    """Return the HTML string inside a JobAdder JSONP response."""
    match = _JSONP_RE.match(payload or "")
    if not match:
        raise ValueError("JobAdder response was not a JSONP callback")

    try:
        value = json.loads(match.group(1))
    except json.JSONDecodeError as exc:
        raise ValueError("JobAdder JSONP payload was not valid JSON") from exc

    if not isinstance(value, str):
        raise ValueError("JobAdder JSONP payload was not an HTML string")
    return value


def extract_jobadder_key(html: str) -> str | None:
    """Read the public JobAdder widget key from Younity's inline settings."""
    match = re.search(r"_jaJobsSettings\s*=\s*(\{.*?\})\s*;", html or "", re.S)
    if not match:
        return None

    try:
        settings = json.loads(match.group(1))
    except json.JSONDecodeError:
        return None

    key = settings.get("key") if isinstance(settings, dict) else None
    return str(key).strip() if key else None


def younity_list_url(key: str, page_number: int) -> str:
    query = urlencode(
        {
            "key": key,
            "jobsPerPage": 8,
            "pageNumber": max(1, int(page_number)),
            "titleIsLink": "true",
            "showDatePosted": "false",
            "showClassifications": "true",
            "showSalary": "false",
            "showJobReference": "false",
            "showPagerSummary": "true",
            # JobAdder serves JSONP and rejects a callback-less GET; the widget's
            # jQuery client adds this automatically, so the server-side fetcher
            # must send one too.
            "callback": "nzjobsCallback",
        }
    )
    return f"{JOBADDER_WIDGET_BASE}/RenderJobList?{query}"


def younity_detail_url(key: str, job_id: str) -> str:
    query = urlencode(
        {
            "key": key,
            "jobID": job_id,
            "showDatePosted": "false",
            "showClassifications": "true",
            "showSalary": "false",
            "showJobReference": "false",
            "showBulletPoints": "true",
            "applyButtonText": "Apply Now",
            "backLinkText": "Real IT Jobs",
            "callback": "nzjobsCallback",
        }
    )
    return f"{JOBADDER_WIDGET_BASE}/RenderJobDetails?{query}"


def _classifications(fragment: str) -> list[str]:
    match = _CLASSIFICATIONS_RE.search(fragment)
    if not match:
        return []
    return [
        clean_text(value)
        for value in re.findall(r"<li[^>]*>(.*?)</li>", match.group(1), re.I | re.S)
        if clean_text(value)
    ]


def _classification_fields(values: list[str]) -> dict[str, str | None]:
    """Map the JobAdder classification list to category/role/location/type.

    The board consistently emits four entries in this order:
    category, role, location, employment type. Fall back to value matching if
    JobAdder ever changes the order or omits an entry.
    """
    category = values[0] if values else None
    role = values[1] if len(values) > 1 else None
    location = values[2] if len(values) > 2 else None
    employment = values[3] if len(values) > 3 else None

    if employment is None:
        employment = next(
            (value for value in values[2:] if value.lower() in _EMPLOYMENT_TYPES),
            None,
        )
    if location is None:
        location = next(
            (
                value
                for value in values[2:]
                if value.lower() not in _EMPLOYMENT_TYPES
            ),
            None,
        )

    return {
        "category": category,
        "role": role,
        "location": location,
        "employment": employment,
    }


def parse_younity_list(fragment: str) -> tuple[list[dict[str, Any]], int | None]:
    """Parse one RenderJobList fragment into cards plus the pager page count."""
    cards: list[dict[str, Any]] = []
    starts = list(_JOB_CARD_START_RE.finditer(fragment or ""))
    for index, match in enumerate(starts):
        end = starts[index + 1].start() if index + 1 < len(starts) else len(fragment)
        pager = fragment.find('<div class="ja-pager-container"', match.end())
        if pager != -1 and pager < end:
            end = pager
        block = fragment[match.start():end]
        link = re.search(
            r'<a[^>]*data-job-id="(\d+)"[^>]*>(.*?)</a>', block, re.I | re.S
        )
        if not link:
            continue

        title = clean_text(link.group(2))
        if not title:
            continue

        values = _classifications(block)
        fields = _classification_fields(values)
        cards.append(
            {
                "jobId": link.group(1),
                "title": title,
                "summary": html_group(block, r'<p class="summary">(.*?)</p>'),
                "classifications": values,
                **fields,
            }
        )

    pager = re.search(r"Page\s+\d+\s+of\s+(\d+)", fragment or "", re.I)
    return cards, int(pager.group(1)) if pager else None


def parse_younity_detail(
    source: str,
    url: str,
    job_id: str,
    fallback_title: str,
    fallback_location: str | None,
    fallback_employment: str | None,
    fragment: str,
) -> RawJobInput:
    """Map a RenderJobDetails fragment onto the shared raw job shape."""
    title = html_group(fragment, r'<h2 class="title">(.*?)</h2>') or fallback_title
    values = _classifications(fragment)
    fields = _classification_fields(values)
    location = fields["location"] or fallback_location
    employment = fields["employment"] or fallback_employment

    bullet_match = re.search(
        r'<ul class="bullet-points">(.*?)</ul>', fragment or "", re.I | re.S
    )
    bullets = (
        [
            clean_text(value)
            for value in re.findall(
                r"<li[^>]*>(.*?)</li>", bullet_match.group(1), re.I | re.S
            )
            if clean_text(value)
        ]
        if bullet_match
        else []
    )
    description_html = html_group(fragment, r'<div class="description">(.*?)</div>')
    description = clean_text(description_html or fragment)
    if employment:
        description = f"{employment}. {description}"

    salary_text = extract_salary(" ".join(bullets)) or extract_salary(description)

    return RawJobInput(
        source=source,
        sourceJobId=job_id,
        title=title,
        company=YOUNITY_COMPANY,
        location=location,
        salaryText=salary_text,
        descriptionText=description[:12000] or None,
        url=url,
        postedDate=None,
        closingDate=None,
    )
