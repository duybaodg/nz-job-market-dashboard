"""Public, unauthenticated JSON postings APIs for common ATS platforms.

Each ``*_jobs`` function takes a ``fetch`` callable and returns a list of plain
dicts using the shared ``RawJobInput`` field names, so the caller never has to
know the platform payload shape.  The worker passes an HTTP JSON fetcher; tests
pass a lambda returning a recorded fixture, which keeps every mapper pure and
offline-testable.

``fetch`` contract::

    fetch(url: str, method: str = "GET", payload: dict | None = None) -> Any

It must return decoded JSON (dict/list) and raise on transport failure.
"""
from __future__ import annotations

import re
from datetime import datetime, timedelta, timezone
from html import unescape
from typing import Any, Callable, Iterable, Mapping

JsonFetcher = Callable[..., Any]

#: Platforms whose public API this module can crawl directly.
API_PLATFORMS = (
    "ashby",
    "greenhouse",
    "lever",
    "workable",
    "smartrecruiters",
    "bamboohr",
    "workday",
    "tribe",
    "oracle",
    "salesforce",
)

_TAG_RE = re.compile(r"<[^>]+>")
_SCRIPT_RE = re.compile(r"<(script|style)\b[^<]*(?:(?!</\1>)<[^<]*)*</\1>", re.I | re.S)
_SPACE_RE = re.compile(r"\s+")


def clean_text(value: Any) -> str:
    """Strip HTML tags/entities and collapse whitespace."""
    if value is None:
        return ""
    text = str(value)
    text = _SCRIPT_RE.sub(" ", text)
    text = _TAG_RE.sub(" ", text)
    text = unescape(text)
    return _SPACE_RE.sub(" ", text).strip()


def iso_date(value: Any) -> str | None:
    """Normalise a date-ish value to ``YYYY-MM-DDT00:00:00Z`` when possible."""
    if value in (None, ""):
        return None
    if isinstance(value, (int, float)):
        # Unix seconds or milliseconds since epoch.
        seconds = value / 1000 if value > 10_000_000_000 else value
        try:
            return datetime.fromtimestamp(seconds, tz=timezone.utc).strftime("%Y-%m-%dT00:00:00Z")
        except (OverflowError, OSError, ValueError):
            return None
    match = re.search(r"\d{4}-\d{2}-\d{2}", str(value))
    return f"{match.group(0)}T00:00:00Z" if match else None


def relative_date(value: Any, now: datetime | None = None) -> str | None:
    """Convert Workday-style ``Posted Yesterday`` / ``Posted 3 Days Ago`` text."""
    if not value:
        return None
    text = str(value).lower()
    explicit = iso_date(value)
    if explicit:
        return explicit
    reference = now or datetime.now(timezone.utc)
    if "today" in text:
        return reference.strftime("%Y-%m-%dT00:00:00Z")
    match = re.search(r"(\d+)\+?\s*day", text)
    if match:
        return (reference - timedelta(days=int(match.group(1)))).strftime("%Y-%m-%dT00:00:00Z")
    match = re.search(r"(\d+)\+?\s*(?:hour|hr)", text)
    if match:
        return reference.strftime("%Y-%m-%dT00:00:00Z")
    if "yesterday" in text:
        return (reference - timedelta(days=1)).strftime("%Y-%m-%dT00:00:00Z")
    return None


def make_job(
    *,
    source: str,
    title: str,
    url: str,
    source_job_id: Any = None,
    company: str | None = None,
    location: str | None = None,
    salary_text: str | None = None,
    description: str | None = None,
    posted_date: str | None = None,
    closing_date: str | None = None,
    department: str | None = None,
) -> dict[str, Any]:
    return {
        "source": source,
        "sourceJobId": str(source_job_id) if source_job_id not in (None, "") else None,
        "title": clean_text(title) or "Untitled job",
        "company": clean_text(company) or None,
        "location": clean_text(location) or None,
        "salaryText": clean_text(salary_text) or None,
        "descriptionText": (clean_text(description) or None),
        "url": url,
        "postedDate": posted_date,
        "closingDate": closing_date,
        # Not part of RawJobInput: used by crawl_platform to narrow broad boards
        # (for example Rocket Lab's engineering-heavy Greenhouse board) and
        # stripped again before the rows reach the worker.
        "department": clean_text(department) or None,
    }


def _as_list(value: Any, *keys: str) -> list[Any]:
    if isinstance(value, list):
        return value
    if isinstance(value, Mapping):
        for key in keys:
            nested = value.get(key)
            if isinstance(nested, list):
                return nested
    return []


def _first_name(value: Any) -> str | None:
    if isinstance(value, Mapping):
        value = value.get("name")
    return str(value) if value else None


# --------------------------------------------------------------------------- #
# Ashby — https://api.ashbyhq.com/posting-api/job-board/{token}
# --------------------------------------------------------------------------- #
def ashby_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    token: str,
    company: str | None = None,
    country: str | None = None,
    require_terms: Iterable[str] = (),
    only_listed: bool = True,
) -> list[dict[str, Any]]:
    payload = fetch(
        f"https://api.ashbyhq.com/posting-api/job-board/{token}?includeCompensation=true"
    )
    terms = tuple(term.lower() for term in require_terms)
    jobs: list[dict[str, Any]] = []
    for value in _as_list(payload, "jobs"):
        if not isinstance(value, Mapping):
            continue
        if only_listed and not value.get("isListed", True):
            continue
        address = ((value.get("address") or {}).get("postalAddress") or {}) if isinstance(value.get("address"), Mapping) else {}
        if country and str(address.get("addressCountry") or "").strip().lower() != country.lower():
            continue
        searchable = " ".join(str(value.get(field) or "") for field in ("title", "department", "team")).lower()
        if terms and not any(term in searchable for term in terms):
            continue
        employment = {"FullTime": "Full-time", "PartTime": "Part-time"}.get(
            str(value.get("employmentType") or ""), str(value.get("employmentType") or "")
        )
        workplace = {"OnSite": "On-site", "Remote": "Remote", "Hybrid": "Hybrid"}.get(
            str(value.get("workplaceType") or ""), str(value.get("workplaceType") or "")
        )
        prefix = ". ".join(
            part
            for part in (
                f"Employment type: {employment}" if employment else "",
                f"Work mode: {workplace}" if workplace else "",
            )
            if part
        )
        description = clean_text(value.get("descriptionPlain") or value.get("descriptionHtml"))
        compensation = value.get("compensation") if isinstance(value.get("compensation"), Mapping) else {}
        jobs.append(
            make_job(
                source=source,
                source_job_id=value.get("id"),
                title=str(value.get("title") or "Untitled job"),
                company=company,
                location=address.get("addressLocality") or value.get("location"),
                salary_text=compensation.get("scrapeableCompensationSalarySummary"),
                description=f"{prefix}. {description}" if prefix else description,
                url=str(value.get("jobUrl") or value.get("applyUrl") or ""),
                posted_date=iso_date(value.get("publishedAt")),
            )
        )
    return [job for job in jobs if job["url"]]


# --------------------------------------------------------------------------- #
# Greenhouse — https://boards-api.greenhouse.io/v1/boards/{token}/jobs
# --------------------------------------------------------------------------- #
def greenhouse_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    token: str,
    company: str | None = None,
) -> list[dict[str, Any]]:
    payload = fetch(
        f"https://boards-api.greenhouse.io/v1/boards/{token}/jobs?content=true"
    )
    jobs: list[dict[str, Any]] = []
    for value in _as_list(payload, "jobs"):
        if not isinstance(value, Mapping):
            continue
        location = value.get("location") or {}
        if isinstance(location, Mapping):
            location = location.get("name")
        departments = value.get("departments")
        department = None
        if isinstance(departments, list):
            names = [
                str(item.get("name"))
                for item in departments
                if isinstance(item, Mapping) and item.get("name")
            ]
            department = "; ".join(names) if names else None
        jobs.append(
            make_job(
                source=source,
                source_job_id=value.get("id"),
                title=str(value.get("title") or "Untitled job"),
                company=company or _first_name(value.get("company_name")),
                location=location,
                description=value.get("content"),
                url=str(value.get("absolute_url") or ""),
                posted_date=iso_date(value.get("first_published") or value.get("updated_at")),
                department=department,
            )
        )
    return [job for job in jobs if job["url"]]


# --------------------------------------------------------------------------- #
# Lever — https://api.lever.co/v0/postings/{token}?mode=json
# --------------------------------------------------------------------------- #
def lever_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    token: str,
    company: str | None = None,
) -> list[dict[str, Any]]:
    payload = fetch(f"https://api.lever.co/v0/postings/{token}?mode=json")
    jobs: list[dict[str, Any]] = []
    for value in _as_list(payload):
        if not isinstance(value, Mapping):
            continue
        categories = value.get("categories") if isinstance(value.get("categories"), Mapping) else {}
        salary = value.get("salaryRange") if isinstance(value.get("salaryRange"), Mapping) else {}
        salary_text = None
        if salary.get("min") and salary.get("max"):
            salary_text = f"${salary['min']} - ${salary['max']} {salary.get('currency', '')}".strip()
        jobs.append(
            make_job(
                source=source,
                source_job_id=value.get("id"),
                title=str(value.get("text") or "Untitled job"),
                company=company,
                location=categories.get("location"),
                salary_text=salary_text,
                description=value.get("descriptionPlain") or value.get("description"),
                url=str(value.get("hostedUrl") or value.get("applyUrl") or ""),
                posted_date=iso_date(value.get("createdAt")),
            )
        )
    return [job for job in jobs if job["url"]]


# --------------------------------------------------------------------------- #
# Workable — https://apply.workable.com/api/v1/widget/accounts/{token}
# --------------------------------------------------------------------------- #
def workable_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    token: str,
    company: str | None = None,
) -> list[dict[str, Any]]:
    payload = fetch(
        f"https://apply.workable.com/api/v1/widget/accounts/{token}?details=true"
    )
    account_name = payload.get("name") if isinstance(payload, Mapping) else None
    jobs: list[dict[str, Any]] = []
    for value in _as_list(payload, "jobs"):
        if not isinstance(value, Mapping):
            continue
        location = value.get("location") if isinstance(value.get("location"), Mapping) else {}
        parts = [location.get("city"), location.get("region"), location.get("country")]
        jobs.append(
            make_job(
                source=source,
                source_job_id=value.get("shortcode") or value.get("id"),
                title=str(value.get("title") or "Untitled job"),
                company=company or account_name,
                location=", ".join(str(part) for part in parts if part),
                description=" ".join(
                    part
                    for part in (
                        clean_text(value.get("description")),
                        clean_text(value.get("requirements")),
                    )
                    if part
                ),
                url=str(value.get("url") or value.get("shortlink") or ""),
                posted_date=iso_date(value.get("created_at")),
            )
        )
    return [job for job in jobs if job["url"]]


# --------------------------------------------------------------------------- #
# SmartRecruiters — https://api.smartrecruiters.com/v1/companies/{token}/postings
# --------------------------------------------------------------------------- #
def smartrecruiters_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    token: str,
    company: str | None = None,
    limit: int = 100,
) -> list[dict[str, Any]]:
    payload = fetch(
        f"https://api.smartrecruiters.com/v1/companies/{token}/postings?limit={limit}"
    )
    jobs: list[dict[str, Any]] = []
    for value in _as_list(payload, "content"):
        if not isinstance(value, Mapping):
            continue
        location = value.get("location") if isinstance(value.get("location"), Mapping) else {}
        parts = [location.get("city"), location.get("region"), location.get("country")]
        employment = value.get("typeOfEmployment")
        if isinstance(employment, Mapping):
            employment = employment.get("label")
        identifier = value.get("id") or value.get("uuid")
        # The list API's `ref` points at the API; the canonical public posting is
        # https://jobs.smartrecruiters.com/{company}/{id}.
        jobs.append(
            make_job(
                source=source,
                source_job_id=identifier,
                title=str(value.get("name") or "Untitled job"),
                company=company or _first_name(value.get("company")),
                location=", ".join(str(part) for part in parts if part),
                description=str(employment or ""),
                url=f"https://jobs.smartrecruiters.com/{token}/{identifier}",
                posted_date=iso_date(value.get("releasedDate")),
            )
        )
    return [job for job in jobs if job["url"]]


# --------------------------------------------------------------------------- #
# BambooHR — https://{token}.bamboohr.com/careers/list
# --------------------------------------------------------------------------- #
def bamboohr_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    token: str,
    company: str | None = None,
) -> list[dict[str, Any]]:
    payload = fetch(f"https://{token}.bamboohr.com/careers/list")
    jobs: list[dict[str, Any]] = []
    for value in _as_list(payload, "result"):
        if not isinstance(value, Mapping):
            continue
        location = value.get("location") if isinstance(value.get("location"), Mapping) else {}
        parts = [location.get("city"), location.get("state"), location.get("country")]
        job_id = value.get("id")
        jobs.append(
            make_job(
                source=source,
                source_job_id=job_id,
                title=str(value.get("jobOpeningName") or "Untitled job"),
                company=company,
                location=", ".join(str(part) for part in parts if part),
                description=str(value.get("employmentStatusLabel") or ""),
                url=f"https://{token}.bamboohr.com/careers/{job_id}",
                posted_date=iso_date(value.get("postedDate") or value.get("datePosted")),
            )
        )
    return [job for job in jobs if job["url"]]


# --------------------------------------------------------------------------- #
# Workday — POST https://{host}/wday/cxs/{tenant}/{site}/jobs
# --------------------------------------------------------------------------- #
def workday_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    host: str,
    tenant: str,
    site: str,
    company: str | None = None,
    limit: int = 100,
    search_text: str = "",
) -> list[dict[str, Any]]:
    api_url = f"https://{host}/wday/cxs/{tenant}/{site}/jobs"
    jobs: list[dict[str, Any]] = []
    offset = 0
    page_size = 20
    while offset < limit:
        payload = fetch(
            api_url,
            "POST",
            {
                "appliedFacets": {},
                "limit": page_size,
                "offset": offset,
                "searchText": search_text,
            },
        )
        postings = _as_list(payload, "jobPostings")
        if not postings:
            break
        for value in postings:
            if not isinstance(value, Mapping):
                continue
            external_path = str(value.get("externalPath") or "")
            bullets = value.get("bulletFields") if isinstance(value.get("bulletFields"), list) else []
            jobs.append(
                make_job(
                    source=source,
                    source_job_id=bullets[-1] if bullets else None,
                    title=str(value.get("title") or "Untitled job"),
                    company=company,
                    location=value.get("locationsText"),
                    description=", ".join(str(bullet) for bullet in bullets),
                    url=f"https://{host}/en-US/{site}{external_path}" if external_path else "",
                    posted_date=relative_date(value.get("postedOn")),
                )
            )
        total = payload.get("total") if isinstance(payload, Mapping) else None
        offset += page_size
        if isinstance(total, int) and offset >= total:
            break
    return [job for job in jobs if job["url"]]


# --------------------------------------------------------------------------- #
# Tribe Recruitment — public JSON feed at https://www.tribegroup.com/api/jobs
# --------------------------------------------------------------------------- #
def tribe_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    api_url: str = "https://www.tribegroup.com/api/jobs",
    web_base: str = "https://www.tribegroup.com/find-a-job/job-listings",
    company: str | None = None,
) -> list[dict[str, Any]]:
    payload = fetch(api_url)
    jobs: list[dict[str, Any]] = []
    for value in _as_list(payload, "jobs"):
        if not isinstance(value, Mapping):
            continue
        salary = value.get("salary")
        salary_unit = str(value.get("salaryUnit") or "").strip()
        salary_text = None
        if salary:
            salary_text = f"${int(salary):,}" + (f" {salary_unit}" if salary_unit else "")
        classifications = value.get("classifications")
        classification_text = ", ".join(str(item) for item in classifications) if isinstance(classifications, list) else str(classifications or "")
        body = value.get("body")
        body_text = " ".join(str(part) for part in body) if isinstance(body, list) else str(body or "")
        description = " ".join(
            part
            for part in (
                classification_text,
                body_text,
                clean_text(value.get("description")),
                clean_text(value.get("benefits")),
            )
            if part
        )
        job_id = value.get("jobId") or str(value.get("objectID") or "").replace("job-", "")
        jobs.append(
            make_job(
                source=source,
                source_job_id=job_id,
                title=str(value.get("title") or "Untitled job"),
                company=company or value.get("brand"),
                location=value.get("location") or value.get("city"),
                salary_text=salary_text,
                description=description,
                url=f"{web_base.rstrip('/')}/{job_id}" if job_id else api_url,
                posted_date=iso_date(value.get("postedAt") or value.get("addedAt")),
            )
        )
    return [job for job in jobs if job["url"]]


# --------------------------------------------------------------------------- #
# Oracle Recruiting Cloud — public Candidate Experience requisitions API
# --------------------------------------------------------------------------- #
def oracle_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    host: str,
    site: str,
    company: str | None = None,
    limit: int = 200,
    country_code: str | None = None,
) -> list[dict[str, Any]]:
    """Read jobs from an Oracle Recruiting Cloud career site.

    The Candidate Experience site calls ``recruitingCEJobRequisitions`` with
    ``expand=requisitionList``; the same endpoint is public.
    """
    jobs: list[dict[str, Any]] = []
    offset = 0
    while offset < limit:
        page_size = min(200, limit - offset)
        finder = f"findReqs;siteNumber={site},limit={page_size},offset={offset},sortBy=POSTING_DATES_DESC"
        payload = fetch(
            f"https://{host}/hcmRestApi/resources/latest/recruitingCEJobRequisitions"
            f"?expand=requisitionList&finder={finder}"
        )
        items = _as_list(payload, "items")
        requisitions: list[Any] = []
        if items and isinstance(items[0], Mapping):
            requisitions = _as_list(items[0], "requisitionList")
        if not requisitions:
            break

        for value in requisitions:
            if not isinstance(value, Mapping):
                continue
            country = str(value.get("PrimaryLocationCountry") or "").strip()
            if country_code and country and country.upper() != country_code.upper():
                continue
            job_id = value.get("Id")
            jobs.append(
                make_job(
                    source=source,
                    source_job_id=job_id,
                    title=str(value.get("Title") or "Untitled job"),
                    company=company,
                    location=value.get("PrimaryLocation") or country or None,
                    description=value.get("ShortDescriptionStr"),
                    url=(
                        f"https://{host}/hcmUI/CandidateExperience/en/sites/{site}/job/{job_id}"
                        if job_id
                        else f"https://{host}/hcmUI/CandidateExperience/en/sites/{site}"
                    ),
                    posted_date=iso_date(value.get("PostedDate")),
                    closing_date=iso_date(value.get("PostingEndDate")),
                )
            )

        total = 0
        if items and isinstance(items[0], Mapping):
            try:
                total = int(items[0].get("TotalJobsCount") or 0)
            except (TypeError, ValueError):
                total = 0
        offset += page_size
        if total and offset >= total:
            break
    return [job for job in jobs if job["url"]]


# --------------------------------------------------------------------------- #
# Salesforce Experience Cloud — public Apex ``execute`` endpoint
# --------------------------------------------------------------------------- #
def salesforce_jobs(
    fetch: JsonFetcher,
    *,
    source: str,
    api_url: str,
    portal_url: str,
    company: str | None = None,
    title_field: str = "Title_Plain__c",
) -> list[dict[str, Any]]:
    """Read vacancies from a Salesforce Experience Cloud Apex endpoint.

    Lightning Web Components fetch guest-accessible data through
    ``/webruntime/api/apex/execute``; NZ Defence exposes ``getOpenVacancies``
    that way. Salesforce does not publish a public per-vacancy page here, so the
    job links back to the portal and the Salesforce record id is kept as the
    source job id (which keeps records distinct and deduplicated).
    """
    payload = fetch(api_url)
    values = _as_list(payload, "returnValue")
    jobs: list[dict[str, Any]] = []
    for value in values:
        if not isinstance(value, Mapping):
            continue
        title = clean_text(value.get(title_field)) or clean_text(value.get("Name"))
        if not title:
            continue
        category = clean_text(value.get("Vacancy_Category__c"))
        work_type = clean_text(value.get("Work_Type__c"))
        description = ". ".join(part for part in (category, work_type) if part)
        jobs.append(
            make_job(
                source=source,
                source_job_id=value.get("Id"),
                title=title,
                company=company,
                location=value.get("Region_Location__c") or value.get("New_Region__c"),
                description=description,
                url=portal_url,
                closing_date=iso_date(value.get("Application_Close_Date__c")),
            )
        )
    return jobs


# --------------------------------------------------------------------------- #
# Dispatcher
# --------------------------------------------------------------------------- #
def crawl_platform(
    platform: str,
    fetch: JsonFetcher,
    *,
    source: str,
    config: Mapping[str, Any],
) -> list[dict[str, Any]]:
    """Run the API client for ``platform`` using a registry entry's config.

    ``config`` may contain: ``token``, ``company``, ``country``, ``requireTerms``,
    ``locationTerms`` (keep only jobs whose location matches one of these), and
    ``host``/``tenant``/``site`` (Workday).
    """
    jobs = _dispatch_platform(platform, fetch, source=source, config=config)
    location_terms = [str(term).lower() for term in (config.get("locationTerms") or [])]
    if location_terms:
        jobs = [
            job
            for job in jobs
            if any(term in (job.get("location") or "").lower() for term in location_terms)
        ]
    department_terms = [str(term).lower() for term in (config.get("departmentTerms") or [])]
    if department_terms:
        jobs = [
            job
            for job in jobs
            if any(term in (job.get("department") or "").lower() for term in department_terms)
        ]
    # "department" is filter-only metadata; RawJobInput does not accept it.
    for job in jobs:
        job.pop("department", None)
    return jobs


def _dispatch_platform(
    platform: str,
    fetch: JsonFetcher,
    *,
    source: str,
    config: Mapping[str, Any],
) -> list[dict[str, Any]]:
    platform = platform.lower()
    company = config.get("company")
    if platform == "ashby":
        return ashby_jobs(
            fetch,
            source=source,
            token=config["token"],
            company=company,
            country=config.get("country"),
            require_terms=config.get("requireTerms") or (),
        )
    if platform == "greenhouse":
        return greenhouse_jobs(fetch, source=source, token=config["token"], company=company)
    if platform == "lever":
        return lever_jobs(fetch, source=source, token=config["token"], company=company)
    if platform == "workable":
        return workable_jobs(fetch, source=source, token=config["token"], company=company)
    if platform == "smartrecruiters":
        return smartrecruiters_jobs(fetch, source=source, token=config["token"], company=company)
    if platform == "bamboohr":
        return bamboohr_jobs(fetch, source=source, token=config["token"], company=company)
    if platform == "workday":
        return workday_jobs(
            fetch,
            source=source,
            host=config["host"],
            tenant=config.get("tenant") or config["host"].split(".")[0],
            site=config["site"],
            company=company,
            limit=int(config.get("limit", 100)),
            search_text=str(config.get("searchText", "")),
        )
    if platform == "tribe":
        return tribe_jobs(
            fetch,
            source=source,
            api_url=str(config.get("apiUrl") or config.get("api_url") or "https://www.tribegroup.com/api/jobs"),
            web_base=str(config.get("webBase") or config.get("web_base") or "https://www.tribegroup.com/find-a-job/job-listings"),
            company=company,
        )
    if platform == "oracle":
        return oracle_jobs(
            fetch,
            source=source,
            host=str(config["host"]),
            site=str(config["site"]),
            company=company,
            limit=int(config.get("limit", 200)),
            country_code=config.get("countryCode"),
        )
    if platform == "salesforce":
        return salesforce_jobs(
            fetch,
            source=source,
            api_url=str(config.get("apiUrl") or config.get("api_url") or ""),
            portal_url=str(
                config.get("portalUrl") or config.get("portal_url") or config.get("apiUrl") or config.get("api_url") or ""
            ),
            company=company,
            title_field=str(config.get("titleField") or config.get("title_field") or "Title_Plain__c"),
        )
    raise ValueError(f"Unsupported API platform: {platform}")
