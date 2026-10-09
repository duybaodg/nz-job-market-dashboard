#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from html import unescape
from html.parser import HTMLParser
from typing import Any
from urllib.parse import urljoin, urlparse
from urllib.robotparser import RobotFileParser


JOB_KEYWORDS = (
    "developer",
    "engineer",
    "analyst",
    "architect",
    "consultant",
    "administrator",
    "manager",
    "designer",
    "specialist",
    "graduate",
    "intern",
    "technician",
)

SKIP_LINK_WORDS = (
    "privacy",
    "terms",
    "login",
    "sign in",
    "register",
    "saved",
    "contact",
    "about",
)

ROBOTS_CACHE: dict[str, RobotFileParser] = {}

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

IT_KEYWORDS = frozenset({
    "software", "developer", "engineer", "data engineer", "cloud engineer",
    "cloud", "security engineer", "security analyst", "cyber",
    "devops", "solutions architect", "architect",
    "qa engineer", "qa analyst", "test engineer", "test analyst", "automation",
    "infrastructure", "platform engineer", "full stack", "frontend", "backend",
    "java developer", "net developer", "c# developer", "python developer",
    "javascript", "react developer", "angular developer",
    "it support", "help desk", "scrum master",
    "digital", "solutions", "technical lead", "technology",
    "programmer", "web developer",
    "database", "dba", "sql", "azure", "aws", "api", "mobile developer",
    "ios developer", "android developer",
    "saas", "integration",
    "power bi", "tableau", "bi developer",
    "machine learning", "artificial intelligence", "data scientist",
    "data analyst", "data engineer", "data architect", "data platform",
    "network engineer", "system engineer", "systems engineer",
    "linux", "embedded", "firmware",
    "ux designer", "ui designer", "product designer",
})


def looks_like_it_job(title: str, description: str = "", sector: str = "") -> bool:
    """Check if a job title, description, or sector matches IT/technology keywords."""
    searchable = f"{title} {description} {sector}".lower()
    # First check for exact multi-word matches
    for keyword in IT_KEYWORDS:
        if " " in keyword and keyword in searchable:
            return True
    # Then check for single-word matches that are less likely to be false positives
    single_words = {"software", "developer", "devops", "cyber", "automation",
                    "frontend", "backend", "programmer", "saas", "sql", "azure", "aws",
                    "linux", "embedded", "firmware", "digital", "javascript"}
    title_lower = title.lower()
    if any(kw in title_lower for kw in single_words):
        return True
    # For "engineer" and "architect", require tech context
    tech_context = {"engineer", "architect", "data", "cloud", "security", "network", "system",
                    "platform", "infrastructure", "mobile", "web", "test", "qa"}
    if "engineer" in title_lower and any(tc in title_lower for tc in tech_context):
        return True
    if "architect" in title_lower and any(tc in title_lower for tc in tech_context):
        return True
    # For "analyst", require specific tech prefix
    tech_analyst = {"data analyst", "security analyst", "qa analyst", "test analyst",
                    "business analyst", "systems analyst", "bi analyst", "cyber analyst"}
    if any(ta in searchable for ta in tech_analyst):
        return True
    # Check description for strong IT signals
    strong_signals = {"software development", "cloud platform", "aws", "azure", "devops",
                      "cyber security", "data engineering", "machine learning"}
    if any(sig in searchable for sig in strong_signals):
        return True
    return False


@dataclass(frozen=True)
class RawJobInput:
    source: str
    sourceJobId: str | None
    title: str
    company: str | None
    location: str | None
    salaryText: str | None
    descriptionText: str | None
    url: str
    postedDate: str | None
    closingDate: str | None


@dataclass(frozen=True)
class RawJobPageInput:
    source: str
    url: str
    rawHtml: str | None
    markdown: str | None
    rawJson: str | None
    crawledAt: str


class LinkParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.links: list[tuple[str, str]] = []
        self._href_stack: list[str | None] = []
        self._text_parts: list[str] = []
        self._rel_stack: list[str] = []
        self.next_links: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag != "a":
            return

        href = dict(attrs).get("href")
        self._href_stack.append(href)
        self._rel_stack.append(dict(attrs).get("rel") or "")
        self._text_parts = []

    def handle_data(self, data: str) -> None:
        if self._href_stack:
            self._text_parts.append(data)

    def handle_endtag(self, tag: str) -> None:
        if tag != "a" or not self._href_stack:
            return

        href = self._href_stack.pop()
        rel = self._rel_stack.pop()
        text = clean_text(" ".join(self._text_parts))
        if href and text:
            self.links.append((text, href))
        if href and ("next" in rel.lower().split() or text.lower() in {"next", "next page", ">", "›", "»"}):
            self.next_links.append(href)
        self._text_parts = []


def clean_text(value: str | None) -> str:
    if not value:
        return ""

    value = re.sub(r"<script\b[^<]*(?:(?!</script>)<[^<]*)*</script>", " ", value, flags=re.I)
    value = re.sub(r"<style\b[^<]*(?:(?!</style>)<[^<]*)*</style>", " ", value, flags=re.I)
    value = re.sub(r"<[^>]+>", " ", value)
    value = unescape(value)
    return re.sub(r"\s+", " ", value).strip(" -*#\t\r\n")


def extract_links(html: str, base_url: str) -> list[tuple[str, str]]:
    parser = LinkParser()
    parser.feed(html)
    return [(text, urljoin(base_url, href)) for text, href in parser.links]


def extract_next_links(html: str, base_url: str) -> list[str]:
    parser = LinkParser()
    parser.feed(html)
    return [urljoin(base_url, href) for href in parser.next_links]


def looks_like_job_title(title: str) -> bool:
    lowered = title.lower()
    return any(keyword in lowered for keyword in JOB_KEYWORDS) and not any(
        word in lowered for word in SKIP_LINK_WORDS
    )


def is_job_link(parser: str, title: str, url: str) -> bool:
    path = urlparse(url).path
    if parser == "govt-nz":
        return "/jobs/" in path or "jncustomsearch.viewFullSingle" in url
    if parser == "absolute-it":
        return path.startswith("/it-job/")
    if parser == "talent-army":
        return path.startswith("/job-ads/") and looks_like_job_title(title)
    if parser == "beyond-recruitment":
        return path.startswith("/job/") and not any(
            part in path for part in ("/save_job", "/apply")
        )
    if parser == "consult-recruitment":
        return path.startswith("/job/") and "/jobs/" not in path and not any(
            p in path for p in ("/register", "/apply", "/save", "/login")
        )
    if parser == "comspek":
        return path.startswith("/job/") and not any(
            part in path for part in ("/save_job", "/apply")
        )
    if parser == "salt":
        return path.startswith("/jobs/") and path.count("/") >= 2
    if parser == "digital-garage":
        return path.startswith("/jobs/") and re.search(r"/\d+-", path) is not None
    if parser == "jobseek":
        return path.startswith("/jobs/") and "/Jobs" not in url
    if parser == "airnz":
        return path.startswith("/job/") and "jid-" in url
    return looks_like_job_title(title)


def extract_label(text: str, label: str) -> str | None:
    match = re.search(rf"(?:\*\*)?{label}(?:\*\*)?\s*:\s*(.+?)(?:\s+(?:Category|Type|Apply|Sector|Posted|Work|Job|Employ|Salary|Location|Discipline|Reference):|$)", text, re.I)
    return clean_text(match.group(1)) if match else None


def extract_salary(text: str) -> str | None:
    labelled = extract_label(text, "Salary")
    if labelled:
        return labelled

    match = re.search(
        r"(\$?\s*\d{2,3}(?:,\d{3}|k)?\s*(?:-|to|–)\s*\$?\s*\d{2,3}(?:,\d{3}|k)?)",
        text,
        re.I,
    )
    return clean_text(match.group(1)) if match else None


def extract_title(html: str, text: str) -> str:
    h1 = re.search(r"<h1[^>]*>(.*?)</h1>", html, re.I | re.S)
    if h1:
        title = clean_text(h1.group(1))
        if title:
            return title

    title = re.search(r"<title[^>]*>(.*?)</title>", html, re.I | re.S)
    if title:
        value = clean_text(title.group(1))
        if value:
            return value

    return text[:80] if text else "Untitled job"


def parse_jobs(source: str, url: str, html: str, text: str) -> list[RawJobInput]:
    salary = extract_salary(text)
    location = extract_label(text, "Location") or extract_label(text, "Region")
    company = extract_label(text, "Company") or extract_label(text, "Employer")
    jobs: list[RawJobInput] = []
    seen_urls: set[str] = set()

    for title, link_url in extract_links(html, url):
        if not looks_like_job_title(title) or link_url in seen_urls:
            continue

        seen_urls.add(link_url)
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=None,
                title=title,
                company=company,
                location=location,
                salaryText=salary,
                descriptionText=text[:1200] or None,
                url=link_url,
                postedDate=None,
                closingDate=None,
            )
        )

    if jobs:
        return jobs

    return [
        RawJobInput(
            source=source,
            sourceJobId=None,
            title=extract_title(html, text),
            company=company,
            location=location,
            salaryText=salary,
            descriptionText=text[:1200] or None,
            url=url,
            postedDate=None,
            closingDate=None,
        )
    ]


def obeys_robots(url: str, user_agent: str) -> bool:
    match = re.match(r"^(https?://[^/]+)", url)
    if not match:
        return False

    origin = match.group(1).lower()
    parser = ROBOTS_CACHE.get(origin)
    if parser is None:
        parser = RobotFileParser()
        parser.set_url(urljoin(origin, "/robots.txt"))
        parser.read()
        ROBOTS_CACHE[origin] = parser
    return parser.can_fetch(user_agent, url)


def fetch_with_scrapling(args: argparse.Namespace, url: str) -> tuple[str, str]:
    try:
        if args.fetcher_type == "dynamic":
            from scrapling.fetchers import DynamicFetcher as Fetcher
        elif args.fetcher_type == "stealth":
            from scrapling.fetchers import StealthyFetcher as Fetcher
        else:
            from scrapling.fetchers import Fetcher
    except Exception as exc:  # pragma: no cover - depends on local Python env
        raise RuntimeError(
            "Scrapling is not installed. Run `python -m pip install -e crawler` or install crawler dependencies."
        ) from exc

    fetch_kwargs: dict[str, Any] = {"url": url}
    if args.proxy_url:
        fetch_kwargs["proxy"] = args.proxy_url

    is_dynamic = args.fetcher_type in ("dynamic", "stealth")
    if is_dynamic:
        fetch_kwargs.setdefault("headless", True)
        page = Fetcher.fetch(**fetch_kwargs)
    else:
        page = Fetcher.get(**fetch_kwargs)
    html = getattr(page, "html", None) or getattr(page, "body", None) or str(page)
    if isinstance(html, bytes):
        html = html.decode("utf-8", errors="replace")

    text = getattr(page, "text", None)
    if not isinstance(text, str) or not text.strip():
        text = clean_text(html)

    return html, text


def raw_page(args: argparse.Namespace, url: str, html: str, text: str) -> RawJobPageInput:
    if args.parser == "halter-ashby":
        return RawJobPageInput(
            source=args.source,
            url=url,
            rawHtml=None,
            markdown=None,
            rawJson=html,
            crawledAt=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        )
    return RawJobPageInput(
        source=args.source,
        url=url,
        rawHtml=html,
        markdown=text,
        rawJson=json.dumps(
            {
                "fetcherType": args.fetcher_type,
                "robotsTxtObey": args.robots_txt_obey,
                "proxyConfigured": bool(args.proxy_url),
            }
        ),
        crawledAt=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    )


def structured_location(source_data: dict[str, Any]) -> str | None:
    job_location = source_data.get("jobLocation") or {}
    if isinstance(job_location, list):
        job_location = job_location[0] if job_location else {}
    address = job_location.get("address") or {} if isinstance(job_location, dict) else {}
    if isinstance(address, str):
        return clean_text(address) or None
    parts = [address.get(key) for key in ("addressLocality", "addressRegion", "addressCountry")]
    unique_parts = list(dict.fromkeys(clean_text(str(part)) for part in parts if part))
    return ", ".join(unique_parts) or None


def parse_job_detail(
    source: str,
    url: str,
    html: str,
    text: str,
    fallback_title: str,
    parser: str = "generic",
) -> RawJobInput:
    if source_data := parse_structured_job(html):
        location = structured_location(source_data)
        company = source_data.get("hiringOrganization", {}).get("name")
        source_job_id = source_data.get("identifier", {}).get("value")
        employment_type = str(source_data.get("employmentType") or "").replace("_", "-").title()
        description = clean_text(str(source_data.get("description") or text))
        if employment_type:
            description = f"{employment_type}. {description}"
        return RawJobInput(
            source=source,
            sourceJobId=str(source_job_id) if source_job_id else None,
            title=clean_text(str(source_data.get("title") or fallback_title)),
            company=clean_text(str(company)) or None,
            location=clean_text(str(location)) or None,
            salaryText=extract_salary(text),
            descriptionText=description[:12000] or None,
            url=url,
            postedDate=iso_date(source_data.get("datePosted")),
            closingDate=iso_date(source_data.get("validThrough")),
        )

    if parser == "absolute-it":
        return parse_absolute_it_detail(source, url, html, text, fallback_title)
    if parser == "talent-army":
        return parse_talent_army_detail(source, url, html, text, fallback_title)
    if parser == "beyond-recruitment":
        return parse_beyond_recruitment_detail(source, url, html, text, fallback_title)
    if parser == "comspek":
        return parse_comspek_detail(source, url, html, text, fallback_title)
    if parser == "consult-recruitment":
        return parse_consult_recruitment_detail(source, url, html, text, fallback_title)
    if parser == "salt":
        return parse_salt_detail(source, url, html, text, fallback_title)
    if parser == "digital-garage":
        return parse_digital_garage_detail(source, url, html, text, fallback_title)
    if parser == "jobseek":
        return parse_jobseek_detail(source, url, html, text, fallback_title)
    if parser == "airnz":
        return parse_airnz_detail(source, url, html, text, fallback_title)
    if parser == "linkedin":
        return parse_linkedin_job(source, url, html, text, fallback_title)

    parsed = parse_jobs(source, url, html, text)
    detail = parsed[0]
    title = extract_title(html, text)
    if title == "Untitled job" or len(title) > 160:
        title = fallback_title

    return RawJobInput(
        source=source,
        sourceJobId=detail.sourceJobId,
        title=title,
        company=detail.company,
        location=detail.location,
        salaryText=detail.salaryText,
        descriptionText=text[:12000] or None,
        url=url,
        postedDate=detail.postedDate,
        closingDate=detail.closingDate,
    )


def html_group(html: str, pattern: str) -> str | None:
    match = re.search(pattern, html, re.I | re.S)
    return clean_text(match.group(1)) if match else None


def human_date(value: str | None, formats: tuple[str, ...]) -> str | None:
    if not value:
        return None
    for date_format in formats:
        try:
            parsed = datetime.strptime(value, date_format)
            return f"{parsed:%Y-%m-%d}T00:00:00Z"
        except ValueError:
            continue
    return None


def parse_absolute_it_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    title = html_group(html, r'<h1[^>]*class="[^"]*heading[^\"]*"[^>]*>(.*?)</h1>') or fallback_title
    location = html_group(html, r'jobs-single__location[^>]*>.*?<a[^>]*>(.*?)</a>')
    salary = html_group(html, r'jobs-single__salary[^>]*>(.*?)</div>')
    posted = html_group(html, r'jobs-single__date[^>]*>(.*?)</div>')
    description = html_group(html, r'jobs-single__content[^>]*>(.*?)(?:<div class="author|</section>)')
    source_id = re.search(r"/it-job/(bh-\d+)", url, re.I)
    return RawJobInput(
        source=source,
        sourceJobId=source_id.group(1).upper() if source_id else None,
        title=title,
        company=None,
        location=location,
        salaryText=salary or extract_salary(text),
        descriptionText=(description or text)[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%d %b %Y",)),
        closingDate=None,
    )


def parse_talent_army_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    title = html_group(html, r'<h2[^>]*>(.*?)</h2>') or fallback_title
    pills = [clean_text(value) for value in re.findall(r'category-pill.*?<p[^>]*>(.*?)</p>', html, re.I | re.S)]
    locations = {"Auckland", "Wellington", "Christchurch", "All New Zealand", "Remote", "Sydney"}
    location = next((value for value in pills if value in locations), None)
    employment = next((value for value in pills if value in {"Permanent", "Contract", "Part-time"}), None)
    posted = html_group(html, r'is-posted[^>]*>\s*Posted\s*</p>\s*<p[^>]*>(.*?)</p>')
    source_id = re.search(r"/job-ads/(\d+)", url)
    description = f"{employment}. {text}" if employment else text
    return RawJobInput(
        source=source,
        sourceJobId=source_id.group(1) if source_id else None,
        title=title,
        company=None,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=description[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%B %d, %Y",)),
        closingDate=None,
    )


def parse_beyond_recruitment_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    title = html_group(html, r'<h1[^>]*class="[^"]*job-title[^"]*"[^>]*>(.*?)</h1>') or fallback_title
    location = html_group(html, r'class="[^"]*job-location[^"]*"[^>]*>.*?<span[^>]*>Location</span>\s*(.*?)\s*</dd>')
    if not location:
        location = html_group(html, r'class="[^"]*job-location[^"]*"[^>]*>\s*(.*?)\s*</dd>')
    disc = html_group(html, r'class="[^"]*discipline[^"]*"[^>]*>\s*<span[^>]*>Discipline</span>\s*(.*?)\s*</dd>')
    job_type = html_group(html, r'class="[^"]*job-type[^"]*"[^>]*>\s*<span[^>]*>Job type</span>\s*(.*?)\s*</dd>')
    ref = html_group(html, r'class="[^"]*job-ref[^"]*"[^>]*>.*?<span[^>]*>Reference</span>\s*(.*?)\s*</dd>')
    posted = html_group(html, r'class="[^"]*date-posted[^"]*"[^>]*>\s*<span[^>]*>Posted</span>\s*(.*?)\s*</dd>')
    description = html_group(html, r'class="[^"]*desc[^"]*"[^>]*>(.*?)(?:</div>\s*<(?:section|div))')
    if not description:
        description = html_group(html, r'class="[^"]*desc[^"]*"[^>]*>(.*?)</div>')
    source_id = re.search(r"/job/[\w-]+-(\d+)", url)
    company = "Beyond Recruitment"
    return RawJobInput(
        source=source,
        sourceJobId=str(ref).strip() if ref else (source_id.group(1) if source_id else None),
        title=title,
        company=company,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=(description or text)[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%d %B %Y", "%B %d, %Y")),
        closingDate=None,
    )


def parse_beyond_recruitment_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    # Extract job cards from Volcanic results-list
    for match in re.finditer(
        r'class="job-title"[^>]*>\s*<a\s+href="(/job/([^"]+))"[^>]*>([^<]*)</a>',
        html,
        re.I,
    ):
        job_url = urljoin(url, match.group(1))
        slug = match.group(2)
        title = clean_text(match.group(3))
        if job_url in seen:
            continue
        seen.add(job_url)
        # Find the surrounding card to get location — search forward only to next card or 1000 chars
        ctx = html[match.start() : match.end() + 1500]
        # Cut at next job-title to avoid grabbing next card's location
        next_title = ctx.find('class="job-title"', 100)
        if next_title > 0:
            ctx = ctx[:next_title]
        loc_match = re.search(r'class="results-job-location"[^>]*>([^<]*)<', ctx, re.I)
        location = clean_text(loc_match.group(1)) if loc_match else None
        desc_match = re.search(r'class="job-description"[^>]*>(.*?)</p>', ctx, re.I | re.S)
        desc = clean_text(desc_match.group(1)) if desc_match else None
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=None,
                title=title,
                company=None,
                location=location,
                salaryText=None,
                descriptionText=desc[:1200] if desc else None,
                url=job_url,
                postedDate=None,
                closingDate=None,
            )
        )
    return jobs


def parse_comspek_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse Comspek International job detail pages (Volcanic ATS)."""
    title = html_group(html, r'<h1[^>]*class="[^"]*job-title[^"]*"[^>]*>(.*?)</h1>') or fallback_title
    location = html_group(html, r'class="[^"]*job-location[^"]*"[^>]*>.*?<span[^>]*>Location</span>\s*(.*?)\s*</dd>')
    if not location:
        location = html_group(html, r'class="[^"]*job-location[^"]*"[^>]*>\s*(.*?)\s*</dd>')
    job_type = html_group(html, r'class="[^"]*job-type[^"]*"[^>]*>\s*<span[^>]*>Job type</span>\s*(.*?)\s*</dd>')
    ref = html_group(html, r'class="[^"]*job-ref[^"]*"[^>]*>.*?<span[^>]*>Reference</span>\s*(.*?)\s*</dd>')
    posted = html_group(html, r'class="[^"]*date-posted[^"]*"[^>]*>\s*<span[^>]*>Posted</span>\s*(.*?)\s*</dd>')
    description = html_group(html, r'class="[^"]*desc[^"]*"[^>]*>(.*?)(?:</div>\s*<(?:section|div))')
    if not description:
        description = html_group(html, r'class="[^"]*desc[^"]*"[^>]*>(.*?)</div>')
    source_id = re.search(r"/job/[\w-]+-(\d+)", url)
    return RawJobInput(
        source=source,
        sourceJobId=str(ref).strip() if ref else (source_id.group(1) if source_id else None),
        title=title,
        company=None,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=(description or text)[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%d %B %Y", "%B %d, %Y")),
        closingDate=None,
    )


def parse_comspek_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Parse Comspek International listing pages (Volcanic ATS)."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'class="job-title"[^>]*>\s*<a\s+href="(/job/([^"]+))"[^>]*>([^<]*)</a>',
        html,
        re.I,
    ):
        job_url = urljoin(url, match.group(1))
        slug = match.group(2)
        title = clean_text(match.group(3))
        if job_url in seen:
            continue
        seen.add(job_url)
        ctx = html[match.start() : match.end() + 1500]
        next_title = ctx.find('class="job-title"', 100)
        if next_title > 0:
            ctx = ctx[:next_title]
        loc_match = re.search(r'class="results-job-location"[^>]*>([^<]*)<', ctx, re.I)
        location = clean_text(loc_match.group(1)) if loc_match else None
        desc_match = re.search(r'class="job-description"[^>]*>(.*?)</p>', ctx, re.I | re.S)
        desc = clean_text(desc_match.group(1)) if desc_match else None
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=None,
                title=title,
                company=None,
                location=location,
                salaryText=None,
                descriptionText=desc[:1200] if desc else None,
                url=job_url,
                postedDate=None,
                closingDate=None,
            )
        )
    return jobs


def parse_salt_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse Salt NZ job detail pages (WordPress + Schema.org JobPosting)."""
    source_data = parse_structured_job(html)
    if source_data:
        location = structured_location(source_data)
        company = (source_data.get("hiringOrganization") or {}).get("name")
        source_job_id = (source_data.get("identifier") or {}).get("value")
        employment_type = str(source_data.get("employmentType") or "").replace("_", "-").title()
        description = clean_text(str(source_data.get("description") or text))
        if employment_type:
            description = f"{employment_type}. {description}"
        # Extract salary from Schema.org baseSalary (more reliable than text scan)
        base_salary = source_data.get("baseSalary", {})
        salary = extract_salary(text)  # fallback
        if isinstance(base_salary, dict):
            sv = base_salary.get("value", {})
            if isinstance(sv, dict):
                val = str(sv.get("value", ""))
            else:
                val = str(sv) if sv else ""
            if val and val != "None":
                if "-" in val:
                    parts = val.split("-")
                    salary = f"${parts[0].strip()}k - ${parts[1].strip()}k"
                else:
                    salary = f"${val}k"
        return RawJobInput(
            source=source,
            sourceJobId=str(source_job_id) if source_job_id else None,
            title=clean_text(str(source_data.get("title") or fallback_title)),
            company=clean_text(str(company)) or None,
            location=clean_text(str(location)) or None,
            salaryText=salary,
            descriptionText=description[:12000] or None,
            url=url,
            postedDate=iso_date(source_data.get("datePosted")),
            closingDate=iso_date(source_data.get("validThrough")),
        )
    # Fallback: parse WordPress job detail page
    title = html_group(html, r'<h1[^>]*class="[^"]*job[^"]*title[^"]*"[^>]*>(.*?)</h1>')
    if not title:
        title = html_group(html, r'<h1[^>]*>(.*?)</h1>') or fallback_title
    location = extract_label(text, "Location")
    salary = extract_salary(text)
    job_type = extract_label(text, "Job type") or extract_label(text, "Work type")
    desc_match = re.search(r'class="[^"]*description[^"]*"[^>]*>(.*?)(?:</div>\s*<(?:div|section))', html, re.DOTALL)
    description = clean_text(desc_match.group(1)) if desc_match else text[:12000]
    company = None  # WordPress detail fallback doesn't extract company
    return RawJobInput(
        source=source,
        sourceJobId=None,
        title=title,
        company=company,
        location=location,
        salaryText=salary,
        descriptionText=description or None,
        url=url,
        postedDate=None,
        closingDate=None,
    )


def parse_salt_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Parse Salt NZ listing pages (WordPress .job-item cards)."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'class="job-item__title"[^>]*>\s*<a\s+href="(https?://[^"]+/jobs/([^"]+))"[^>]*>([^<]*)</a>',
        html, re.I,
    ):
        job_url = match.group(1)
        slug = match.group(2)
        title = clean_text(match.group(3))
        if job_url in seen:
            continue
        seen.add(job_url)
        ctx_start = max(0, match.start() - 1500)
        ctx = html[ctx_start : match.end() + 500]
        loc_match = re.search(r'highlights__item--location[^<]*</i>\s*([^<]+)<', ctx, re.I)
        location = clean_text(loc_match.group(1)) if loc_match else None
        type_match = re.search(r'highlights__item--type[^<]*</i>\s*([^<]+)<', ctx, re.I)
        job_type = clean_text(type_match.group(1)) if type_match else None
        desc_match = re.search(r'job-item__excerpt[^>]*>([^<]+)<', ctx, re.I)
        desc = clean_text(desc_match.group(1)) if desc_match else None
        jobs.append(RawJobInput(
            source=source,
            sourceJobId=None,
            title=title,
            company=None,
            location=location,
            salaryText=None,
            descriptionText=desc[:1200] if desc else None,
            url=job_url,
            postedDate=None,
            closingDate=None,
        ))
    return jobs


def parse_digital_garage_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse Digital Garage job detail pages (JobAdder integration)."""
    title = html_group(html, r'<h1[^>]*>(.*?)</h1>') or fallback_title
    # Extract job ID from URL: /jobs/{id}-{slug}
    source_id = re.search(r"/jobs/(\d+)-", url)
    job_id = source_id.group(1) if source_id else None
    # Extract JobAdder reference number (7 digits)    
    ref_match = re.search(r'(\d{7,8})\s+\1', text)
    ref = ref_match.group(1) if ref_match else None
    # Extract date: DD.MM.YYYY  
    date_match = re.search(r'(\d{2}\.\d{2}\.\d{4})', text)
    posted = date_match.group(1) if date_match else None
    # Extract type: try more specific patterns first
    type_match = re.search(r'(Permanent\s*/\s*Full\s*Time|Permanent\s*/\s*Part\s*Time|Full\s*Time)', text, re.I)
    if not type_match:
        type_match = re.search(r'(Contract|Temporary|Fixed\s*Term)', text, re.I)
    job_type = type_match.group(1) if type_match else None
    # Description: use full text stripped of header nav, or just text past the type
    desc = text
    # Try to find the job title repeated after "//" pattern in the body
    marker = re.search(r'//\s*.+?\s+(?:Permanent|Contract|Full|Part)', text)
    if marker:
        desc = text[marker.start():].strip()
        apply_pos = desc.find("Apply now")
        if apply_pos > 0:
            desc = desc[apply_pos + 9:].strip()  # Skip "Apply now"
    elif job_type:
        desc_start = text.rfind(job_type)  # Use last occurrence of type
        apply_pos = text.find("Apply now", desc_start)
        if apply_pos > desc_start:
            desc = text[desc_start + len(job_type):apply_pos].strip()
    return RawJobInput(
        source=source,
        sourceJobId=job_id,
        title=title,
        company=None,
        location=None,
        salaryText=extract_salary(text),
        descriptionText=(job_type + ". " + desc if job_type else desc)[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%d.%m.%Y",)),
        closingDate=None,
    )


def parse_digital_garage_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Parse Digital Garage listing page (JobAdder iframe links)."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'href="(https://digitalgarage\.co\.nz/jobs/(\d+)-([^"]+))"',
        html, re.I,
    ):
        job_url = match.group(1)
        job_id = match.group(2)
        slug = match.group(3)
        if job_url in seen:
            continue
        seen.add(job_url)
        title = slug.replace("-", " ").title()
        jobs.append(RawJobInput(
            source=source,
            sourceJobId=job_id,
            title=title,
            company=None,
            location=None,
            salaryText=None,
            descriptionText=None,
            url=job_url,
            postedDate=None,
            closingDate=None,
        ))
    return jobs


def parse_jobseek_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse JobSeek NZ job detail pages (Bootstrap + Schema.org)."""
    # Try Schema.org first
    source_data = parse_structured_job(html)
    if source_data:
        location = structured_location(source_data)
        company = (source_data.get("hiringOrganization") or {}).get("name")
        source_job_id = (source_data.get("identifier") or {}).get("value")
        description = clean_text(str(source_data.get("description") or text))
        return RawJobInput(
            source=source,
            sourceJobId=str(source_job_id) if source_job_id else None,
            title=clean_text(str(source_data.get("title") or fallback_title)),
            company=clean_text(str(company)) or None,
            location=clean_text(str(location)) or None,
            salaryText=extract_salary(text),
            descriptionText=description[:12000] or None,
            url=url,
            postedDate=iso_date(source_data.get("datePosted")),
            closingDate=iso_date(source_data.get("validThrough")),
        )
    # Fallback: parse HTML list items
    title = html_group(html, r'<h1[^>]*>(.*?)</h1>') or fallback_title
    location = html_group(html, r'<strong>Location:</strong>\s*([^<]+)<',)
    category = html_group(html, r'<strong>Category:</strong>\s*([^<]+)<',)
    employment = html_group(html, r'<strong>Employment:</strong>\s*([^<]+)<',)
    closing = html_group(html, r'<strong>Closing:</strong>\s*([^<]+)<',)
    ref = html_group(html, r'<strong>Reference:</strong>\s*([^<]+)<',)
    desc_match = re.search(r'class="[^"]*description[^"]*"[^>]*>(.*?)(?:</div>\s*<(?:div|section))', html, re.DOTALL)
    description = clean_text(desc_match.group(1)) if desc_match else text[:12000]
    return RawJobInput(
        source=source,
        sourceJobId=ref.strip() if ref else None,
        title=title,
        company=None,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=description or None,
        url=url,
        postedDate=None,
        closingDate=human_date(closing, ("%d %b %Y",)),
    )


def parse_jobseek_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Parse JobSeek NZ listing pages (Bootstrap cards)."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'href="(/jobs/([^"]+))"[^>]*>.*?<h2[^>]*class="jp-card-title"[^>]*>([^<]+)<',
        html, re.I | re.S,
    ):  # jobseek listing
        job_url = urljoin(url, match.group(1))
        slug = match.group(2)
        title = clean_text(match.group(3))
        if job_url in seen or '/Jobs' in match.group(1):
            continue
        seen.add(job_url)
        jobs.append(RawJobInput(
            source=source,
            sourceJobId=None,
            title=title,
            company=None,
            location=None,
            salaryText=None,
            descriptionText=None,
            url=job_url,
            postedDate=None,
            closingDate=None,
        ))
    return jobs


def parse_linkedin_job(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse a LinkedIn job from JSON data (passed as html or text)."""
    import json as _json
    data = {}
    try:
        if html and html.strip().startswith("{"):
            data = _json.loads(html)
        elif text and text.strip().startswith("{"):
            data = _json.loads(text)
    except _json.JSONDecodeError:
        pass
    title = data.get("title") or fallback_title
    return RawJobInput(
        source=source,
        sourceJobId=data.get("sourceJobId"),
        title=title,
        company=data.get("company"),
        location=data.get("location"),
        salaryText=data.get("salaryText"),
        descriptionText=(data.get("descriptionText") or "")[:12000] or None,
        url=data.get("url") or url,
        postedDate=data.get("postedDate"),
        closingDate=data.get("closingDate"),
    )


def run_linkedin_crawl(source: str, output_path: str, max_jobs: int = 80) -> list[RawJobInput]:
    """Run the LinkedIn crawler script and parse its JSON output."""
    import subprocess as _sp, json as _json, os as _os
    # Find the venv Python
    crawler_dir = _os.path.dirname(_os.path.dirname(_os.path.abspath(__file__)))
    venv_python = _os.path.join(crawler_dir, ".venv", "bin", "python")
    if not _os.path.exists(venv_python):
        venv_python = "python3"  # fallback
    script = _os.path.join(crawler_dir, "adapters", "linkedin", "linkedin_crawler.py")
    if not _os.path.exists(script):
        raise RuntimeError(f"LinkedIn crawler script not found: {script}")
    result = _sp.run(
        [venv_python, script, "--location", "New Zealand", "--max-jobs", str(max_jobs), "--delay", "0.3", "--output", output_path],
        capture_output=True, text=True, timeout=900
    )
    if result.returncode != 0:
        raise RuntimeError(f"LinkedIn crawler failed: {result.stderr[:500]}")
    with open(output_path) as f:
        data = _json.load(f)
    jobs_data = data.get("jobs", [])
    return [
        RawJobInput(
            source=source,
            sourceJobId=j.get("sourceJobId"),
            title=j.get("title", "Unknown"),
            company=j.get("company"),
            location=j.get("location"),
            salaryText=j.get("salaryText"),
            descriptionText=(j.get("descriptionText") or "")[:12000] or None,
            url=j.get("url", ""),
            postedDate=j.get("postedDate"),
            closingDate=j.get("closingDate"),
        )
        for j in jobs_data if j.get("title") != "Unknown" and j.get("company")
    ]


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


def parse_consult_recruitment_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    # Guard: if this is a listing page (has many "See job" links), reject
    see_job_count = len(re.findall(r'>See job</a>', html, re.I))
    if see_job_count >= 10:
        raise ValueError("Page appears to be a job listing, not a detail page.")
    title = html_group(html, r'<h1[^>]*>.*?<strong>([^<]+)</strong>') or fallback_title
    if not title:
        h1_text = html_group(html, r'<h1[^>]*>(.*?)</h1>')
        if h1_text:
            # Try to extract the last meaningful text from H1 (after <br> or <span>)
            parts = re.split(r'<(?:br|/span)[^>]*>', h1_text, flags=re.I)
            title = clean_text(parts[-1]) if parts else h1_text
    if not title:
        title = fallback_title
    # Try HTML-based extraction first (more reliable)
    loc_strong = re.search(r'Location:\s*<br[^>]*>\s*<strong>([^<]+)</strong>', html, re.I)
    if loc_strong:
        location = loc_strong.group(1).strip()
    else:
        location = extract_label(text, "Location") or extract_label(text, "Location:")
    job_type = html_group(html, r'Type:\s*<br[^>]*>\s*<strong>([^<]+)</strong>')
    if not job_type:
        job_type = extract_label(text, "Type") or extract_label(text, "Type:")
    sector = html_group(html, r'class="[^"]*job-sector[^"]*"[^>]*>([^<]+)<')
    posted = html_group(html, r'class="[^"]*job-date[^"]*"[^>]*>([^<]+)<')
    if not posted:
        posted = html_group(html, r'class="[^"]*date[^"]*"[^>]*>([^<]+)<')
    description = None
    # Find text-editor widget that comes after the H1 (job description)
    h1_pos = html.find("<h1")
    if h1_pos > 0:
        after_h1 = html[h1_pos:]
        desc_block = re.search(
            r'data-widget_type="text-editor\.default"[^>]*>(.*?)(?:</div>\s*</div>\s*</div>)',
            after_h1, re.DOTALL
        )
        if desc_block:
            description = clean_text(desc_block.group(1))
    if not description:
        description = text[:12000] if text else None
    source_id = re.search(r"/job/([^/]+)/?$", url)
    slug = source_id.group(1) if source_id else None
    # Also extract category from WordPress pattern for IT filtering
    category_match = re.search(r'Category:\s*<br[^>]*>\s*<strong>([^<]+)</strong>', html, re.I)
    category = category_match.group(1).replace("&amp;", "&").strip() if category_match else None
    # Prefix description with category and type for downstream IT filtering
    prefix_parts = [p for p in (category, job_type) if p]
    prefix = ". ".join(part for part in prefix_parts if part)
    final_desc = description or text[:12000] or None
    if prefix and final_desc:
        final_desc = f"{prefix}. {final_desc}"
    return RawJobInput(
        source=source,
        sourceJobId=slug,
        title=title,
        company=None,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=final_desc,
        url=url,
        postedDate=human_date(posted, ("%d %B %Y",)),
        closingDate=None,
    )


def parse_consult_recruitment_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'class="ja_job_link__btn"[^>]*href="(https?://[^"]*/job/([^/"]+)/?)"',
        html,
        re.I,
    ):
        job_url = urljoin(url, match.group(1)) if not match.group(1).startswith("http") else match.group(1)
        slug = match.group(2)
        if job_url in seen:  # consult line 583
            continue
        seen.add(job_url)
        ctx_start = max(0, match.start() - 2000)
        ctx = html[ctx_start : match.end() + 500]
        title_match = re.search(r'class="[^"]*elementor-heading-title[^"]*"[^>]*>\s*<a[^>]*>([^<]+)</a>', ctx, re.I)
        title = clean_text(title_match.group(1)) if title_match else slug.replace("-", " ").title()
        sector_match = re.search(r'data-sector="([^"]*)"', ctx, re.I)
        loc_match = re.search(r'data-location="([^"]*)"', ctx, re.I)
        type_match = re.search(r'data-type="([^"]*)"', ctx, re.I)
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=slug,
                title=title,
                company=None,
                location=loc_match.group(1).replace("-", " ").title() if loc_match else None,
                salaryText=None,
                descriptionText=None,
                url=job_url,
                postedDate=None,
                closingDate=None,
            )
        )
    return jobs


def parse_structured_job(html: str) -> dict[str, Any] | None:
    for match in re.finditer(
        r'<script[^>]+type=["\']application/ld\+json["\'][^>]*>(.*?)</script>',
        html,
        re.I | re.S,
    ):
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
            if isinstance(item, dict) and item.get("@type") == "JobPosting":
                return item
    return None


def iso_date(value: Any) -> str | None:
    if not value:
        return None
    match = re.match(r"\d{4}-\d{2}-\d{2}", str(value))
    return f"{match.group(0)}T00:00:00Z" if match else None


def same_site(left: str, right: str) -> bool:
    return urlparse(left).netloc.lower() == urlparse(right).netloc.lower()


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


def crawl(
    args: argparse.Namespace,
    fetch: Any = None,
    sleeper: Any = time.sleep,
) -> dict[str, Any]:
    fetch = fetch or (lambda url: fetch_with_scrapling(args, url))

    if args.parser == "halter-ashby":
        # The documented public API is the access contract; its robots endpoint returns 401.
        html, text = fetch(args.url)
        jobs = parse_halter_ashby(args.source, html)
        return {
            "rawPages": [asdict(raw_page(args, args.url, html, text))],
            "jobs": [asdict(job) for job in jobs],
            "isComplete": True,
        }

    if args.parser == "linkedin":
        # LinkedIn uses a separate MCP-based crawler — run it and return its output
        output_path = args.url or "/tmp/li_dashboard_output.json"
        jobs = run_linkedin_crawl(args.source, output_path)
        return {
            "rawPages": [asdict(RawJobPageInput(
                source=args.source, url="https://www.linkedin.com/jobs/",
                rawHtml=None, markdown=None,
                rawJson=json.dumps({"jobs_count": len(jobs)}),
                crawledAt=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            ))],
            "jobs": [asdict(job) for job in jobs],
            "isComplete": True,
        }

    listing_queue = [args.url]
    visited: set[str] = set()
    candidates: dict[str, str] = {}
    pages: list[RawJobPageInput] = []

    while listing_queue and len(pages) < args.max_pages:
        url = listing_queue.pop(0)
        if url in visited or not same_site(args.url, url):
            continue
        if args.robots_txt_obey and not obeys_robots(url, args.user_agent):
            raise RuntimeError(f"Robots.txt does not allow crawling {url}")
        if pages:
            sleeper(args.delay_seconds)

        html, text = fetch(url)
        visited.add(url)
        pages.append(raw_page(args, url, html, text))

        for title, job_url in extract_links(html, url):
            if same_site(args.url, job_url) and is_job_link(args.parser, title, job_url):
                candidates.setdefault(job_url, title)
        for next_url in extract_next_links(html, url):
            if next_url not in visited and next_url not in listing_queue:
                listing_queue.append(next_url)

        # Supplement candidates from listing-level parsers (sources where detail URLs
        # aren't standard <a> links but are embedded in JS/JSON/data attributes)
        supplementary_candidates = _supplement_candidates(args, url, html)
        for job_url, fallback_title in supplementary_candidates:
            if job_url not in candidates:
                candidates[job_url] = fallback_title

    jobs: list[RawJobInput] = []
    for url, fallback_title in candidates.items():
        if len(pages) >= args.max_pages:
            break
        if args.robots_txt_obey and not obeys_robots(url, args.user_agent):
            continue
        sleeper(args.delay_seconds)
        html, text = fetch(url)
        pages.append(raw_page(args, url, html, text))
        job = parse_job_detail(args.source, url, html, text, fallback_title, args.parser)
        if looks_like_it_job(job.title, job.descriptionText or ""):
            jobs.append(job)

    if not candidates and pages:
        first = pages[0]
        html_chunk = (first.rawHtml or "")[:3000]
        text_chunk = (first.markdown or "")[:2000]
        # Skip if page looks like a listing rather than a job detail
        listing_signals = (
            'class="job-result-item', 'class="results-list"', 'class="pagination"',
            '>See job</a>',
        )
        is_listing = any(sig in html_chunk for sig in listing_signals)
        if not is_listing:
            try:
                job = parse_job_detail(args.source, first.url, html_chunk, text_chunk, "Untitled job", args.parser)
                jobs.append(job)
            except ValueError:
                pass  # Parser rejected the page

    return {
        "rawPages": [asdict(page) for page in pages],
        "jobs": [asdict(job) for job in jobs],
        "isComplete": not listing_queue and len(jobs) == len(candidates),
    }


def _supplement_candidates(
    args: argparse.Namespace, url: str, html: str
) -> list[tuple[str, str]]:
    """Extract job detail URLs from listing pages where standard <a> links aren't used."""
    out: list[tuple[str, str]] = []
    parser = args.parser

    if parser == "beyond-recruitment":
        for job in parse_beyond_recruitment_listing(args.source, url, html, ""):
            if looks_like_it_job(job.title, job.descriptionText or ""):
                out.append((job.url, job.title))
    elif parser == "comspek":
        for job in parse_comspek_listing(args.source, url, html, ""):
            if looks_like_it_job(job.title, job.descriptionText or ""):
                out.append((job.url, job.title))
    elif parser == "consult-recruitment":
        for job in parse_consult_recruitment_listing(args.source, url, html, ""):
            if looks_like_it_job(job.title, job.descriptionText or ""):
                out.append((job.url, job.title))
    elif parser == "salt":
        for job in parse_salt_listing(args.source, url, html, ""):
            if looks_like_it_job(job.title, job.descriptionText or ""):
                out.append((job.url, job.title))
    elif parser == "digital-garage":
        for job in parse_digital_garage_listing(args.source, url, html, ""):
            if looks_like_it_job(job.title, job.descriptionText or ""):
                out.append((job.url, job.title))
    elif parser == "jobseek":
        for job in parse_jobseek_listing(args.source, url, html, ""):
            if looks_like_it_job(job.title, job.descriptionText or ""):
                out.append((job.url, job.title))
    elif parser == "airnz":
        for job in parse_airnz_listing(args.source, url, html, ""):
            if looks_like_it_job(job.title, job.descriptionText or ""):
                out.append((job.url, job.title))

    return out


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Fetch and parse job pages with Scrapling.")
    parser.add_argument("--source", default="Scrapling")
    parser.add_argument("--url", required=True)
    parser.add_argument(
        "--parser",
        choices=("generic", "govt-nz", "absolute-it", "talent-army", "halter-ashby", "beyond-recruitment", "consult-recruitment", "comspek", "salt", "digital-garage", "jobseek", "airnz", "linkedin"),
        default="generic",
    )
    parser.add_argument("--fetcher-type", choices=("fetcher", "dynamic", "stealth"), default="fetcher")
    parser.add_argument("--max-pages", type=int, default=50)
    parser.add_argument("--delay-seconds", type=float, default=5)
    parser.add_argument("--robots-txt-obey", action="store_true")
    parser.add_argument("--proxy-url", default="")
    parser.add_argument("--user-agent", default="NZJobMarketDashboardBot/0.1")
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    try:
        print(json.dumps(crawl(args), ensure_ascii=False))
        return 0
    except Exception as exc:  # pragma: no cover - defensive CLI boundary
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
