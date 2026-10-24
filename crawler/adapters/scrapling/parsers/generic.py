"""Generic / Schema.org JobPosting parsing.

Round 19: mechanically moved out of ``scrapling_worker.py``.
"""
from __future__ import annotations

import json
import re
from html import unescape
from typing import Any

from models import RawJobInput
from normalisation import clean_text, extract_salary, iso_date

#: Schema.org JSON-LD script block. Some boards (Xero/Happydance) encode the
#: ``+`` in ``application/ld+json`` as ``&#x2B;``/``&#43;``, so accept the HTML
#: entities as well as the literal plus or the JobPosting is silently missed.
LD_JSON_SCRIPT_RE = re.compile(
    r'<script[^>]+type=["\'][^"\']*application/ld(?:\+|&#x2B;|&#43;)json[^"\']*["\'][^>]*>(.*?)</script>',
    re.I | re.S,
)


def parse_structured_jobs(html: str) -> list[dict[str, Any]]:
    """Return *every* Schema.org JobPosting on the page.

    Aggregator and listing pages can embed several JobPosting blocks (or an
    ``@graph`` containing them); harvesting all of them is far cheaper than
    following each detail link.
    """
    postings: list[dict[str, Any]] = []
    for match in LD_JSON_SCRIPT_RE.finditer(html):
        try:
            value = json.loads(unescape(match.group(1)))
        except json.JSONDecodeError:
            continue
        queue = list(value) if isinstance(value, list) else [value]
        while queue:
            item = queue.pop(0)
            if not isinstance(item, dict):
                continue
            if "@graph" in item:
                graph = item["@graph"]
                queue.extend(graph if isinstance(graph, list) else [graph])
            elif item.get("@type") == "JobPosting" and clean_text(str(item.get("title") or "")):
                # A JobPosting without a title is unusable — some WordPress
                # themes (Talent International) emit an otherwise-empty block.
                postings.append(item)
    return postings


def structured_location(source_data: dict[str, Any]) -> str | None:
    job_location = source_data.get("jobLocation") or {}
    if isinstance(job_location, list):
        job_location = job_location[0] if job_location else {}
    address = job_location.get("address") or {} if isinstance(job_location, dict) else {}
    if isinstance(address, str):
        return clean_text(address) or None

    parts = [address.get(key) for key in ("addressLocality", "addressRegion", "addressCountry")]
    cleaned_parts = []
    for part in parts:
        if not part:
            continue
        if isinstance(part, dict):
            part = part.get("name") or part.get("addressLocality") or part.get("addressRegion")
        value = clean_text(str(part)) if part else ""
        if value:
            cleaned_parts.append(value)
    unique_parts = list(dict.fromkeys(cleaned_parts))
    return ", ".join(unique_parts) or None


def structured_salary(source_data: dict[str, Any], text: str) -> str | None:
    """Prefer Schema.org baseSalary over a whole-page text scan.

    A detail page often carries a salary-filter widget ("Salary Annual Wage $0
    $300,000+") in its navigation. Scanning the page text picked that bound up as
    the advertised salary (the ZEIL support-role $300,000 misparse). The JSON-LD
    ``baseSalary`` is the authoritative figure, so use it when present and only
    fall back to the text scan otherwise.
    """
    base = source_data.get("baseSalary")
    if not isinstance(base, dict):
        return extract_salary(text)

    currency = str(base.get("currency") or "NZD").strip() or "NZD"
    value = base.get("value")
    unit = ""
    minimum = maximum = single = None

    if isinstance(value, dict):
        unit = str(value.get("unitText") or "").upper()
        single = value.get("value")
        minimum = value.get("minValue")
        maximum = value.get("maxValue")
    elif value is not None:
        single = value

    suffix = {"HOUR": " per hour", "DAY": " per day", "YEAR": " per year"}.get(unit, "")

    def render(amount: Any) -> str | None:
        text_value = str(amount).strip()
        if not text_value or text_value.lower() == "none":
            return None
        return f"{currency} {text_value}"

    if minimum is not None and maximum is not None:
        low, high = render(minimum), render(maximum)
        if low and high:
            return f"{low} - {high}{suffix}"

    if single is not None:
        rendered = render(single)
        if rendered:
            return f"{rendered}{suffix}"

    for amount in (minimum, maximum):
        rendered = render(amount)
        if rendered:
            return f"{rendered}{suffix}"

    return extract_salary(text)


def structured_job_to_raw(
    source: str,
    url: str,
    source_data: dict[str, Any],
    text: str,
    fallback_title: str,
) -> RawJobInput:
    """Map a Schema.org JobPosting dict onto the shared raw job shape."""
    location = structured_location(source_data)
    organization = source_data.get("hiringOrganization") or {}
    company = organization.get("name") if isinstance(organization, dict) else organization
    # Schema.org allows "identifier" to be a plain string as well as an object
    # (ZEIL emits a bare string), so guard the nested lookup.
    identifier = source_data.get("identifier") or {}
    source_job_id = identifier.get("value") if isinstance(identifier, dict) else identifier
    employment_type = str(source_data.get("employmentType") or "")
    if isinstance(employment_type, list):
        employment_type = employment_type[0] if employment_type else ""
    employment_type = employment_type.replace("_", "-").title()
    description = clean_text(str(source_data.get("description") or text))
    if employment_type:
        description = f"{employment_type}. {description}"
    job_url = str(source_data.get("url") or url)
    return RawJobInput(
        source=source,
        sourceJobId=str(source_job_id) if source_job_id else None,
        title=clean_text(str(source_data.get("title") or fallback_title)),
        company=clean_text(str(company)) or None,
        location=clean_text(str(location)) or None,
        salaryText=structured_salary(source_data, text),
        descriptionText=description[:12000] or None,
        url=job_url,
        postedDate=iso_date(source_data.get("datePosted")),
        closingDate=iso_date(source_data.get("validThrough")),
    )


def parse_structured_job(html: str) -> dict[str, Any] | None:
    for match in LD_JSON_SCRIPT_RE.finditer(html):
        try:
            value = json.loads(unescape(match.group(1)))
        except json.JSONDecodeError:
            continue
        values = value if isinstance(value, list) else [value]
        # Handle @graph wrapping (Schema.org with WordPress Yoast/SEO plugins)
        graph_items: list[dict[str, Any]] = []
        for v in values:
            if isinstance(v, dict) and "@graph" in v:
                graph = v["@graph"]
                graph_items.extend(graph if isinstance(graph, list) else [graph])
            else:
                graph_items.append(v)
        for item in graph_items:
            if (
                isinstance(item, dict)
                and item.get("@type") == "JobPosting"
                and clean_text(str(item.get("title") or ""))
            ):
                return item
    return None
