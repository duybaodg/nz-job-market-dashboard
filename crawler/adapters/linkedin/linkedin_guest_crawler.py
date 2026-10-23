#!/usr/bin/env python3
"""Scheduled LinkedIn public guest-endpoint crawler (operator exemption).

ROBOTS VERDICT (checked 2026-10-23): ``www.linkedin.com/robots.txt`` declares
``User-agent: * / Disallow: /`` (and disallows ``/jobs-guest/`` even for
Googlebot). The operator (Bao Duy) explicitly authorised this guest-endpoint
crawl on 2026-10-23 as a knowing, personal-use exception to the repository's
robots rule. The exception is scoped to LinkedIn guest crawling ONLY; every
other source keeps full robots discipline.

It uses the logged-out guest endpoints only: no login, no cookies, no
credentials, no browser impersonation, honest User-Agent, low volume. Request
budget: one search page plus at most ``--detail-limit`` detail fetches per run
(hard cap 10), each separated by at least 1.5 seconds. A block (429 after
bounded retries, 999, a challenge page, 403) fails the run with code
``BLOCKED``; never escalate, rotate UAs, retry harder or solve a challenge.

Output is the same RawJobInput envelope the MCP crawler emits, so a manual run
can be fed to the pipeline. ``RawJobInput`` has no ``isActive`` field, so ghost
status is carried in the side-channel ``isActiveById`` map (and parsed detail
criteria in ``criteriaById``).

Usage:
    python3 crawler/adapters/linkedin/linkedin_guest_crawler.py \
        --keywords "software engineer" --location "Wellington, New Zealand" \
        --jobage 14 --max-jobs 10

    python3 crawler/adapters/linkedin/linkedin_guest_crawler.py \
        --detail 4252026496 --format json

    python3 crawler/adapters/linkedin/linkedin_guest_crawler.py \
        --detail https://www.linkedin.com/jobs/view/4252026496/
"""

from __future__ import annotations

import argparse
import json
import random
import re
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from html import unescape
from typing import Any, Callable
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urljoin, urlsplit, urlunsplit
from urllib.request import Request, urlopen

SOURCE = "LinkedIn"
USER_AGENT = "Mozilla/5.0 (compatible; nzjobs-linkedin-guest/1.0)"
SEARCH_ENDPOINT = "https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search"
DETAIL_ENDPOINT = "https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/{job_id}"
JOB_VIEW_URL = "https://www.linkedin.com/jobs/view/{job_id}/"
PAGE_SIZE = 10
#: A block is back-off-and-report, never retry-harder. Three attempts at most
#: (initial + two bounded backoffs) keep a throttled run cheap.
MAX_RETRIES = 2
BACKOFF_BASE_SECONDS = 0.5
BACKOFF_MAX_SECONDS = 2.0
JITTER_MAX_SECONDS = 0.25
MIN_DELAY_SECONDS = 1.5
DEFAULT_DELAY_SECONDS = 1.5
DEFAULT_MAX_JOBS = 20
MAX_JOBS_PER_RUN = 40
DEFAULT_DETAIL_LIMIT = 10
MAX_DETAIL_LIMIT = 10
#: At most one search page + ten detail fetches per run. The CLI enforces this
#: logical-request budget itself, so the worker cannot accidentally widen it.
MAX_SEARCH_PAGES_PER_RUN = 1
MAX_REQUESTS_PER_RUN = MAX_SEARCH_PAGES_PER_RUN + MAX_DETAIL_LIMIT
FETCH_TIMEOUT_SECONDS = 15

#: Strong challenge/interstitial markers. Kept narrow so ordinary advert copy
#: ("a challenging role") is never mistaken for a block page.
CHALLENGE_MARKERS = re.compile(
    r"authwall|captcha|challenge-form|security[-_ ]check|"
    r"verify (?:you are|that you are) (?:a )?human|unusual activity",
    re.I,
)


class CliError(Exception):
    """A contract error rendered as ``{"error", "code"}`` on stderr."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


class FetchError(Exception):
    """A non-recoverable HTTP/transport failure."""


class BlockedError(FetchError):
    """LinkedIn returned a block signature (429/999/403/challenge)."""

    code = "BLOCKED"


class BudgetExceeded(FetchError):
    """The per-run request budget would be exceeded."""


class _ContractParser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        code = "UNKNOWN_FLAG" if "unrecognized arguments" in message else "BAD_ARG"
        raise CliError(code, message)


def _parse_whole_positive(value: str) -> int:
    try:
        number = int(value)
    except (TypeError, ValueError):
        raise argparse.ArgumentTypeError(f"must be a whole integer >= 1: {value!r}")
    if number < 1:
        raise argparse.ArgumentTypeError(f"must be a whole integer >= 1: {value!r}")
    return number


def _parse_max_jobs(value: str) -> int:
    number = _parse_whole_positive(value)
    if number > MAX_JOBS_PER_RUN:
        raise argparse.ArgumentTypeError(
            f"must be <= {MAX_JOBS_PER_RUN} per run: {value!r}"
        )
    return number


def _parse_detail_limit(value: str) -> int:
    number = _parse_whole_positive(value)
    if number > MAX_DETAIL_LIMIT:
        raise argparse.ArgumentTypeError(
            f"must be <= {MAX_DETAIL_LIMIT} detail fetches per run: {value!r}"
        )
    return number


def _parse_delay(value: str) -> float:
    try:
        delay = float(value)
    except (TypeError, ValueError):
        raise argparse.ArgumentTypeError(f"must be a number >= {MIN_DELAY_SECONDS}: {value!r}")
    if delay < MIN_DELAY_SECONDS:
        raise argparse.ArgumentTypeError(
            f"must be >= {MIN_DELAY_SECONDS} seconds (politeness floor): {value!r}"
        )
    return delay


def is_block_page(html: str) -> bool:
    """True when the fragment is LinkedIn's challenge/interstitial page."""
    return bool(html and CHALLENGE_MARKERS.search(html[:50_000]))


@dataclass
class RequestBudget:
    """Hard cap on logical HTTP requests (search pages + detail fetches)."""

    limit: int = MAX_REQUESTS_PER_RUN
    used: int = 0

    def spend(self, url: str) -> None:
        if self.used >= self.limit:
            raise BudgetExceeded(
                f"Request budget of {self.limit} exhausted before fetching {url}"
            )
        self.used += 1


def build_search_url(keywords: str | None, location: str, jobage: int | None, page: int) -> str:
    params: dict[str, str] = {}
    if keywords:
        params["keywords"] = keywords
    params["location"] = location
    params["start"] = str((page - 1) * PAGE_SIZE)
    if jobage:
        params["f_TPR"] = f"r{jobage * 86_400}"
    return f"{SEARCH_ENDPOINT}?{urlencode(params)}"


def _sleep_with_jitter(delay: float, sleeper: Callable[[float], None], jitter: Callable[[], float]) -> float:
    wait = min(delay, BACKOFF_MAX_SECONDS) + jitter()
    sleeper(wait)
    return min(delay * 2, BACKOFF_MAX_SECONDS)


def html_fetch(
    url: str,
    *,
    timeout: int = FETCH_TIMEOUT_SECONDS,
    max_retries: int = MAX_RETRIES,
    sleeper: Callable[[float], None] = time.sleep,
    jitter: Callable[[], float] | None = None,
) -> str:
    """Fetch a guest fragment: honest UA, 15s timeout, bounded backoff+jitter.

    429/5xx retry up to ``max_retries``; 404 returns "" (a missing posting is
    empty, not a crash); 403/999/challenge pages raise :class:`BlockedError`.
    Never rotates UAs, never sends cookies, never tries to solve a challenge.
    """
    jitter_fn = jitter or (lambda: random.uniform(0, JITTER_MAX_SECONDS))
    delay = BACKOFF_BASE_SECONDS
    headers = {
        "User-Agent": USER_AGENT,
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        "Accept-Language": "en-NZ,en;q=0.9",
        "Cache-Control": "no-cache",
    }

    for attempt in range(max_retries + 1):
        request = Request(url, headers=headers, method="GET")
        try:
            with urlopen(request, timeout=timeout) as response:
                status = int(getattr(response, "status", 200) or 200)
                body = response.read()
        except HTTPError as exc:
            if exc.code == 404:
                return ""
            if exc.code in (403, 999):
                raise BlockedError(
                    f"LinkedIn blocked the guest crawl (HTTP {exc.code}) for {url}"
                ) from exc
            if (exc.code == 429 or 500 <= exc.code < 600) and attempt < max_retries:
                delay = _sleep_with_jitter(delay, sleeper, jitter_fn)
                continue
            if exc.code == 429:
                raise BlockedError(
                    f"LinkedIn rate-limited the guest crawl (HTTP 429) after "
                    f"{max_retries + 1} bounded attempts for {url}"
                ) from exc
            raise FetchError(f"HTTP {exc.code} for {url}") from exc
        except URLError as exc:
            raise FetchError(f"Network error for {url}: {exc.reason}") from exc

        if status == 404:
            return ""
        if status in (403, 999):
            raise BlockedError(
                f"LinkedIn blocked the guest crawl (HTTP {status}) for {url}"
            )
        if (status == 429 or 500 <= status < 600) and attempt < max_retries:
            delay = _sleep_with_jitter(delay, sleeper, jitter_fn)
            continue
        if status == 429:
            raise BlockedError(
                f"LinkedIn rate-limited the guest crawl (HTTP 429) after "
                f"{max_retries + 1} bounded attempts for {url}"
            )
        if status >= 400:
            raise FetchError(f"HTTP {status} for {url}")

        text = body.decode("utf-8", errors="replace")
        if is_block_page(text):
            raise BlockedError(f"LinkedIn returned a challenge page for {url}")
        return text

    raise FetchError(f"HTTP retries exhausted for {url}")


# ---------------------------------------------------------------------------
# Text / HTML helpers
# ---------------------------------------------------------------------------


def decode_entities(value: str) -> str:
    """Decode named and numeric HTML entities (code-point-safe)."""
    return unescape(value)


def _clean_text(value: str | None) -> str:
    text = decode_entities(value or "")
    text = re.sub(r"<[^>]+>", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def _attr(attrs: str, name: str) -> str | None:
    match = re.search(rf"\b{re.escape(name)}\s*=\s*([\"'])(.*?)\1", attrs, re.I | re.S)
    return decode_entities(match.group(2)) if match else None


def _element_inner(fragment: str, tag: str, class_name: str) -> str | None:
    pattern = re.compile(rf"<{re.escape(tag)}\b([^>]*)>(.*?)</{re.escape(tag)}>", re.I | re.S)
    for match in pattern.finditer(fragment):
        if class_name in match.group(1):
            return match.group(2)
    return None


def _first_element_attrs(fragment: str, tag: str, class_name: str) -> tuple[str, str] | None:
    pattern = re.compile(rf"<{re.escape(tag)}\b([^>]*)>(.*?)</{re.escape(tag)}>", re.I | re.S)
    for match in pattern.finditer(fragment):
        if class_name in match.group(1):
            return match.group(1), match.group(2)
    return None


def _balanced_element(fragment: str, tag: str, class_name: str) -> tuple[int, int, str] | None:
    """Return ``(start, end, inner_html)`` for the first matching element."""
    open_pattern = re.compile(rf"<{re.escape(tag)}\b([^>]*)>", re.I | re.S)
    token_pattern = re.compile(rf"</?{re.escape(tag)}\b[^>]*>", re.I | re.S)
    for opening in open_pattern.finditer(fragment):
        if class_name not in opening.group(1):
            continue
        depth = 1
        for token in token_pattern.finditer(fragment, opening.end()):
            if token.group(0).startswith("</"):
                depth -= 1
            else:
                depth += 1
            if depth == 0:
                return opening.start(), token.end(), fragment[opening.end() : token.start()]
    return None


def _html_to_text(fragment: str) -> str:
    text = re.sub(r"<br\s*/?>", "\n", fragment, flags=re.I)
    text = re.sub(
        r"</(?:p|li|div|h[1-6]|ul|ol|section|article|tr|td|th|blockquote)\s*>",
        "\n",
        text,
        flags=re.I,
    )
    text = re.sub(r"<[^>]+>", " ", text)
    text = decode_entities(text)
    text = re.sub(r"[ \t\r\f\v]+", " ", text)
    text = re.sub(r"\n\s*\n\s*\n+", "\n\n", text)
    return text.strip()


def _clean_job_url(raw_url: str | None, job_id: str) -> str:
    if not raw_url:
        return JOB_VIEW_URL.format(job_id=job_id)
    absolute = urljoin("https://www.linkedin.com", decode_entities(raw_url).strip())
    parsed = urlsplit(absolute)
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        return JOB_VIEW_URL.format(job_id=job_id)
    return urlunsplit((parsed.scheme, parsed.netloc, parsed.path, "", ""))


def _iso_date(value: str | None) -> str | None:
    match = re.match(r"(\d{4}-\d{2}-\d{2})", value or "")
    return match.group(1) if match else None


# ---------------------------------------------------------------------------
# Search parsing (per-card isolation)
# ---------------------------------------------------------------------------


def parse_search_cards(fragment: str) -> list[dict[str, Any]]:
    """Parse the flat ``<li>`` fragment, isolating each card.

    Splitting on ``data-entity-urn="urn:li:jobPosting:`` gives one chunk per
    card. A malformed card is skipped; its neighbours are still parsed.
    """
    cards: list[dict[str, Any]] = []
    chunks = fragment.split('data-entity-urn="urn:li:jobPosting:')[1:]
    for chunk in chunks:
        try:
            id_match = re.match(r"\s*(\d{6,20})", chunk)
            if not id_match:
                continue
            job_id = id_match.group(1)

            href = None
            for anchor in re.finditer(r"<a\b([^>]*)>", chunk, re.I):
                attrs = anchor.group(1)
                if "base-card__full-link" in attrs:
                    href = _attr(attrs, "href")
                    break

            title_html = _element_inner(chunk, "h3", "base-search-card__title")
            if not title_html:
                title_html = _element_inner(chunk, "span", "sr-only")
            title = _clean_text(title_html)
            if not title:
                # No title means the card cannot be trusted; keep neighbours.
                continue

            company_html = _element_inner(chunk, "h4", "base-search-card__subtitle")
            company = _clean_text(company_html) or None
            company_url = None
            if company_html:
                company_anchor = re.search(r"<a\b([^>]*)>", company_html, re.I)
                if company_anchor:
                    company_url = _clean_job_url(_attr(company_anchor.group(1), "href"), job_id)

            location_html = _element_inner(chunk, "span", "job-search-card__location")
            location = _clean_text(location_html) or None

            date = None
            time_match = _first_element_attrs(chunk, "time", "job-search-card__listdate")
            if time_match:
                attrs, inner = time_match
                date = _iso_date(_attr(attrs, "datetime")) or _clean_text(inner) or None
            if not date:
                time_match = _first_element_attrs(chunk, "time", "job-search-card__listdate--new")
                if time_match:
                    attrs, inner = time_match
                    date = _iso_date(_attr(attrs, "datetime")) or _clean_text(inner) or None

            cards.append(
                {
                    "id": job_id,
                    "title": title,
                    "company": company,
                    "companyUrl": company_url,
                    "location": location,
                    "date": date,
                    "url": _clean_job_url(href, job_id),
                }
            )
        except Exception:  # noqa: BLE001 - one malformed card must not drop the batch
            continue
    return cards


def _raw_job_from_card(card: dict[str, Any]) -> dict[str, Any]:
    return {
        "source": SOURCE,
        "sourceJobId": card["id"],
        "title": card["title"],
        "company": card.get("company"),
        "location": card.get("location"),
        "salaryText": None,
        "descriptionText": None,
        "url": card["url"],
        "postedDate": card.get("date"),
        "closingDate": None,
    }


# ---------------------------------------------------------------------------
# Detail parsing
# ---------------------------------------------------------------------------


def parse_criteria(html: str) -> dict[str, str]:
    criteria: dict[str, str] = {}
    for match in re.finditer(r"<h3\b([^>]*)>(.*?)</h3>", html, re.I | re.S):
        if "description__job-criteria-subheader" not in match.group(1):
            continue
        label = _clean_text(match.group(2)).lower()
        remainder = html[match.end() :]
        value_match = re.search(
            r"<span\b([^>]*description__job-criteria-text[^>]*)>(.*?)</span>",
            remainder,
            re.I | re.S,
        )
        if not value_match:
            continue
        value = _clean_text(value_match.group(2))
        if not value:
            continue
        if "seniority" in label:
            criteria["seniority"] = value
        elif "employment" in label:
            criteria["employmentType"] = value
        elif "function" in label:
            criteria["jobFunction"] = value
        elif "industr" in label:
            criteria["industries"] = value
    return criteria


def parse_detail(html: str, job_id: str) -> tuple[dict[str, Any], bool, dict[str, str]]:
    """Return ``(RawJobInput, isActive, criteria)`` for one guest detail page."""
    description_block = _balanced_element(html, "div", "show-more-less-html__markup")
    if description_block is None:
        description_block = _balanced_element(html, "div", "description__text")
    description_start = description_block[0] if description_block else len(html)
    description_html = description_block[2] if description_block else ""
    description = _html_to_text(description_html)

    # isActive is scoped to the markup ABOVE the description. Recruiter copy
    # inside a real description often quotes "no longer accepting applications";
    # that must never mark a live posting as closed.
    body_start = html.lower().find("<body")
    above = html[body_start if body_start >= 0 else 0 : description_start].lower()
    is_active = not (
        "closed-job__flavor" in above or "no longer accepting applications" in above
    )

    title = _clean_text(_element_inner(html, "h1", "top-card-layout__title"))
    if not title:
        title = _clean_text(_element_inner(html, "h2", "top-card-layout__title"))
    if not title:
        title = _clean_text(_element_inner(html, "h2", "topcard__title"))
    if not title:
        title = _clean_text(_element_inner(html, "h1", "topcard__title"))

    company = _clean_text(_element_inner(html, "a", "topcard__org-name-link")) or None
    if not company:
        company = _clean_text(_element_inner(html, "span", "topcard__flavor")) or None

    location = _clean_text(_element_inner(html, "span", "topcard__flavor--bullet")) or None
    if not location:
        location = _clean_text(_element_inner(html, "span", "job-search-card__location")) or None

    posted_date = None
    time_match = _first_element_attrs(html, "time", "job-search-card__listdate")
    if time_match:
        posted_date = _iso_date(_attr(time_match[0], "datetime"))
    if not posted_date:
        time_match = _first_element_attrs(html, "time", "posted-time-ago__text")
        if time_match:
            posted_date = _iso_date(_attr(time_match[0], "datetime"))

    job = {
        "source": SOURCE,
        "sourceJobId": job_id,
        "title": title or "Unknown",
        "company": company,
        "location": location,
        "salaryText": None,
        "descriptionText": description[:12000] or None,
        "url": JOB_VIEW_URL.format(job_id=job_id),
        "postedDate": posted_date,
        "closingDate": None,
    }
    return job, is_active, parse_criteria(html)


def extract_job_id(value: str) -> str:
    """Accept a bare id, a ``/jobs/view/<id>/`` URL, or a jobPosting URN."""
    text = (value or "").strip()
    if re.fullmatch(r"\d{6,20}", text):
        return text
    match = re.search(r"(?:urn:li:jobPosting:|/jobs/view/)(\d{6,20})", text, re.I)
    if match:
        return match.group(1)
    raise CliError("NO_ID", f"Could not extract a LinkedIn job id from {value!r}")


# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------


def _raw_page(url: str, raw_json: dict[str, Any]) -> dict[str, Any]:
    return {
        "source": SOURCE,
        "url": url,
        "rawHtml": None,
        "markdown": None,
        "rawJson": json.dumps(raw_json, ensure_ascii=False),
        "crawledAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    }


def _envelope(
    jobs: list[dict[str, Any]],
    *,
    url: str,
    meta: dict[str, Any],
    is_active_by_id: dict[str, bool] | None = None,
    criteria_by_id: dict[str, dict[str, str]] | None = None,
) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "source": SOURCE,
        "rawPages": [_raw_page(url, meta)],
        "jobs": jobs,
        "isComplete": bool(meta.get("isComplete", True)),
        "meta": meta,
    }
    if is_active_by_id:
        payload["isActiveById"] = is_active_by_id
    if criteria_by_id:
        payload["criteriaById"] = criteria_by_id
    return payload


def _render_table(payload: dict[str, Any], detail_mode: bool) -> str:
    jobs = payload.get("jobs", [])
    criteria = payload.get("criteriaById", {})
    active = payload.get("isActiveById", {})
    lines: list[str] = []
    if detail_mode:
        for job in jobs:
            job_id = str(job.get("sourceJobId"))
            lines.append(f"{job.get('title')} @ {job.get('company') or '?'}")
            lines.append(f"  id:       {job_id}")
            lines.append(f"  location: {job.get('location') or '?'}")
            lines.append(f"  date:     {job.get('postedDate') or '?'}")
            lines.append(f"  url:      {job.get('url')}")
            lines.append(f"  active:   {active.get(job_id)}")
            for name, value in (criteria.get(job_id) or {}).items():
                lines.append(f"  {name}: {value}")
            description = (job.get("descriptionText") or "").strip()
            if description:
                lines.append("")
                lines.append(description[:2000])
        return "\n".join(lines)
    lines.append(f"{'ID':<12}  {'DATE':<10}  {'COMPANY':<28}  {'LOCATION':<26}  TITLE")
    for job in jobs:
        job_id = str(job.get("sourceJobId"))
        lines.append(
            f"{job_id:<12}  {(job.get('postedDate') or '?'):<10}  "
            f"{(job.get('company') or '?')[:28]:<28}  "
            f"{(job.get('location') or '?')[:26]:<26}  {job.get('title')}"
        )
    return "\n".join(lines)


def _emit(payload: dict[str, Any], output_format: str, detail_mode: bool) -> None:
    if output_format == "table":
        print(_render_table(payload, detail_mode))
    else:
        print(json.dumps(payload, ensure_ascii=False, indent=2))


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------


def run_search(args: argparse.Namespace, fetch: Callable[[str], str] = html_fetch) -> dict[str, Any]:
    budget = RequestBudget()
    search_url = build_search_url(args.keywords, args.location, args.jobage, args.page)
    budget.spend(search_url)
    fragment = fetch(search_url)
    cards = parse_search_cards(fragment) if fragment else []

    deduped: list[dict[str, Any]] = []
    seen: set[str] = set()
    for card in cards:
        if card["id"] in seen:
            continue
        seen.add(card["id"])
        deduped.append(card)
    selected = deduped[: args.max_jobs]
    detail_limit = min(
        getattr(args, "detail_limit", DEFAULT_DETAIL_LIMIT) or DEFAULT_DETAIL_LIMIT,
        MAX_DETAIL_LIMIT,
    )

    jobs: list[dict[str, Any]] = []
    active_by_id: dict[str, bool] = {}
    criteria_by_id: dict[str, dict[str, str]] = {}
    detail_fetches = 0
    for index, card in enumerate(selected):
        if args.dry_run or index >= detail_limit:
            jobs.append(_raw_job_from_card(card))
            continue
        detail_url = DETAIL_ENDPOINT.format(job_id=card["id"])
        budget.spend(detail_url)
        detail_html = fetch(detail_url)
        detail_fetches += 1
        if detail_html:
            detail_job, is_active, criteria = parse_detail(detail_html, card["id"])
            jobs.append(detail_job)
            active_by_id[card["id"]] = is_active
            if criteria:
                criteria_by_id[card["id"]] = criteria
        else:
            # 404 detail: keep the search card, but do not invent ghost status.
            jobs.append(_raw_job_from_card(card))
        if index < len(selected) - 1 and index + 1 < detail_limit:
            time.sleep(args.delay)

    meta = {
        "count": len(jobs),
        "page": args.page,
        "keywords": args.keywords,
        "location": args.location,
        "jobage": args.jobage,
        "maxJobs": args.max_jobs,
        "detailLimit": detail_limit,
        "detailFetches": detail_fetches,
        "requestBudget": budget.limit,
        "requestsUsed": budget.used,
        "dryRun": bool(args.dry_run),
        "delay": args.delay,
        "isComplete": args.dry_run or len(jobs) >= args.max_jobs,
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    }
    return _envelope(
        jobs,
        url=search_url,
        meta=meta,
        is_active_by_id=active_by_id,
        criteria_by_id=criteria_by_id,
    )


def run_detail(args: argparse.Namespace, fetch: Callable[[str], str] = html_fetch) -> dict[str, Any]:
    job_id = extract_job_id(args.detail)
    detail_url = DETAIL_ENDPOINT.format(job_id=job_id)
    html = fetch(detail_url)
    if not html:
        raise CliError("NOT_FOUND", f"LinkedIn returned no detail markup for job {job_id}")
    job, is_active, criteria = parse_detail(html, job_id)
    meta = {
        "count": 1,
        "page": 1,
        "detail": job_id,
        "isComplete": True,
        "generatedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    }
    return _envelope(
        [job],
        url=detail_url,
        meta=meta,
        is_active_by_id={job_id: is_active},
        criteria_by_id={job_id: criteria} if criteria else None,
    )


def _build_parser() -> argparse.ArgumentParser:
    parser = _ContractParser(
        description=(
            "Scheduled personal-use LinkedIn guest-endpoint crawler under the "
            "operator's 2026-10-23 exemption. linkedin.com robots.txt disallows "
            "all automated agents; this scoped allowlist-of-one exception is for "
            "LinkedIn guest crawling only. Honest UA, no login, low volume."
        )
    )
    parser.add_argument("--keywords", type=str, default=None, help="Search keywords")
    parser.add_argument("--location", type=str, default=None, help="Location (required for search)")
    parser.add_argument("--jobage", type=_parse_whole_positive, default=None, help="Posted within N days")
    parser.add_argument(
        "--max-jobs",
        type=_parse_max_jobs,
        default=DEFAULT_MAX_JOBS,
        help=f"Max results (hard cap {MAX_JOBS_PER_RUN})",
    )
    parser.add_argument("--page", type=_parse_whole_positive, default=1, help="1-indexed results page")
    parser.add_argument(
        "--detail-limit",
        type=_parse_detail_limit,
        default=DEFAULT_DETAIL_LIMIT,
        help=f"Max detail fetches per run (hard cap {MAX_DETAIL_LIMIT})",
    )
    parser.add_argument("--detail", type=str, default=None, help="Fetch one posting: id, URL, or URN")
    parser.add_argument("--format", choices=("json", "table"), default="json", help="Output format")
    parser.add_argument("--dry-run", action="store_true", help="Search only; do not fetch details")
    parser.add_argument(
        "--delay",
        type=_parse_delay,
        default=DEFAULT_DELAY_SECONDS,
        help=f"Seconds between requests (default {DEFAULT_DELAY_SECONDS}, minimum {MIN_DELAY_SECONDS})",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = _build_parser()
    try:
        args = parser.parse_args(argv)
        if args.detail and args.dry_run:
            raise CliError("BAD_ARG", "--detail cannot be combined with --dry-run")
        if args.detail:
            payload = run_detail(args)
            _emit(payload, args.format, detail_mode=True)
            return 0
        if not args.location:
            raise CliError("NO_LOCATION", "--location is required for a search")
        payload = run_search(args)
        _emit(payload, args.format, detail_mode=False)
        return 0
    except CliError as exc:
        print(json.dumps({"error": exc.message, "code": exc.code}), file=sys.stderr)
        return 1
    except BlockedError as exc:
        print(json.dumps({"error": str(exc), "code": "BLOCKED"}), file=sys.stderr)
        return 1
    except FetchError as exc:
        print(json.dumps({"error": str(exc), "code": "FETCH_FAILED"}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
