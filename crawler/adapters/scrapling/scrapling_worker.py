#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timedelta, timezone
from html import unescape
from html.parser import HTMLParser
from typing import Any
from urllib.parse import urljoin, urlparse

# The worker runs both as a script (invoked by the .NET adapter) and as an
# imported package (unit tests). Adding our own directory keeps both import
# styles working without a relative-import failure.
_THIS_DIR = os.path.dirname(os.path.abspath(__file__))
if _THIS_DIR not in sys.path:
    sys.path.insert(0, _THIS_DIR)

from platform_api import API_PLATFORMS, crawl_platform  # noqa: E402
from robots import RobotsRules, can_fetch, parse_robots  # noqa: E402
from site_registry import lookup_source  # noqa: E402



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

ROBOTS_CACHE: dict[str, RobotsRules] = {}

DEFAULT_HTTP_USER_AGENT = (
    "Mozilla/5.0 (compatible; NZJobMarketDashboardBot/0.2; +https://example.invalid/bot)"
)

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
    # Enterprise platforms and tooling that clearly mark a technology role.
    "workday", "salesforce", "servicenow", "netsuite", "mulesoft", "boomi",
    "snowflake", "databricks", "kafka", "kubernetes", "docker", "terraform",
    "ansible", "jenkins", "gitlab", "github actions", "ci/cd", "cicd",
    "sharepoint", "active directory", "vmware", "citrix", "intune",
    "azure devops", "power platform", "dynamics 365", "microsoft dynamics",
    "pega", "erp", "crm", "etl", "data warehouse", "business intelligence",
    "microservices", "google cloud", "gcp", "sql server", "mongodb", "redis",
    "elasticsearch", "splunk", "okta", "sailpoint", "cyberark", "fortinet",
    "cisco", "juniper", "sap hana", "sap consultant", "salesforce developer",
    "test automation", "quality assurance", "scrum master", "product owner",
})

#: Distinctive single tokens that are a strong technology signal on their own.
IT_SINGLE_WORD_SIGNALS = frozenset({
    "software", "developer", "devops", "cyber", "automation", "frontend",
    "backend", "programmer", "saas", "sql", "azure", "aws", "linux", "embedded",
    "firmware", "digital", "javascript", "workday", "salesforce", "servicenow",
    "netsuite", "snowflake", "databricks", "kafka", "kubernetes", "docker",
    "terraform", "ansible", "jenkins", "gitlab", "sharepoint", "vmware",
    "citrix", "intune", "pega", "mulesoft", "boomi", "splunk", "okta",
    "cyberark", "fortinet", "juniper", "mongodb", "redis", "elasticsearch",
    "mysql", "sas", "tableau", "informatica", "talend", "dbt", "airflow",
    "integration", "migration", "database", "dba", "sre", "api", "cloud", "technology",
    ".net", "c#", "php", "golang", "kotlin", "swift", "ruby", "scala", "rust",
    "react", "angular", "vue", "node", "nodejs", "django", "laravel", "graphql",
})

# Word-boundary matching avoids false hits ("sas" inside "sales", "pega" in "propaganda").
IT_SINGLE_WORD_RE = re.compile(
    r"(?<![\w])(?:" + "|".join(re.escape(word) for word in sorted(IT_SINGLE_WORD_SIGNALS)) + r")(?![\w])"
)


def looks_like_it_job(title: str, description: str = "", sector: str = "") -> bool:
    """Check if a job title, description, or sector matches IT/technology keywords."""
    searchable = f"{title} {description} {sector}".lower()
    # First check for exact multi-word matches
    for keyword in IT_KEYWORDS:
        if " " in keyword and keyword in searchable:
            return True
    # Then check for single-word matches that are less likely to be false positives
    title_lower = title.lower()
    if IT_SINGLE_WORD_RE.search(title_lower):
        return True
    # For "engineer" and "architect", require tech context
    # "engineer"/"architect" alone are not technology context: otherwise every
    # civil or mechanical engineer would qualify. A second context word is required.
    tech_context = {"architect", "data", "cloud", "security", "network", "system",
                    "platform", "infrastructure", "mobile", "web", "test", "qa", "software",
                    "devops", "database", "integration"}
    if re.search(r"\bengineers?\b", title_lower) and any(tc in title_lower for tc in tech_context):
        return True
    if re.search(r"\barchitects?\b", title_lower) and any(tc in title_lower for tc in tech_context):
        return True
    # For "analyst", require specific tech prefix
    tech_analyst = {"data analyst", "security analyst", "qa analyst", "test analyst",
                    "business analyst", "systems analyst", "bi analyst", "cyber analyst",
                    "business analysis", "systems analysis", "data analysis"}
    if any(ta in searchable for ta in tech_analyst):
        return True
    # Check description for strong IT signals
    strong_signals = {"software development", "cloud platform", "aws", "azure", "devops",
                      "cyber security", "data engineering", "machine learning"}
    if any(sig in searchable for sig in strong_signals):
        return True
    return False


HUB_TITLE_RE = re.compile(
    r"""(?ix)
    ^\s*(?:home|careers?|jobs?|job\s+search|search\s+results?|working\s+here|people\s+and\s+careers
        |register(?:\s+as)?|login|sign\s+in|find\s+a\s+job|current\s+vacancies|job\s+vacancies
        |job\s+listings?|vacancies)\b
    | \bcareers?\s+at\b
    | \bjob\s+search\b
    | \bjobs?\s+in\s+(?:new\s+zealand|nz)\b
    | \brecruitment\s+specialists?\b
    | \blaunch\s+your\b
    | \bjob\s?seek(?:er)?s?\b
    | \bregister\s+as\b
    | \bprogram\s+your\s+path\b
    | \b\d+\s+jobs?\s+found\b
    | \bjob\s+alerts?\b
    | \bjob\s+board\b
    | \bcurrent\s+opportunities\b
    | \bjob\s+opportunit\w*
    | \bgraduate\s+jobs?\b
    | \bopen\s+now\b
    | \bcareers?\s+home\b
    | \bjoin\s+(?:our|the)\s+team\b
    | \bcareers\b
    | \bopportunit\w*\s*$
    """
)


def is_plausible_job_title(title: str | None, source: str | None = None) -> bool:
    """Reject navigation/hub-page titles that look like jobs to a keyword filter.

    Generic extraction sometimes lands on a careers landing page and treats its
    title ("Careers - BNZ", "3 jobs found", "Fonterra") as a job.  Those records
    pollute the market analytics, so they are filtered out here.
    """
    value = (title or "").strip()
    if len(value) < 4:
        return False
    if HUB_TITLE_RE.search(value):
        return False
    if source and value.lower() == source.strip().lower():
        return False
    return True


def is_acceptable_job(job: RawJobInput) -> bool:
    return is_plausible_job_title(job.title, job.source) and looks_like_it_job(
        job.title, job.descriptionText or ""
    )


# --- Shared content-quality gate (mirrors Application.Jobs.JobContentValidator) ---

ERROR_PAGE_RE = re.compile(
    r"""(?ix)
    \b404\b
    | \berror\s*\(\s*404\s*\)
    | \bpage\s+not\s+found\b
    | \bpage\s+cannot\s+be\s+found\b
    | \bpage\s+not\s+available\b
    | \bpage\s+unavailable\b
    | \bpage\s+does\s+not\s+exist\b
    | \bnot\s+found\b
    | \bsomething\s+went\s+wrong\b
    | \binternal\s+server\s+error\b
    | \bthe\s+page\s+you\s+are\s+looking\s+for\b
    """
)

ARTICLE_PATH_RE = re.compile(r"/(?:advice|article|articles|blog|blogs|guides?)(?:/|$)", re.I)

ARTICLE_TITLE_RE = re.compile(
    r"""(?ix)
    ^\s*(?:how\s+to\s+|why\s+\w|what\s+\w|when\s+\w
          |top\s+\d+\b
          |\d+\s+(?:good|best|great|amazing|reasons?|tips?|ways?|things?)\b)
    """
)

JOB_SIGNAL_RE = re.compile(
    r"\b(?:full[-\s]?time|part[-\s]?time|permanent|fixed[-\s]?term|contract|casual|internship|apply\s+now|salary|per\s+annum)\b",
    re.I,
)

HUB_STRONG_RE = re.compile(
    r"""(?ix)
    \bpeople\s+and\s+careers\b
    | \brecruitment\s+and\s+executive\s+search\b
    | \bempowering\s+new\s+zealand\s+to\s+hire\b
    | \blaunch\s+your\b
    | \bprogram\s+your\s+path\b
    | \bjob\s+board\b
    | \bcareers?\s+home\b
    | \bjoin\s+(?:our|the)\s+team\b
    | \b\d+\s+jobs?\s+found\b
    | ^\s*(?:home|careers?|jobs?|job\s+search|working\s+here|find\s+a\s+job
          |search\s+results?|current\s+vacancies|job\s+vacancies|job\s+listings?
          |vacancies)\s*$
    | ^\s*(?:home|careers?|jobs?|job\s+search)\s*[-|–—:]\s*
    """
)

# Weaker phrasing that could also be a real title ("Executive Search Consultant").
HUB_WEAK_RE = re.compile(
    r"""(?ix)
    \bjob\s+search\b
    | \bsearch\s+results?\b
    | \bexecutive\s+search\b
    | \brecruitment\s+specialists?\b
    """
)

# Employer-profile boilerplate ("10 opportunities New Zealand").
HUB_DESCRIPTION_RE = re.compile(r"(?ix)\b\d+\s+opportunit(?:y|ies)\b")


def page_title_from_html(html: str | None) -> str | None:
    """Return the cleaned contents of the first <title> tag, if present."""
    if not html:
        return None
    match = re.search(r"<title[^>]*>(.*?)</title>", html, re.I | re.S)
    return clean_text(match.group(1)) if match else None


def _path_of(url: str | None) -> str:
    if not url:
        return ""
    try:
        return urlparse(url).path or ""
    except ValueError:
        return url


def _normalise_path(path: str | None) -> str:
    trimmed = (path or "").rstrip("/")
    return trimmed or "/"


def _same_page(left: str | None, right: str | None) -> bool:
    if not left or not right:
        return False
    try:
        left_url, right_url = urlparse(left), urlparse(right)
    except ValueError:
        return left.rstrip("/") == right.rstrip("/")
    if not left_url.netloc or not right_url.netloc:
        return left.rstrip("/") == right.rstrip("/")
    return (
        left_url.netloc.lower() == right_url.netloc.lower()
        and _normalise_path(left_url.path).lower() == _normalise_path(right_url.path).lower()
    )


def _has_job_signals(job: RawJobInput) -> bool:
    if job.salaryText and job.salaryText.strip():
        return True
    if job.location and job.location.strip():
        return True
    if job.postedDate:
        return True
    if job.sourceJobId:
        return True
    return bool(JOB_SIGNAL_RE.search(job.descriptionText or ""))


def candidate_rejection_reason(
    job: RawJobInput,
    crawl_url: str | None = None,
    page_title: str | None = None,
) -> str | None:
    """Return a reason when a candidate is junk (error page, article, hub), else None.

    Mirrors the .NET ``JobContentValidator`` so the worker and the API
    admin crawl-run trigger enforce the same gate. Only strong signals are used.
    """
    title = (job.title or "").strip()
    description = job.descriptionText or ""

    if len(title) < 3:
        return "empty or truncated title"

    if ERROR_PAGE_RE.search(title):
        return "error/404 page title"
    if page_title and ERROR_PAGE_RE.search(page_title):
        return "error/404 page <title>"
    if 0 < len(description) <= 400 and ERROR_PAGE_RE.search(description):
        return "error/404 page description"

    if ARTICLE_PATH_RE.search(_path_of(job.url)):
        return "article/advice url"
    if ARTICLE_TITLE_RE.search(title) and not _has_job_signals(job):
        return "article title without job signals"

    if HUB_STRONG_RE.search(title):
        return "hub/landing page title"

    if not job.postedDate:
        if HUB_WEAK_RE.search(title):
            return "hub/landing page title"
        if job.source and title.lower() == job.source.strip().lower():
            return "title equals source name"
        if (
            not (job.salaryText or "").strip()
            and _same_page(job.url, crawl_url)
            and not (job.location or "").strip()
        ):
            return "hub/landing page url"
        if (
            not (job.salaryText or "").strip()
            and not (job.location or "").strip()
            and HUB_DESCRIPTION_RE.search(description)
        ):
            return "hub/landing page description"

    return None


def is_acceptable_candidate(
    job: RawJobInput,
    crawl_url: str | None = None,
    page_title: str | None = None,
) -> bool:
    return candidate_rejection_reason(job, crawl_url, page_title) is None


#: Parsers whose descriptions come from full HTML pages (navigation, footers,
#: boilerplate). They must show the technology signal in the title itself.
TITLE_ONLY_IT_PARSERS = frozenset({"generic", "jsonld", "hays", "pageup", "trade-me"})


def is_probable_job_detail(url: str, title: str | None) -> bool:
    """Guard against blog/news pages reached through job-looking links.

    For generic extraction we accept a parsed page only when the URL itself looks
    like a job posting or the page title still carries a role keyword.  This stops
    content pages (for example a telematics blog post) entering the job table.
    """
    if is_probable_job_url(url):
        return True
    return looks_like_job_title(title or "")


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

    # Some ATS payloads contain literal escape sequences ("\r\n") as text.
    value = value.replace("\\r\\n", " ").replace("\\n", " ").replace("\\r", " ").replace("\\t", " ")
    value = re.sub(r"<script\b[^<]*(?:(?!</script>)<[^<]*)*</script>", " ", value, flags=re.I)
    value = re.sub(r"<style\b[^<]*(?:(?!</style>)<[^<]*)*</style>", " ", value, flags=re.I)
    value = re.sub(r"<[^>]+>", " ", value)
    value = unescape(value)
    return re.sub(r"\s+", " ", value).strip(" -*#\t\r\n")


def sanitize_href(href: str | None) -> str | None:
    """Clean malformed hrefs seen in the wild (stray quotes/whitespace)."""
    if not href:
        return None
    value = href.strip().strip("\"'").strip()
    if not value or any(character in value for character in ('"', "'", " ", "\n", "\t")):
        return None
    return value


def extract_links(html: str, base_url: str) -> list[tuple[str, str]]:
    parser = LinkParser()
    parser.feed(html)
    links: list[tuple[str, str]] = []
    for text, href in parser.links:
        cleaned = sanitize_href(href)
        if cleaned:
            links.append((text, urljoin(base_url, cleaned)))
    return links


def extract_next_links(html: str, base_url: str) -> list[str]:
    parser = LinkParser()
    parser.feed(html)
    return [urljoin(base_url, cleaned) for href in parser.next_links if (cleaned := sanitize_href(href))]


def looks_like_job_title(title: str) -> bool:
    lowered = title.lower()
    return any(keyword in lowered for keyword in JOB_KEYWORDS) and not any(
        word in lowered for word in SKIP_LINK_WORDS
    )


def is_concrete_job_url(url: str) -> bool:
    """True when a job-looking URL points at a specific posting, not a category.

    Rendered job cards often link with generic text ("View", "Apply"), so we also
    accept a URL whose final segment is an id or a role slug — but we reject
    category hubs such as ``/jobs/category/construction``.
    """
    if not is_probable_job_url(url):
        return False
    last = urlparse(url).path.rstrip("/").rsplit("/", 1)[-1]
    if re.search(r"\d{3,}", last):
        return True
    slug = re.sub(r"[-_]+", " ", last).lower()
    return any(keyword in slug for keyword in JOB_KEYWORDS)


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
    if parser == "seek":
        return path.startswith("/job/") and re.match(r"^/job/\d+", path) is not None
    if parser == "pageup":
        return "/job/" in path and re.search(r"/\d{6,}/?$", path) is not None
    if parser == "hays":
        return "/job-detail/" in path
    if parser == "trade-me":
        return "/listing/" in path
    if parser in ("", "generic", "jsonld"):
        # Rendered boards often use generic link text, so fall back to the URL shape.
        return looks_like_job_title(title) or is_concrete_job_url(url)
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


MAX_LOCATION_LENGTH = 80
MAX_LOCATION_SEGMENTS = 4

#: Tokens that mark the end of a location and the start of a detail block
#: ("Wellington Job type Permanent Specialisation …") or an ad sentence
#: ("Nelson, Nelson Region, New Zealand We also welcome …"). Mirrors the .NET
#: Application.Normalisation.LocationSanitiser.
LOCATION_STOP_RE = re.compile(
    r"""(?ix)
    \s*
    (?:
        job\s+type
      | job\s+description
      | speciali[sz]ation
      | reference
      | discipline
      | salary
      | category
      | posted
      | employment\s+type
      | work\s+type
      | contract\s+type
      | we\s+(?:also|are|have|offer|provide|believe|welcome)
      | the\s+role
      | about\s+(?:the|us|you|this)
      | key\s+responsibilit\w*
      | what\s+you
      | apply\s+now
      | please\s+(?:note|apply|contact)
      | join\s+(?:our|us|the)
    )\b
    """
)


def sanitise_location(value: str | None, max_length: int = MAX_LOCATION_LENGTH) -> str | None:
    """Reduce a raw location string to a place-shaped value.

    Cuts at the first non-location token, then caps the result at a handful of
    comma-separated segments and roughly ``max_length`` characters. Returns
    ``None`` when nothing place-like remains. The .NET normalisation pass runs
    the same logic again so a stale parser cannot leak description text into
    the ``location`` column.
    """
    if not value:
        return None

    original = re.sub(r"\s+", " ", value).strip()
    if not original:
        return None

    stop = LOCATION_STOP_RE.search(original)
    if stop and stop.start() > 0:
        original = original[: stop.start()]

    if len(original) > max_length:
        window = original[:max_length]
        cut = max(window.rfind(","), window.rfind(";"))
        if cut <= 0:
            cut = window.rfind(" ")
        original = window[:cut] if cut > 0 else window

    segments = original.split(",")
    if len(segments) > MAX_LOCATION_SEGMENTS:
        original = ",".join(segments[:MAX_LOCATION_SEGMENTS])

    clean = original.strip(" .,;|-")
    return clean or None


def extract_location(text: str) -> str | None:
    """Best-effort location for parsers with no explicit location field.

    Handles labelled fields ("Location: Auckland") and the common
    "Job in Christchurch Central, Canterbury - SEEK" / "Based in Wellington"
    phrasings so the .NET region mapper has a signal to work with. The result
    is always run through :func:`sanitise_location`, because a labelled field
    can otherwise absorb the remainder of a single-line page dump
    ("Location: Nelson, Nelson Region, New Zealand We also welcome …").
    """
    for label in ("Location", "Region", "Based in", "Located in"):
        labelled = extract_label(text, label)
        if labelled:
            return sanitise_location(labelled)

    match = re.search(
        r"(?:job\s+in|based\s+in|located\s+in)\s*:?\s*([^|\-–\n]+)",
        text,
        re.I,
    )
    return sanitise_location(clean_text(match.group(1))) if match else None


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
    location = extract_location(text)
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


def load_robots(origin: str) -> RobotsRules:
    """Fetch and cache robots.txt for an origin (unreachable means allow)."""
    cached = ROBOTS_CACHE.get(origin)
    if cached is not None:
        return cached
    try:
        text = http_request(
            urljoin(origin, "/robots.txt"),
            user_agent=DEFAULT_HTTP_USER_AGENT,
            timeout=20,
            retries=0,
        )
        rules = parse_robots(text)
    except Exception:  # noqa: BLE001 - no robots.txt (or unreachable) means no restriction
        rules = RobotsRules()
    ROBOTS_CACHE[origin] = rules
    return rules


def obeys_robots(url: str, user_agent: str) -> bool:
    match = re.match(r"^(https?://[^/]+)", url)
    if not match:
        return False

    origin = match.group(1).lower()
    return can_fetch(load_robots(origin), user_agent, url)

class _UndecodableBody(Exception):
    """Raised when the server used a content-encoding we cannot decode."""


def _decompress(raw: bytes, content_encoding: str) -> bytes:
    encoding = (content_encoding or "").lower().strip()
    if encoding in ("", "identity"):
        return raw
    if encoding == "gzip":
        import gzip

        return gzip.decompress(raw)
    if encoding == "deflate":
        import zlib

        return zlib.decompress(raw)
    if encoding in ("br", "brotli"):
        for module_name in ("brotli", "brotlicffi"):
            try:
                module = __import__(module_name)
                return module.decompress(raw)
            except Exception:  # noqa: BLE001 - try the next provider
                continue
        raise _UndecodableBody(encoding)
    raise _UndecodableBody(encoding)


def _sanitize(value: str, limit: int = 200) -> str:
    """Keep error context readable: drop control bytes and binary noise."""
    printable = re.sub(r"[^\x20-\x7e]+", " ", value)
    return re.sub(r"\s+", " ", printable).strip()[:limit]


def http_request(
    url: str,
    method: str = "GET",
    payload: dict[str, Any] | None = None,
    user_agent: str | None = None,
    timeout: int = 40,
    retries: int = 2,
) -> str:
    """Minimal urllib client used for JSON APIs and static pages.

    Scrapling is the primary browser runtime, but public ATS APIs and plain HTML
    endpoints need no browser, so the worker can run without the heavy Playwright
    dependency (and CI/tests stay fast).  429/503 responses are retried with
    exponential backoff, honouring ``Retry-After``.  If a server answers with
    brotli and no brotli decoder is installed, the request is retried once asking
    for an identity encoding so we never parse compressed bytes as text.
    """
    import ssl
    import urllib.error
    import urllib.request

    data = None
    base_headers = {
        "User-Agent": user_agent or DEFAULT_HTTP_USER_AGENT,
        "Accept": "application/json, text/html;q=0.9, */*;q=0.8",
        "Accept-Language": "en-NZ,en;q=0.9",
    }
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        base_headers["Content-Type"] = "application/json"

    context = ssl.create_default_context()
    attempt = 0
    encoding = "gzip"
    redirects = 0
    while True:
        headers = {**base_headers, "Accept-Encoding": encoding}
        request = urllib.request.Request(url, data=data, headers=headers, method=method.upper())
        try:
            with urllib.request.urlopen(request, timeout=timeout, context=context) as response:
                raw = response.read()
                content_encoding = response.headers.get("Content-Encoding") or ""
                charset = response.headers.get_content_charset() or "utf-8"
        except urllib.error.HTTPError as exc:
            location = exc.headers.get("Location")
            is_redirect = exc.code in (301, 302, 303, 307, 308)
            # Some servers answer with a redirect status but no Location header,
            # putting the real page in the body (Cloudflare/Liferay do this).
            # Use that body rather than failing the fetch.
            if is_redirect and not location:
                raw = exc.read()
                content_encoding = exc.headers.get("Content-Encoding") or ""
                charset = exc.headers.get_content_charset() or "utf-8"
            elif is_redirect and redirects < 5 and method.upper() in ("GET", "HEAD"):
                # Follow redirects urllib refuses to (for example HTML-escaped Location).
                url = urljoin(url, location.strip())
                redirects += 1
                continue
            elif exc.code in (429, 503) and attempt < retries:
                retry_after = (exc.headers.get("Retry-After") or "").strip()
                delay = float(retry_after) if retry_after.replace(".", "", 1).isdigit() else min(5 * (2**attempt), 60)
                time.sleep(delay)
                attempt += 1
                continue
            else:
                detail = ""
                try:
                    detail = _sanitize(
                        _decompress(exc.read(2000), exc.headers.get("Content-Encoding") or "").decode(
                            "utf-8", errors="replace"
                        )
                    )
                except Exception:  # noqa: BLE001 - best-effort error context
                    detail = ""
                raise RuntimeError(f"HTTP {exc.code} for {url}: {detail}") from exc
        except urllib.error.URLError as exc:
            raise RuntimeError(f"Network error for {url}: {exc.reason}") from exc

        try:
            return _decompress(raw, content_encoding).decode(charset, errors="replace")
        except _UndecodableBody:
            if encoding != "identity":
                encoding = "identity"
                continue
            raise RuntimeError(f"Unsupported content encoding '{content_encoding}' for {url}")


def json_fetch(url: str, method: str = "GET", payload: dict[str, Any] | None = None) -> Any:
    """JSON fetcher passed to :mod:`platform_api` clients."""
    return json.loads(http_request(url, method, payload) or "{}")


def fetch_with_http(args: argparse.Namespace, url: str) -> tuple[str, str]:
    """Static HTML fetcher used for sites that need no JavaScript rendering.

    Page fetches deliberately do not retry: a slow or challenged page must not
    stall a whole crawl run. Rate-limit retries belong to the JSON API client.
    """
    timeout = int(getattr(args, "fetch_timeout", 30) or 30)
    html = http_request(url, user_agent=getattr(args, "user_agent", None), timeout=timeout, retries=0)
    return html, clean_text(html)


def title_from_url(url: str) -> str:
    """Derive a readable fallback title from a job URL slug."""
    slug = urlparse(url).path.rstrip("/").rsplit("/", 1)[-1]
    slug = re.sub(r"\.(html?|php|aspx?)$", "", slug, flags=re.I)
    slug = re.sub(r"[-_]+", " ", slug)
    slug = re.sub(r"\b(jid|jr|req|ref|job|id)[- ]?\d+\b", " ", slug, flags=re.I)
    slug = re.sub(r"^\s*\d{3,}\b", " ", slug)          # leading ATS id (e.g. "1112438-senior-dev")
    slug = re.sub(r"\b\d{3,}\b\s*$", " ", slug)        # trailing ATS id (e.g. "senior-dev-92372")
    slug = re.sub(r"\s+", " ", slug).strip()
    return slug.title() if slug else "Untitled job"


def discover_sitemap_job_urls(
    base_url: str,
    fetch: Any,
    max_urls: int = 60,
    max_sitemaps: int = 8,
) -> list[str]:
    """Follow sitemap indexes and collect URLs that look like job postings.

    Many NZ career sites render listings with JavaScript but still publish a
    static XML sitemap, so sitemap-first discovery is the most reliable way to
    enumerate their openings.  Uses the injected ``fetch`` so it stays testable.
    """
    roots = ("sitemap.xml", "sitemap_index.xml", "sitemap-index.xml", "sitemap/sitemap.xml")
    queue = [urljoin(base_url, "/" + root) for root in roots]
    seen_sitemaps: set[str] = set()
    job_urls: list[str] = []

    while queue and len(seen_sitemaps) < max_sitemaps and len(job_urls) < max_urls:
        sitemap_url = queue.pop(0)
        if sitemap_url in seen_sitemaps or not same_site(base_url, sitemap_url):
            continue
        seen_sitemaps.add(sitemap_url)
        try:
            html, text = fetch(sitemap_url)
        except Exception:  # noqa: BLE001 - a missing sitemap is expected
            continue
        haystack = f"{text} {html}"
        if "<" not in haystack[:2000] and "http" not in haystack:
            continue
        for candidate in re.findall(r"https?://[^\s\"'<>\\]+", haystack):
            candidate = candidate.rstrip(".,)")
            if not same_site(base_url, candidate):
                continue
            if candidate.lower().endswith(".xml") or "sitemap" in candidate.lower():
                if candidate not in seen_sitemaps:
                    queue.append(candidate)
            elif is_probable_job_url(candidate):
                job_urls.append(candidate)
        if len(job_urls) >= max_urls:
            break

    return list(dict.fromkeys(job_urls))[:max_urls]


JOB_URL_HINT = re.compile(
    r"(?:^|/)(?:jobs?|positions?|vacanc(?:y|ies)|openings?|requisitions?)(?:[/_-]|$)", re.I
)
CAREERS_PATH_HINT = re.compile(r"/(?:careers?|work-with-us|join-us)(?:/|$)", re.I)
# Content sections that are never job postings, even when a slug mentions "job".
CONTENT_PATH_HINT = re.compile(
    r"/(?:blog|blogs|news|media|press|support|help|docs|resources|products?|accessories|"
    r"spare-parts|legal|terms|privacy|programmes?|internships?)(?:[/_-]|$)",
    re.I,
)


def is_probable_job_url(url: str) -> bool:
    """True when a URL looks like a *job posting* rather than site content.

    Precision matters: sitemaps are full of ``/apply-for-a-loan``,
    ``/blog/best-tech-jobs`` and ``/careers/benefits`` pages. We therefore
    require an explicit job segment (``/job/``, ``/vacancy/``, …); for career
    URLs we additionally require a role-like slug with an id or multiple words,
    and we always reject known content sections.
    """
    path = urlparse(url).path.lower()
    if JOB_URL_HINT.search(path):
        return True
    if CONTENT_PATH_HINT.search(path):
        return False
    if CAREERS_PATH_HINT.search(path):
        slug = path.rstrip("/").rsplit("/", 1)[-1]
        if not any(keyword in slug for keyword in JOB_KEYWORDS):
            return False
        # Role pages are usually multi-word or carry an id; "graduate"/"intern"
        # landing pages are not postings.
        return bool(re.search(r"\d{3,}", slug)) or slug.count("-") >= 2
    return False


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


def structured_job_to_raw(
    source: str,
    url: str,
    source_data: dict[str, Any],
    text: str,
    fallback_title: str,
) -> RawJobInput:
    """Map a Schema.org JobPosting dict onto the shared raw job shape."""
    location = structured_location(source_data)
    company = (source_data.get("hiringOrganization") or {}).get("name")
    source_job_id = (source_data.get("identifier") or {}).get("value")
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
        salaryText=extract_salary(text),
        descriptionText=description[:12000] or None,
        url=job_url,
        postedDate=iso_date(source_data.get("datePosted")),
        closingDate=iso_date(source_data.get("validThrough")),
    )


def parse_structured_jobs(html: str) -> list[dict[str, Any]]:
    """Return *every* Schema.org JobPosting on the page.

    Aggregator and listing pages can embed several JobPosting blocks (or an
    ``@graph`` containing them); harvesting all of them is far cheaper than
    following each detail link.
    """
    postings: list[dict[str, Any]] = []
    for match in re.finditer(
        r'<script[^>]+type=["\']application/ld\+json["\'][^>]*>(.*?)</script>',
        html,
        re.I | re.S,
    ):
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
            elif item.get("@type") == "JobPosting":
                postings.append(item)
    return postings


def parse_job_detail(
    source: str,
    url: str,
    html: str,
    text: str,
    fallback_title: str,
    parser: str = "generic",
) -> RawJobInput:
    if source_data := parse_structured_job(html):
        return structured_job_to_raw(source, url, source_data, text, fallback_title)

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
    if parser == "seek":
        return parse_seek_detail(source, url, html, text, fallback_title)
    if parser == "pageup":
        return parse_pageup_detail(source, url, html, text, fallback_title)
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
    # Live pages use <li class="flex-item job-location"><span>Location</span>Auckland</li>
    # while older fixtures use <dd>. Stop at whichever closing tag comes first so
    # the match cannot run on into the rest of the detail block.
    location = html_group(
        html,
        r'class="[^"]*job-location[^"]*"[^>]*>.*?<span[^>]*>Location</span>\s*(.*?)\s*</(?:dd|li)>',
    )
    if not location:
        location = html_group(html, r'class="[^"]*job-location[^"]*"[^>]*>\s*(.*?)\s*</(?:dd|li)>')
    location = sanitise_location(location)
    job_type = html_group(html, r'class="[^"]*job-type[^"]*"[^>]*>\s*<span[^>]*>Job type</span>\s*(.*?)\s*</(?:dd|li)>')
    ref = html_group(html, r'class="[^"]*job-ref[^"]*"[^>]*>.*?<span[^>]*>Reference</span>\s*(.*?)\s*</(?:dd|li)>')
    posted = html_group(html, r'class="[^"]*date-posted[^"]*"[^>]*>\s*<span[^>]*>Posted</span>\s*(.*?)\s*</(?:dd|li)>')
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


SEEK_WORK_TYPES = {
    "242": "Full-time",
    "243": "Part-time",
    "244": "Contract",
    "245": "Casual",
    "246": "Temporary",
}

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


def parse_seek_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Extract SEEK job cards from a search/listing page.

    SEEK's robots.txt disallows ``*/job/`` detail pages, so we deliberately never
    fetch them: the listing page is allowed and already carries title, company,
    location, classification, teaser and posting age for every card.
    """
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
        link = re.search(r'href="(/job/\d+)', chunk)
        if not title or not link:
            continue
        job_url = urljoin(url, link.group(1))
        if job_url in seen:
            continue
        seen.add(job_url)

        classification = field("jobClassification")
        subclass = field("jobSubClassification")
        teaser = field("jobShortDescription")
        prefix = ". ".join(part for part in (classification, subclass) if part)
        description = f"{prefix}. {teaser}" if prefix and teaser else (teaser or prefix)
        job_id = re.search(r"/job/(\d+)", job_url)

        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=job_id.group(1) if job_id else None,
                title=title,
                company=field("jobCompany"),
                location=sanitise_location(
                    field("jobLocation") or field("jobCardLocation") or extract_location(teaser or "")
                ),
                salaryText=extract_salary(chunk),
                descriptionText=(description or "")[:12000] or None,
                url=job_url,
                postedDate=relative_days_ago(field("jobListingDate")),
                closingDate=None,
            )
        )

    return jobs


def normalize_job_url(parser: str, url: str) -> str:
    """Drop tracking query strings for sites whose robots.txt disallows them."""
    if parser == "trade-me":
        parsed = urlparse(url)
        return parsed._replace(query="", fragment="").geturl()
    return url


def parse_seek_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse a SEEK NZ job page using its embedded ``window.SK_DL`` payload."""
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


_BROWSER_BINARY: str | None = None
_BROWSER_RESOLVED = False


def find_browser_binary() -> str | None:
    """Locate a Chromium/Chrome binary usable for ``--dump-dom`` rendering.

    Scrapling (Playwright/patchright) is the primary renderer, but a plain
    Chromium binary lets JavaScript sites be crawled without any Python browser
    dependency. Checks CHROME_BIN/CHROMIUM_BIN, PATH, then the Playwright cache.
    """
    global _BROWSER_BINARY, _BROWSER_RESOLVED
    if _BROWSER_RESOLVED:
        return _BROWSER_BINARY

    import glob
    import shutil

    candidates = [
        os.environ.get("CHROME_BIN"),
        os.environ.get("CHROMIUM_BIN"),
        "chromium",
        "chromium-browser",
        "google-chrome",
        "google-chrome-stable",
        "headless_shell",
    ]
    for candidate in candidates:
        if not candidate:
            continue
        path = candidate if os.sep in candidate else shutil.which(candidate)
        if path and os.path.exists(path):
            _BROWSER_BINARY = path
            _BROWSER_RESOLVED = True
            return path

    roots = [
        os.environ.get("PLAYWRIGHT_BROWSERS_PATH", ""),
        os.path.expanduser("~/.cache/ms-playwright"),
        "/root/.cache/ms-playwright",
    ]
    patterns = (
        # Playwright's headless shell is the most reliable for one-shot DOM dumps;
        # the full Chrome binary can wait on long-polling analytics and hang.
        "chromium_headless_shell-*/chrome-linux/headless_shell",
        "chromium-*/chrome-linux/chrome",
    )
    for root in roots:
        if not root:
            continue
        for pattern in patterns:
            matches = sorted(glob.glob(os.path.join(root, pattern)))
            if matches:
                _BROWSER_BINARY = matches[-1]
                _BROWSER_RESOLVED = True
                return _BROWSER_BINARY

    _BROWSER_RESOLVED = True
    return None


def fetch_with_browser(args: argparse.Namespace, url: str) -> tuple[str, str]:
    """Render a page with a headless Chromium binary and return its DOM."""
    import shutil
    import subprocess
    import tempfile

    binary = find_browser_binary()
    if not binary:
        raise RuntimeError("No Chromium/Chrome binary is available for browser rendering.")

    timeout = int(getattr(args, "browser_timeout", 45) or 45)
    budget = int(getattr(args, "render_wait_ms", 8000) or 8000)
    profile = tempfile.mkdtemp(prefix="nzjm-render-")
    command = [
        binary,
        "--headless",
        "--no-sandbox",
        "--disable-gpu",
        "--disable-dev-shm-usage",
        "--hide-scrollbars",
        "--mute-audio",
        "--no-first-run",
        "--disable-extensions",
        "--disable-default-apps",
        "--disable-background-networking",
        "--disable-sync",
        "--blink-settings=imagesEnabled=false",
        f"--user-data-dir={profile}",
        f"--virtual-time-budget={budget}",
        f"--user-agent={getattr(args, 'user_agent', DEFAULT_HTTP_USER_AGENT)}",
        "--dump-dom",
        url,
    ]
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(f"Browser render timed out after {timeout}s for {url}") from exc
    finally:
        shutil.rmtree(profile, ignore_errors=True)

    html = completed.stdout or ""
    if len(html) < 200:
        error = _sanitize(completed.stderr or "")
        raise RuntimeError(f"Browser render produced no DOM for {url}: {error}")
    return html, clean_text(html)


def build_fetcher(args: argparse.Namespace, fetcher_type: str) -> Any:
    """Return a page fetcher with graceful degradation.

    JavaScript sites prefer Scrapling's browser; if Scrapling is not installed we
    fall back to a plain Chromium ``--dump-dom`` render, and only if no browser
    exists at all do we use the static HTTP fetcher (correct for JSON APIs and
    server-rendered pages).
    """
    if fetcher_type == "http":
        return lambda url: fetch_with_http(args, url)
    if fetcher_type == "browser":
        return lambda url: fetch_with_browser(args, url)

    state = {"mode": "scrapling"}

    def _fetch(url: str) -> tuple[str, str]:
        if state["mode"] == "http":
            return fetch_with_http(args, url)
        if state["mode"] == "browser":
            return fetch_with_browser(args, url)
        try:
            return fetch_with_scrapling(args, url)
        except RuntimeError as exc:
            if "Scrapling is not installed" not in str(exc):
                raise
            if find_browser_binary():
                print(
                    "Scrapling is unavailable; rendering with the local Chromium binary.",
                    file=sys.stderr,
                )
                state["mode"] = "browser"
                return fetch_with_browser(args, url)
            print(
                "Scrapling and Chromium are unavailable; using the static HTTP fetcher.",
                file=sys.stderr,
            )
            state["mode"] = "http"
            return fetch_with_http(args, url)

    return _fetch


def crawl(
    args: argparse.Namespace,
    fetch: Any = None,
    sleeper: Any = time.sleep,
    api_fetch: Any = None,
) -> dict[str, Any]:
    parser = getattr(args, "parser", "generic")
    source_config = lookup_source(getattr(args, "source", None)) or {}
    platform = str(source_config.get("platform") or "").lower()

    if fetch is None:
        # A per-site fetcher in the registry overrides the global default: static
        # sites stay cheap while JavaScript-only career pages use a real browser.
        fetcher_type = str(
            getattr(args, "fetcher_override", None)
            or source_config.get("fetcher")
            or getattr(args, "fetcher_type", "fetcher")
        )
        fetch = build_fetcher(args, fetcher_type)

    # Public JSON posting APIs: no rendering, no pagination guesswork.
    if platform in API_PLATFORMS and parser != "halter-ashby" and parser in (platform, "generic"):
        return _crawl_api_platform(args, source_config, platform, api_fetch or json_fetch)

    if parser == "halter-ashby":
        # The documented public API is the access contract; its robots endpoint returns 401.
        html, text = fetch(args.url)
        jobs = parse_halter_ashby(args.source, html)
        return {
            "rawPages": [asdict(raw_page(args, args.url, html, text))],
            "jobs": [asdict(job) for job in jobs],
            "isComplete": True,
        }

    if parser == "seek":
        # SEEK disallows */job/ detail pages, so we only ever read listing pages,
        # which robots.txt allows (including ?keywords= slices).
        listing_urls = [args.url] + [
            item for item in (source_config.get("listingUrls") or []) if item
        ]
        seek_jobs: list[RawJobInput] = []
        seek_pages: list[RawJobPageInput] = []
        seen_ids: set[str] = set()
        for listing_url in listing_urls:
            if len(seek_pages) >= args.max_pages:
                break
            if not same_site(args.url, listing_url):
                continue
            if args.robots_txt_obey and not obeys_robots(listing_url, args.user_agent):
                continue
            if seek_pages:
                sleeper(args.delay_seconds)
            listing_html, listing_text = fetch(listing_url)
            seek_pages.append(raw_page(args, listing_url, listing_html, listing_text))
            for job in parse_seek_listing(args.source, listing_url, listing_html, listing_text):
                if job.url in seen_ids or not is_plausible_job_title(job.title, args.source):
                    continue
                seen_ids.add(job.url)
                seek_jobs.append(job)
        return {
            "rawPages": [asdict(page) for page in seek_pages],
            "jobs": [asdict(job) for job in seek_jobs],
            "isComplete": True,
        }

    if parser == "rss":
        # Job RSS/XML feeds (SuccessFactors SearchJobs/feed) list every vacancy.
        if args.robots_txt_obey and not obeys_robots(args.url, args.user_agent):
            raise RuntimeError(f"Robots.txt does not allow crawling {args.url}")
        xml, xml_text = fetch(args.url)
        rss_jobs = [
            job
            for job in parse_rss_jobs(args.source, xml or xml_text)
            if is_plausible_job_title(job.title, args.source) and looks_like_it_job(job.title, "")
        ]
        return {
            "rawPages": [asdict(raw_page(args, args.url, xml, xml_text))],
            "jobs": [asdict(job) for job in rss_jobs],
            "isComplete": True,
        }

    if parser == "linkedin":
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

    # Sitemap-first discovery: many NZ career sites render listings with
    # JavaScript but publish a static XML sitemap of their openings.
    sitemap_candidates: dict[str, str] = {}
    strategy = str(source_config.get("strategy") or "")
    if (
        getattr(args, "use_sitemap", False)
        or strategy in ("sitemap-html", "dynamic")
        or parser == "jsonld"
    ):
        try:
            for job_url in discover_sitemap_job_urls(
                args.url,
                fetch,
                max_urls=int(getattr(args, "sitemap_max_urls", 60) or 60),
            ):
                sitemap_candidates.setdefault(job_url, title_from_url(job_url))
        except Exception:  # noqa: BLE001 - sitemap is an optional accelerator
            sitemap_candidates = {}

    listing_queue = [args.url] + [
        item for item in (source_config.get("listingUrls") or []) if item
    ]
    if strategy == "sitemap-html" and sitemap_candidates:
        listing_queue = []
    visited: set[str] = set()
    candidates: dict[str, str] = {}
    inline_jobs: dict[str, RawJobInput] = {}
    pages: list[RawJobPageInput] = []

    while listing_queue and len(pages) < args.max_pages:
        url = listing_queue.pop(0)
        if url in visited or not same_site(args.url, url):
            continue
        if args.robots_txt_obey and not obeys_robots(url, args.user_agent):
            # Skip disallowed listing URLs (for example paginated query URLs) but keep
            # crawling the pages robots.txt does allow instead of failing the source.
            print(f"robots.txt disallows listing page, skipping: {url}", file=sys.stderr)
            continue
        if pages:
            sleeper(args.delay_seconds)

        html, text = fetch(url)
        visited.add(url)
        pages.append(raw_page(args, url, html, text))

        for title, job_url in extract_links(html, url):
            job_url = normalize_job_url(parser, job_url)
            if same_site(args.url, job_url) and is_job_link(parser, title, job_url):
                candidates.setdefault(job_url, title)
        for next_url in extract_next_links(html, url):
            if next_url not in visited and next_url not in listing_queue:
                listing_queue.append(next_url)

        # Listing pages that embed JobPosting JSON-LD are harvested directly,
        # which avoids a detail fetch per job.
        if parser in ("jsonld", "generic"):
            for source_data in parse_structured_jobs(html):
                inline = structured_job_to_raw(args.source, url, source_data, text, title_from_url(url))
                if not is_acceptable_job(inline):
                    continue
                if candidate_rejection_reason(inline, crawl_url=url, page_title=page_title_from_html(html)):
                    continue
                inline_jobs.setdefault(inline.url or url, inline)

        # Supplement candidates from listing-level parsers (sources where detail URLs
        # aren't standard <a> links but are embedded in JS/JSON/data attributes)
        supplementary_candidates = _supplement_candidates(args, url, html)
        for job_url, fallback_title in supplementary_candidates:
            if job_url not in candidates:
                candidates[job_url] = fallback_title

    for job_url, fallback_title in sitemap_candidates.items():
        candidates.setdefault(normalize_job_url(parser, job_url), fallback_title)

    jobs: list[RawJobInput] = list(inline_jobs.values())
    for url, fallback_title in candidates.items():
        if url in inline_jobs:
            continue
        if len(pages) >= args.max_pages:
            break
        if args.robots_txt_obey and not obeys_robots(url, args.user_agent):
            continue
        sleeper(args.delay_seconds)
        html, text = fetch(url)
        pages.append(raw_page(args, url, html, text))
        page_title = page_title_from_html(html)
        job = parse_job_detail(args.source, url, html, text, fallback_title, parser)
        if parser in TITLE_ONLY_IT_PARSERS:
            if parser in ("generic", "jsonld") and not is_probable_job_detail(url, job.title):
                continue
                continue
            # Rendered pages carry noisy navigation/footer text, so heuristic
            # extraction must see the technology signal in the title itself;
            # Full-page descriptions carry navigation and boilerplate, so these
            # parsers must see the technology signal in the title itself.
            accepted = is_plausible_job_title(job.title, args.source) and looks_like_it_job(job.title, "")
            if not accepted:
                continue
        elif not is_acceptable_job(job):
            continue

        # Shared gate: reject error/404 pages, articles and hub/landing pages.
        if candidate_rejection_reason(job, crawl_url=args.url, page_title=page_title):
            continue

        # Global boards (for example ANZ's Australian/NZ list) can be narrowed to
        # the source's region with locationTerms from the registry.
        location_terms = source_config.get("locationTerms") or []
        if location_terms and not any(
            str(term).lower() in (job.location or "").lower() for term in location_terms
        ):
            continue

        jobs.append(job)

    if not candidates and not inline_jobs and pages and parser not in ("generic", "jsonld"):
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
                job = parse_job_detail(args.source, first.url, html_chunk, text_chunk, "Untitled job", parser)
                if not candidate_rejection_reason(
                    job, crawl_url=args.url, page_title=page_title_from_html(first.rawHtml)
                ):
                    jobs.append(job)
            except ValueError:
                pass  # Parser rejected the page

    return {
        "rawPages": [asdict(page) for page in pages],
        "jobs": [asdict(job) for job in jobs],
        "isComplete": not listing_queue and len(jobs) == len(candidates),
    }


def _crawl_api_platform(
    args: argparse.Namespace,
    source_config: dict[str, Any],
    platform: str,
    api_fetch: Any,
) -> dict[str, Any]:
    """Fetch and normalise jobs straight from a public ATS JSON API."""
    config = dict(source_config)
    if platform == "adzuna":
        config.setdefault("appId", os.environ.get("ADZUNA_APP_ID", ""))
        config.setdefault("appKey", os.environ.get("ADZUNA_APP_KEY", ""))
        if not config.get("appId") or not config.get("appKey"):
            raise RuntimeError(
                "Adzuna crawling requires ADZUNA_APP_ID and ADZUNA_APP_KEY environment variables."
            )

    raw_jobs = crawl_platform(platform, api_fetch, source=args.source, config=config)
    jobs = [
        job
        for job in (RawJobInput(**raw) for raw in raw_jobs)
        if is_acceptable_job(job)
    ]
    raw_pages = [
        RawJobPageInput(
            source=args.source,
            url=args.url,
            rawHtml=None,
            markdown=None,
            rawJson=json.dumps(
                {"platform": platform, "jobsReturned": len(raw_jobs), "jobsKept": len(jobs)}
            ),
            crawledAt=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        )
    ]
    return {
        "rawPages": [asdict(page) for page in raw_pages],
        "jobs": [asdict(job) for job in jobs],
        "isComplete": True,
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


#: Parser values accepted on the CLI. Kept in one place so the test suite can
#: cross-check them against crawler/sources/sites.json: a registry parser that
#: is missing here makes argparse exit 2 and fails the source (the Xero /
#: Trade Me Jobs bug).
PARSER_CHOICES = (
    "generic",
    "jsonld",
    "govt-nz",
    "absolute-it",
    "talent-army",
    "halter-ashby",
    "beyond-recruitment",
    "consult-recruitment",
    "comspek",
    "salt",
    "digital-garage",
    "jobseek",
    "airnz",
    "linkedin",
    "seek",
    "trade-me",
    "pageup",
    "rss",
    "hays",
    "ashby",
    "greenhouse",
    "lever",
    "workable",
    "smartrecruiters",
    "bamboohr",
    "workday",
    "adzuna",
    "tribe",
    "oracle",
    "salesforce",
)

#: Fetcher back-ends accepted on the CLI.
FETCHER_CHOICES = ("fetcher", "dynamic", "stealth", "http", "browser")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Fetch and parse job pages with Scrapling.")
    parser.add_argument("--source", default="Scrapling")
    parser.add_argument("--url", required=True)
    parser.add_argument("--parser", choices=PARSER_CHOICES, default="generic")
    parser.add_argument("--fetcher-type", choices=FETCHER_CHOICES, default="fetcher")
    parser.add_argument(
        "--fetcher-override",
        choices=FETCHER_CHOICES,
        default="",
        help="force a fetcher even when the registry specifies one",
    )
    parser.add_argument(
        "--use-sitemap", action="store_true", help="discover job URLs from the XML sitemap"
    )
    parser.add_argument("--sitemap-max-urls", type=int, default=60)
    parser.add_argument("--max-pages", type=int, default=50)
    parser.add_argument("--delay-seconds", type=float, default=5)
    parser.add_argument("--fetch-timeout", type=int, default=30)
    parser.add_argument("--browser-timeout", type=int, default=45)
    parser.add_argument("--render-wait-ms", type=int, default=8000)
    parser.add_argument("--robots-txt-obey", action="store_true")
    parser.add_argument("--proxy-url", default="")
    parser.add_argument("--user-agent", default=DEFAULT_HTTP_USER_AGENT)
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
