"""Corporate/ATS job-board parsers (Air NZ, Xero, PageUp, RSS, Halter).

Round 19: mechanically moved out of ``scrapling_worker.py``.
"""
from __future__ import annotations

import json
import re
from html import unescape
from urllib.parse import urljoin, urlparse

from models import RawJobInput
from normalisation import (
    clean_text,
    extract_label,
    extract_salary,
    html_group,
    human_date,
    iso_date,
    looks_like_it_job,
)
from parsers.generic import parse_structured_job, structured_location

HALTER_TECH_TERMS = (
    "software",
    "developer",
    "data",
    "machine learning",
    "cloud",
    "security",
    "network",
    "firmware",
    "mobile",
    "devops",
    "platform",
    "application",
    "product manager",
    "product designer",
)


def parse_airnz_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse Air New Zealand job detail pages (custom ATS + Schema.org)."""
    source_data = parse_structured_job(html)
    if source_data:
        location = structured_location(source_data)
        company = "Air New Zealand"
        source_job_id = (source_data.get("identifier") or {}).get("value")
        employment_type = str(source_data.get("employmentType") or "")
        if isinstance(employment_type, list):
            employment_type = employment_type[0] if employment_type else ""
        employment_type = employment_type.replace("_", "-").title()
        description = clean_text(str(source_data.get("description") or text))
        if employment_type:
            description = f"{employment_type}. {description}"
        return RawJobInput(
            source=source,
            sourceJobId=str(source_job_id) if source_job_id else None,
            title=clean_text(str(source_data.get("title") or fallback_title)),
            company=company,
            location=clean_text(str(location)) or None,
            salaryText=extract_salary(text),
            descriptionText=description[:12000] or None,
            url=url,
            postedDate=iso_date(source_data.get("datePosted")),
            closingDate=iso_date(source_data.get("validThrough")),
        )
    # Fallback
    title = html_group(html, r'<h1[^>]*>(.*?)</h1>') or fallback_title
    return RawJobInput(
        source=source,
        sourceJobId=None,
        title=title,
        company="Air New Zealand",
        location=None,
        salaryText=None,
        descriptionText=text[:12000] or None,
        url=url,
        postedDate=None,
        closingDate=None,
    )


def parse_airnz_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Parse Air NZ listing pages."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'href="(/job/([^"]+))"[^>]*>\s*((?:(?!Read more).)+?)\s*</a>',
        html, re.I | re.S,
    ):
        job_url = urljoin(url, match.group(1))
        title = clean_text(match.group(3))
        if job_url in seen or not title or title.lower() == "read more":
            continue
        seen.add(job_url)
        # Extract job ID from URL: ...-jid-830
        jid_match = re.search(r'jid-(\d+)', match.group(1))
        jobs.append(RawJobInput(
            source=source,
            sourceJobId=jid_match.group(1) if jid_match else None,
            title=title,
            company="Air New Zealand",
            location=None,
            salaryText=None,
            descriptionText=None,
            url=job_url,
            postedDate=None,
            closingDate=None,
        ))
    return jobs


MONTH_NUMBERS = {
    "jan": "01", "feb": "02", "mar": "03", "apr": "04", "may": "05", "jun": "06",
    "jul": "07", "aug": "08", "sep": "09", "oct": "10", "nov": "11", "dec": "12",
}


def pageup_date(value: str | None) -> str | None:
    """Parse PageUp's ``Thu Oct 01 00:00:00 UTC 2026`` date strings."""
    if not value:
        return None
    match = re.search(
        r"([A-Za-z]{3})\s+([A-Za-z]{3})\s+(\d{1,2})\s+\d{2}:\d{2}:\d{2}\s+\S+\s+(\d{4})", value
    )
    if not match:
        return iso_date(value)
    month = MONTH_NUMBERS.get(match.group(2).lower())
    if not month:
        return iso_date(value)
    return f"{match.group(4)}-{month}-{int(match.group(3)):02d}T00:00:00Z"


def parse_rfc822_date(value: str | None) -> str | None:
    """Parse RFC-822 dates used by RSS job feeds."""
    if not value:
        return None
    from email.utils import parsedate_to_datetime

    try:
        parsed = parsedate_to_datetime(value)
    except (TypeError, ValueError):
        return iso_date(value)
    if parsed is None:
        return iso_date(value)
    return parsed.strftime("%Y-%m-%dT00:00:00Z")


def parse_rss_jobs(source: str, xml: str) -> list[RawJobInput]:
    """Extract jobs from an RSS feed (for example SuccessFactors SearchJobs/feed).

    SuccessFactors career sites publish every vacancy in one RSS document, which
    is far cheaper and more reliable than rendering their JavaScript search page.
    """
    jobs: list[RawJobInput] = []
    seen: set[str] = set()

    for block in re.findall(r"<item\b.*?</item>", xml, re.I | re.S):
        def field(name: str) -> str | None:
            match = re.search(rf"<{name}\b[^>]*>(.*?)</{name}>", block, re.I | re.S)
            if not match:
                return None
            value = re.sub(r"^\s*<!\[CDATA\[(.*?)\]\]>\s*$", r"\1", match.group(1).strip(), flags=re.S)
            return value.strip() or None

        title = clean_text(field("title"))
        link = field("link") or field("guid")
        if not title or not link:
            continue
        link = link.strip()
        if link in seen:
            continue
        seen.add(link)
        job_id = re.search(r"/(\d+)/?$", urlparse(link).path)
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=job_id.group(1) if job_id else None,
                title=title,
                company=None,
                location=clean_text(field("location")) or None,
                salaryText=None,
                descriptionText=(clean_text(field("description")) or None),
                url=link,
                postedDate=parse_rfc822_date(field("pubDate")),
                closingDate=None,
            )
        )
    return jobs


def parse_pageup_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse a PageUp job page, which embeds Schema.org microdata."""

    def meta_content(prop: str) -> str | None:
        match = re.search(
            rf'itemprop=["\']{prop}["\'][^>]*content=["\']([^"\']*)["\']', html, re.I
        )
        return clean_text(match.group(1)) or None if match else None

    def block(prop: str) -> str | None:
        match = re.search(
            rf'<[^>]+itemprop=["\']{prop}["\'][^>]*>(.*?)</(?:div|section|span)>', html, re.S | re.I
        )
        return clean_text(match.group(1)) or None if match else None

    def long_block(prop: str) -> str | None:
        """Extract a large microdata block (PageUp descriptions nest many spans)."""
        marker = re.search(rf'itemprop=["\']{prop}["\'][^>]*>', html, re.I)
        if not marker:
            return None
        start = marker.end()
        boundary = html.find("itemprop=", start)
        chunk = html[start : boundary if boundary > start else start + 20000]
        value = clean_text(chunk)
        return value[:12000] if len(value) > 200 else None

    title = html_group(html, r"<h1[^>]*>(.*?)</h1>") or fallback_title
    title = re.sub(r"^\s*Title:\s*", "", title, flags=re.I)
    if not title:
        title = fallback_title

    location = meta_content("streetAddress")
    if not location:
        address = re.search(
            r'itemprop=["\']address["\'][^>]*>(.*?)</span>\s*</span>', html, re.S | re.I
        )
        location = clean_text(address.group(1)) if address else None
    if not location:
        location = extract_label(text, "Location")

    job_id = re.search(r"/job/[^/]+/(\d+)", url) or re.search(r"/(\d{5,})", url)
    return RawJobInput(
        source=source,
        sourceJobId=job_id.group(1) if job_id else None,
        title=title,
        company=meta_content("hiringOrganization"),
        location=location,
        salaryText=extract_salary(text),
        descriptionText=(long_block("description") or block("description") or text[:12000]) or None,
        url=url,
        postedDate=pageup_date(meta_content("datePosted")),
        closingDate=pageup_date(meta_content("validThrough")),
    )


#: Xero's careers board labels every technology role with one of these
#: industries. "Engineering" alone is not enough for the shared title gate
#: (which deliberately rejects civil/mechanical engineers), but on Xero's own
#: technology board it is a reliable IT signal.
XERO_TECH_INDUSTRIES = frozenset({"engineering", "data, ai & machine learning"})


def parse_xero_sitemap(xml: str) -> list[str]:
    """Return the unique job URLs from the careers.xero.com sitemap."""
    urls = [
        unescape(value).strip()
        for value in re.findall(r"<loc>\s*(.*?)\s*</loc>", xml, re.I | re.S)
        if "/jobs/" in value
    ]
    return list(
        dict.fromkeys(
            value
            for value in urls
            if re.search(r"/jobs/[0-9a-f-]{8,}(?:/|$)", value, re.I)
        )
    )


def xero_title_is_it_candidate(title: str) -> bool:
    """Broad slug prefilter before fetching a Xero detail page.

    A Xero posting costs ~1.6 MB, so fetch only role slugs that look technical.
    The authoritative gate still runs on the JobPosting title/industry after the
    fetch; this only avoids downloading 90+ non-technical roles.
    """
    if looks_like_it_job(title):
        return True
    lowered = title.lower()
    return any(
        term in lowered
        for term in (
            "engineer",
            "engineering",
            "platform",
            "cloud",
            "software",
            "developer",
            "data",
            "security",
            "architect",
            "analyst",
            "automation",
            "salesforce",
            "technical",
            "technology",
        )
    )


def parse_halter_ashby(source: str, payload_text: str) -> list[RawJobInput]:
    payload = json.loads(payload_text)
    values = payload.get("jobs")
    if not isinstance(values, list):
        raise ValueError("Ashby response does not contain a jobs array.")

    jobs: list[RawJobInput] = []
    for value in values:
        address = (value.get("address") or {}).get("postalAddress") or {}
        searchable = " ".join(
            str(value.get(field) or "") for field in ("title", "department", "team")
        ).lower()
        if (
            not value.get("isListed", True)
            or str(address.get("addressCountry") or "").lower() != "new zealand"
            or not any(term in searchable for term in HALTER_TECH_TERMS)
        ):
            continue

        employment = {
            "FullTime": "Full-time",
            "PartTime": "Part-time",
        }.get(str(value.get("employmentType") or ""), str(value.get("employmentType") or ""))
        workplace = {
            "OnSite": "On-site",
            "Remote": "Remote",
            "Hybrid": "Hybrid",
        }.get(str(value.get("workplaceType") or ""), str(value.get("workplaceType") or ""))
        description = clean_text(str(value.get("descriptionPlain") or ""))
        prefix = ". ".join(
            part for part in (
                f"Employment type: {employment}" if employment else "",
                f"Work mode: {workplace}" if workplace else "",
            ) if part
        )
        salary = (value.get("compensation") or {}).get("scrapeableCompensationSalarySummary")
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=str(value.get("id")) if value.get("id") else None,
                title=clean_text(str(value.get("title") or "Untitled job")),
                company="Halter",
                location=clean_text(str(address.get("addressLocality") or value.get("location") or "")) or None,
                salaryText=clean_text(str(salary)) or None,
                descriptionText=f"{prefix}. {description}"[:12000] if prefix else description[:12000] or None,
                url=str(value.get("jobUrl") or ""),
                postedDate=iso_date(value.get("publishedAt")),
                closingDate=None,
            )
        )
    return jobs
