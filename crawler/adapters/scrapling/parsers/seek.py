"""SEEK listing/detail parsers and SSR payload helpers.

Round 19: mechanically moved out of ``scrapling_worker.py``.
"""
from __future__ import annotations

import json
import re
import sys
from datetime import datetime, timedelta, timezone
from typing import Any

from models import RawJobInput
from normalisation import (
    clean_text,
    extract_location,
    extract_salary,
    extract_title,
    iso_date,
    sanitise_location,
)

SEEK_WORK_TYPES = {
    "242": "Full-time",
    "243": "Part-time",
    "244": "Contract",
    "245": "Casual",
    "246": "Temporary",
}

# The SSR payload carries display labels ("Full time", "Contract/Temp") rather
# than the numeric ids the detail payload used. Normalise the labels that have
# an unambiguous employment-type meaning; anything else is ignored rather than
# guessed at.
SEEK_WORK_TYPE_LABELS = {
    "full time": "Full-time",
    "part time": "Part-time",
    "contract": "Contract",
    "contract/temp": "Contract",
    "casual": "Casual",
    "temporary": "Temporary",
}

SEEK_REDUX_MARKER = "window.SEEK_REDUX_DATA"
SEEK_RECENCY_DAYS = 31

NUMBER_WORDS = {
    "a": 1, "an": 1, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6,
    "seven": 7, "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12,
}


def relative_days_ago(value: Any) -> str | None:
    """Convert relative ages ("18d ago", "Listed six days ago") to an ISO date."""
    if not value:
        return None
    text = str(value).strip().lower()
    explicit = iso_date(value)
    if explicit:
        return explicit
    now = datetime.now(timezone.utc)
    days: int | None = None
    match = re.search(r"(\d+|" + "|".join(NUMBER_WORDS) + r")\s*d(?:ay)?s?\s*ago", text)
    if match:
        token = match.group(1)
        days = int(token) if token.isdigit() else NUMBER_WORDS.get(token)
    elif re.search(r"(\d+|a|an|one)\s*(?:h|hour|min|minute)", text) or "just posted" in text:
        days = 0
    elif "yesterday" in text:
        days = 1
    elif "today" in text:
        days = 0
    if days is None:
        return None
    return (now - timedelta(days=days)).strftime("%Y-%m-%dT00:00:00Z")


def extract_embedded_json(html: str, marker: str) -> Any | None:
    """Extract a JSON object assigned after ``marker`` in an inline script.

    SEEK embeds its complete search result set as ``window.SEEK_REDUX_DATA =
    {...};``. A regex to the first ``}`` is wrong because JSON string values can
    contain braces, so this performs a string-aware balanced-brace scan. Returns
    ``None`` for a missing marker, an unbalanced payload, or invalid JSON; it
    never raises on malformed markup.
    """
    if not html or not marker:
        return None
    marker_at = html.find(marker)
    if marker_at < 0:
        return None
    start = html.find("{", marker_at)
    if start < 0:
        return None

    depth = 0
    in_string = False
    escaped = False
    for index in range(start, len(html)):
        character = html[index]
        if in_string:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                in_string = False
            continue
        if character == '"':
            in_string = True
        elif character == "{":
            depth += 1
        elif character == "}":
            depth -= 1
            if depth == 0:
                try:
                    return json.loads(html[start : index + 1])
                except json.JSONDecodeError:
                    return None
    return None


def _seek_work_type_label(value: Any) -> str | None:
    raw = clean_text(str(value or ""))
    if not raw:
        return None
    if raw in SEEK_WORK_TYPES:
        return SEEK_WORK_TYPES[raw]
    return SEEK_WORK_TYPE_LABELS.get(raw.lower())


def _seek_job_from_redux(source: str, job: dict[str, Any], now: datetime) -> RawJobInput | None:
    """Map one ``results.results.jobs[]`` entry, or None when unusable/stale."""
    job_id = clean_text(str(job.get("id") or ""))
    title = clean_text(str(job.get("title") or ""))
    if not job_id or not title:
        return None

    listing_date = str(job.get("listingDate") or "")
    date_match = re.match(r"(\d{4}-\d{2}-\d{2})", listing_date)
    posted_date = date_match.group(1) if date_match else None
    if posted_date:
        try:
            posted_at = datetime.strptime(posted_date, "%Y-%m-%d").replace(tzinfo=timezone.utc)
        except ValueError:
            posted_at = None
        if posted_at is not None and posted_at < now - timedelta(days=SEEK_RECENCY_DAYS):
            return None

    company = clean_text(str(job.get("companyName") or "")) or None
    if not company:
        advertiser = job.get("advertiser")
        if isinstance(advertiser, dict):
            company = clean_text(str(advertiser.get("description") or "")) or None

    location = None
    locations = job.get("locations")
    if isinstance(locations, list) and locations and isinstance(locations[0], dict):
        location = sanitise_location(str(locations[0].get("label") or ""))

    teaser = clean_text(str(job.get("teaser") or ""))
    classification = clean_text(str(job.get("classification") or ""))
    subclassification = clean_text(str(job.get("subClassification") or ""))
    work_type = None
    work_types = job.get("workTypes")
    if isinstance(work_types, list):
        work_type = next(
            (mapped for mapped in (_seek_work_type_label(value) for value in work_types) if mapped),
            None,
        )

    prefix = ". ".join(
        part
        for part in (
            classification,
            subclassification,
            f"Employment type: {work_type}" if work_type else "",
        )
        if part
    )
    description = f"{prefix}. {teaser}" if prefix and teaser else (teaser or prefix)

    return RawJobInput(
        source=source,
        sourceJobId=job_id,
        title=title,
        company=company,
        location=location,
        salaryText=extract_salary(teaser),
        descriptionText=(description or "")[:12000] or None,
        url=f"https://www.seek.co.nz/job/{job_id}",
        postedDate=posted_date,
        closingDate=None,
    )


def _parse_seek_html_cards(source: str, url: str, html: str) -> list[RawJobInput]:
    """Legacy HTML-card fallback, used only when the Redux marker is absent."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()

    for match in re.finditer(
        r'<article[^>]*data-automation="normalJob".*?</article>', html, re.I | re.S
    ):
        chunk = match.group(0)

        def field(name: str) -> str | None:
            found = re.search(
                rf'data-automation="{re.escape(name)}"[^>]*>(.*?)</', chunk, re.I | re.S
            )
            return clean_text(found.group(1)) if found else None

        title = field("jobTitle")
        link = re.search(r'href="(?:https?://[^/"]+)?(/job/\d+)', chunk)
        if not title or not link:
            continue
        job_id = re.search(r"/job/(\d+)", link.group(1))
        if not job_id or job_id.group(1) in seen:
            continue
        seen.add(job_id.group(1))

        classification = field("jobClassification")
        subclass = field("jobSubClassification")
        teaser = field("jobShortDescription")
        prefix = ". ".join(part for part in (classification, subclass) if part)
        description = f"{prefix}. {teaser}" if prefix and teaser else (teaser or prefix)

        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=job_id.group(1),
                title=title,
                company=field("jobCompany"),
                location=sanitise_location(
                    field("jobLocation") or field("jobCardLocation") or extract_location(teaser or "")
                ),
                salaryText=extract_salary(chunk),
                descriptionText=(description or "")[:12000] or None,
                url=f"https://www.seek.co.nz/job/{job_id.group(1)}",
                postedDate=relative_days_ago(field("jobListingDate")),
                closingDate=None,
            )
        )

    return jobs


def parse_seek_listing(
    source: str, url: str, html: str, text: str, now: datetime | None = None
) -> list[RawJobInput]:
    """Extract SEEK jobs from the SSR ``window.SEEK_REDUX_DATA`` payload.

    SEEK's robots.txt disallows ``*/job/`` detail pages, so we deliberately never
    fetch them: the keyword-search listing page is allowed and the server-rendered
    Redux payload already carries id, title, company, location and ISO listing
    date. If that marker disappears, the old HTML-card parser is kept as an
    explicit fallback; a 200 page with neither payload nor cards raises loudly so
    a challenge page can never masquerade as "zero jobs".
    """
    reference_now = now or datetime.now(timezone.utc)
    marker_present = SEEK_REDUX_MARKER in html
    payload = extract_embedded_json(html, SEEK_REDUX_MARKER)

    if payload is not None:
        nested = payload.get("results") if isinstance(payload, dict) else None
        nested = nested.get("results") if isinstance(nested, dict) else None
        raw_jobs = nested.get("jobs") if isinstance(nested, dict) else None
        if not isinstance(raw_jobs, list):
            raise ValueError(
                f"SEEK Redux payload for {url} has no results.results.jobs array"
            )
        jobs: list[RawJobInput] = []
        seen_ids: set[str] = set()
        for raw_job in raw_jobs:
            if not isinstance(raw_job, dict):
                continue
            job_id = str(raw_job.get("id") or "").strip()
            if not job_id or job_id in seen_ids:
                continue
            seen_ids.add(job_id)
            job = _seek_job_from_redux(source, raw_job, reference_now)
            if job is not None:
                jobs.append(job)
        print(
            f"SEEK parser path=json url={url} jobs={len(jobs)} "
            f"(raw={len(raw_jobs)}, deduped={len(seen_ids)})",
            file=sys.stderr,
        )
        return jobs

    if marker_present:
        # The marker exists but the payload is truncated or no longer JSON:
        # markup drift, not an empty search. Never silently return zero.
        raise ValueError(
            f"SEEK page {url} contains window.SEEK_REDUX_DATA but the payload is unparseable"
        )

    jobs = _parse_seek_html_cards(source, url, html)
    if jobs:
        print(f"SEEK parser path=html-fallback url={url} jobs={len(jobs)}", file=sys.stderr)
        return jobs

    raise ValueError(
        f"SEEK page {url} returned HTTP 200 but has neither window.SEEK_REDUX_DATA nor job cards"
    )


def parse_seek_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse an already-fetched SEEK job page (``window.SK_DL`` payload).

    UNREACHABLE FROM CRAWL BY DESIGN: ``parse_seek_listing`` is the only SEEK
    parser the crawl path calls, and SEEK's robots.txt disallows ``*/job/``.
    This helper exists for offline parsing of an operator-supplied page; no
    code in this worker may use it to fetch a detail URL.
    """
    payload: dict[str, Any] = {}
    match = re.search(r"window\.SK_DL\s*=\s*(\{.*?\});", html, re.S)
    if match:
        try:
            payload = json.loads(match.group(1))
        except json.JSONDecodeError:
            payload = {}

    title = clean_text(payload.get("jobTitle")) or extract_title(html, text) or fallback_title
    company = clean_text(payload.get("advertiserName")) or None
    location = sanitise_location(payload.get("jobArea") or payload.get("jobLocation"))

    employment = None
    work_types = re.search(r'"workTypes":\{[^}]*?"label":"([^"]+)"', html)
    if work_types:
        employment = clean_text(work_types.group(1))
    if not employment and payload.get("jobWorkTypeIds"):
        first_id = str(payload["jobWorkTypeIds"]).split(",")[0].strip()
        employment = SEEK_WORK_TYPES.get(first_id)
    if employment:
        employment = employment.replace("Full time", "Full-time").replace("Part time", "Part-time")

    # The job ad HTML lives in the largest unescaped "content" JSON string.
    best_content = ""
    for raw in re.findall(r'"content":"((?:[^"\\]|\\.)*)"', html):
        try:
            candidate = json.loads('"' + raw + '"')
        except json.JSONDecodeError:
            continue
        if len(candidate) > len(best_content):
            best_content = candidate
    description = clean_text(best_content)
    if employment and description:
        description = f"Employment type: {employment}. {description}"

    job_id = re.search(r"/job/(\d+)", url)
    return RawJobInput(
        source=source,
        sourceJobId=job_id.group(1) if job_id else None,
        title=title,
        company=company,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=description[:12000] or None,
        url=url,
        postedDate=relative_days_ago(payload.get("jobPostedTime")),
        closingDate=None,
    )
