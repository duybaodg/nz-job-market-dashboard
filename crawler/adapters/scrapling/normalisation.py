"""Text, title, location, salary and URL normalisation helpers.

Round 19: mechanically moved out of ``scrapling_worker.py``. The functions and
constants are unchanged; the worker re-exports this module's public surface so
existing imports keep working.
"""
from __future__ import annotations

import re
from datetime import datetime
from html import unescape
from html.parser import HTMLParser
from typing import Any
from urllib.parse import urljoin, urlparse

from models import RawJobInput

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


DEFAULT_HTTP_USER_AGENT = (
    "Mozilla/5.0 (compatible; NZJobMarketDashboardBot/0.2; +https://example.invalid/bot)"
)

#: HTML pages must be requested with a document ``Accept`` header. Some WAFs
#: (WP Engine/Cloudflare in front of Consult Recruitment) answer a page request
#: carrying the JSON-ish API Accept with a hard 403, while the same request with
#: a browser Accept header succeeds.
DEFAULT_HTML_ACCEPT = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"

#: Lower-case board/site names that appear as a trailing " - <name>" in a page
#: <title>; stripped by :func:`clean_job_title`.
TITLE_SITE_SUFFIXES = frozenset(
    {"smartrecruiters", "seek", "linkedin", "jobadder", "jobs", "job search", "careers"}
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
    # Round 12: role titles a live revival surfaced that the taxonomy missed.
    "systems administrator", "system administrator", "soc analyst",
    "deskside support",
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


#: Student Job Search is an all-industry student board. Its current ICT yield is
#: zero, while the broad "digital" keyword was matching "Digital Marketing
#: Coordinator" and "Digital Backpack Roaming ...". Require a concrete
#: technology role noun for this board instead of the generic title gate.
SJS_IT_TITLE_RE = re.compile(
    r"""(?ix)
    \b(?:
        software | developer | programmer | engineer | data | cloud | security
      | network | systems? | database | devops | qa | test | architect | analyst
      | administrator | technician | web | frontend | backend | full[\s-]*stack
      | machine\s+learning | artificial\s+intelligence | cyber | coding
      | help\s*desk | it\s+support
    )\b
    """
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


#: Placeholder page titles a JavaScript job board leaves on its detail route
#: before (or instead of) the role name. Student Job Search's Next.js detail
#: pages title the document "Job Details" or prepend a "See more results"
#: back-link breadcrumb, while the real title lives in the rendered <h3>. When
#: the listing gave us the card's anchor text, prefer that over these strings.
GENERIC_DETAIL_TITLE_RE = re.compile(
    r"""(?ix)
    ^\s*(?:see\s+more(?:\s+results)?|view\s+(?:job|details?)|job\s+details?
        |job\s+description|apply(?:\s+now)?|back\s+to\s+(?:jobs|results?)
        |share\s+(?:this\s+)?job|print\s+(?:this\s+)?job|loading)\b
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
    match = re.search(
        rf"(?:\*\*)?{label}(?:\*\*)?\s*:\s*(.+?)"
        r"(?:\s+(?:Category|Type|Apply|Sector|Posted|Work|Job|Employ|Salary|Location|"
        r"Discipline|Reference|Industry|Contact(?:\s+name)?|Career\s+growth|"
        # "Date" alone and "Date Posted"/"Date Expires" are stop labels: Student
        # Job Search renders "Employer: X Date Posted: ... Date Expires: ...",
        # which previously left a trailing " Date" on every company name.
        r"Closing\s+date|Department|Date(?:\s+(?:Posted|Expires|Listed|Updated|Closes?))?"
        r"):|$)",
        text,
        re.I,
    )
    return clean_text(match.group(1)) if match else None


#: Salary amounts must be anchored by a currency symbol, an explicit "k"
#: suffix or a fully written 4-6 digit number; otherwise bare number pairs in ad
#: teasers ("100-160") get misread as a salary. Mirrors the .NET
#: CrawlerOrchestrationService.ParseSalary anchors.
#:
#: The bare "NNk" alternative additionally requires a clean left/right boundary.
#: Without it, a phone country code inside a contact string
#: ("+64kayla@potentia.co.nz"), a document size ("198KB"), a decimal amount
#: ("$87.62 k/yr") or a headline quantity ("500k users") is read as 64k / 198k /
#: 62k / 500k. That produced the Potentia "Senior Software Engineer - iOS"
#: $64,000/yr misparse (Round 11).
SALARY_FRAGMENT_RE = re.compile(
    r"("
    r"(?:\$|NZD)\s*\d{1,3}(?:,\d{3})+(?:\.\d+)?\s*(?:-|to|–|—)\s*(?:\$|NZD)?\s*\d{1,3}(?:,\d{3})+(?:\.\d+)?"
    r"|(?<![\w.+])\d{2,3}\s?[kK]\s*(?:-|to|–|—)\s*\$?\s*\d{2,3}\s?[kK](?![a-zA-Z0-9])"
    r"|(?:\$|NZD)\s*\d{1,3}(?:,\d{3})+(?:\.\d+)?"
    r"|(?<![\w.+])\d{2,3}\s?[kK](?![a-zA-Z0-9])"
    r")",
    re.I,
)

#: Non-pay tokens a bare "NNk" scan can otherwise absorb. They are blanked
#: (spaces, so offsets are preserved) before the salary scan. This is the
#: contact/size/quantity half of the Round 11 misparse fix; the boundary rules
#: on :data:`SALARY_FRAGMENT_RE` catch the decimal half.
NON_PAY_TOKEN_RES = (
    re.compile(r"https?://\S+", re.I),
    re.compile(r"[\w.+-]+@[\w.-]+\.\w+", re.I),
    re.compile(r"\btel:\S+", re.I),
    # Document/data size: 198KB, 1.2MB, 4 GB.
    re.compile(r"\b\d[\d,.]*\s?[kKmMgGtT]?[bB]\b", re.I),
    # Headline quantity: 500k users, 1.2m customers, 300 downloads.
    re.compile(
        r"\b\d[\d,.]*\s?[kKmM]?\s*(?:users?|followers?|customers?|subscribers?|"
        r"downloads?|installs?|records?|rows?|employees?|clients?|companies|"
        r"businesses|students?|members?|visits?|views?|hits?|pages?|results?)\b",
        re.I,
    ),
)


def _mask_non_pay_tokens(text: str) -> str:
    """Blank contact strings, URLs, file sizes and quantity counts in-place.

    Blanking rather than deleting keeps every character offset stable, so
    :func:`_is_salary_filter_match` windows still line up with the original text.
    """
    if not text:
        return text
    chars = list(text)
    for pattern in NON_PAY_TOKEN_RES:
        for match in pattern.finditer(text):
            for index in range(match.start(), match.end()):
                chars[index] = " "
    return "".join(chars)


#: Salary-filter widgets ("Salary Annual Wage $0 $300,000+") sit in page
#: navigation and are not advertised pay. Reject a text-scan match that is part
#: of such a widget.
SALARY_FILTER_LABEL_RE = re.compile(
    r"annual\s+wage|salary\s+(?:range|filter)|salary\s+annual",
    re.I,
)


def _is_salary_filter_match(text: str, start: int) -> bool:
    window = text[max(0, start - 40) : start]
    return bool(SALARY_FILTER_LABEL_RE.search(window))


def extract_salary(text: str) -> str | None:
    text = _mask_non_pay_tokens(text)
    labelled = extract_label(text, "Salary")
    if labelled:
        # A labelled value can run into the next field; keep only its leading
        # salary-shaped fragment so "Salary: $120,000 Job type: Permanent" does
        # not become the whole salary string.
        labelled = clean_text(labelled)
        fragment = SALARY_FRAGMENT_RE.search(labelled)
        if fragment and not _is_salary_filter_match(labelled, fragment.start()):
            return clean_text(fragment.group(1))
        if len(labelled) <= 40 and any(ch.isdigit() for ch in labelled):
            return labelled
        return None

    for match in SALARY_FRAGMENT_RE.finditer(text):
        if _is_salary_filter_match(text, match.start()):
            continue
        return clean_text(match.group(1))
    return None


MAX_LOCATION_LENGTH = 60
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
      | job\s+category
      | speciali[sz]ation
      | reference
      | discipline
      | salary
      | category
      | posted
      | employment\s+type
      | work\s+type
      | contract\s+type
      | industry
      | contact\s+name
      | contact\s+details
      | contact
      | career\s+growth
      | closing\s+date
      | department
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

#: Known NZ places and work modes, mirrored from
#: Application.Normalisation.NormalisationService. Used to reject a stop-marker
#: cut that leaves a bare description fragment with no place name
#: ("centives" out of "...incentives Career growth ...").
_KNOWN_PLACE_RE = re.compile(
    r"""(?ix)
    (?<![\w])
    (?:
        auckland | manukau | north\ shore | waitakere | papakura | pukekohe | waiheke
      | wellington | porirua | lower\ hutt | upper\ hutt | kapiti | kāpiti
      | masterton | paraparaumu
      | christchurch | canterbury | timaru | ashburton | rangiora | rolleston
      | hamilton | waikato | taupo | taupō | cambridge | te\ awamutu | tokoroa | thames
      | dunedin | otago | queenstown | wanaka | oamaru | alexandra | cromwell
      | tauranga | bay\ of\ plenty | rotorua | whakatane | whakatāne | mount\ maunganui
      | palmerston\ north | manawatu | manawatū | whanganui | wanganui | levin
      | feilding | ohakune
      | hawke'?s\ bay | napier | hastings | havelock\ north
      | new\ plymouth | taranaki | stratford | hawera | hāwera
      | whangarei | whangārei | northland | kerikeri | kaitaia | dargaville
      | gisborne | nelson | blenheim | picton | marlborough | tasman | motueka
      | greymouth | hokitika | westport | west\ coast | invercargill | southland | gore
      | new\ zealand | nz | remote | work\ from\ home | wfh | anywhere
      | nationwide | countrywide | national
      | india | australia | united\ states | usa | united\ kingdom | uk | canada
      | singapore | philippines | ireland | germany | japan | china | malaysia
      | vietnam | indonesia | south\ africa | brazil | mexico | poland | spain
      | france | netherlands
    )
    (?![\w])
    """
)


def _looks_like_place(value: str) -> bool:
    """True when a value could be a location rather than a sentence fragment.

    Structured (comma/semicolon) values and values carrying a proper noun pass;
    a bare lower-case fragment only passes when it names a known place or work
    mode.
    """
    if not value:
        return False
    if "," in value or ";" in value:
        return True
    if any(character.isupper() for character in value):
        return True
    return _KNOWN_PLACE_RE.search(value) is not None


def _trim_to_word_boundary(prefix: str) -> str:
    if not prefix:
        return prefix
    if prefix[-1] in " -/,":
        return prefix
    cut = max(prefix.rfind(" "), prefix.rfind("-"), prefix.rfind("/"), prefix.rfind(","))
    return prefix[: cut + 1] if cut >= 0 else prefix


def _collapse_multi_region(value: str) -> str:
    """Compact a semicolon list by factoring the shared prefix.

    "Wellington - Hutt Valley;Wellington - Kapiti;Wellington - Masterton"
    becomes "Wellington - Hutt Valley; Kapiti; Masterton"; every region name is
    kept so the .NET region mapper still sees Wellington.
    """
    if ";" not in value:
        return value

    segments = [segment.strip() for segment in value.split(";") if segment.strip()]
    if len(segments) < 2:
        return value

    prefix = segments[0]
    for segment in segments[1:]:
        limit = min(len(prefix), len(segment))
        index = 0
        while index < limit and prefix[index].lower() == segment[index].lower():
            index += 1
        prefix = prefix[:index]

    prefix = _trim_to_word_boundary(prefix)
    if len(prefix) < 3:
        return value

    parts = [segments[0]]
    for segment in segments[1:]:
        remainder = segment[len(prefix):].lstrip(" -/,") if len(segment) > len(prefix) else ""
        parts.append(remainder or segment)
    return "; ".join(parts)


def sanitise_location(value: str | None, max_length: int = MAX_LOCATION_LENGTH) -> str | None:
    """Reduce a raw location string to a place-shaped value.

    Cuts at the first non-location token, rejects a bare fragment with no place
    name, compacts semicolon multi-region lists, then caps the result at a
    handful of comma-separated segments and roughly ``max_length`` characters.
    Returns ``None`` when nothing place-like remains. The .NET normalisation
    pass runs the same logic again so a stale parser cannot leak description
    text into the ``location`` column.
    """
    if not value:
        return None

    original = re.sub(r"\s+", " ", value).strip()
    if not original:
        return None

    stop = LOCATION_STOP_RE.search(original)
    if stop:
        if stop.start() == 0:
            return None
        original = original[: stop.start()]

    if not _looks_like_place(original):
        return None

    original = _collapse_multi_region(original)

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

    # The trailing word boundary is essential: without it "based in" matches
    # inside "Performance-based incentives", extracting "centives …".
    match = re.search(
        r"(?:job\s+in\b|based\s+in\b|located\s+in\b)\s*:?\s*([^|\-–\n]+)",
        text,
        re.I,
    )
    return sanitise_location(clean_text(match.group(1))) if match else None


def clean_job_title(title: str, source: str | None = None) -> str:
    """Remove a board's site suffix and the source-name prefix from a page title.

    SmartRecruiters titles pages as "Air New Zealand <role> | SmartRecruiters"
    and Datacom as "<role> | Datacom"; both should read as just the role.
    """
    value = clean_text(title or "")
    if "|" in value:
        head = value.split("|", 1)[0].strip()
        if head:
            value = head
    if " - " in value:
        head, _, tail = value.rpartition(" - ")
        if tail.strip().lower() in TITLE_SITE_SUFFIXES:
            value = head.strip()
    if source:
        prefix = source.strip()
        if prefix and value.lower().startswith(prefix.lower() + " "):
            value = value[len(prefix):].strip()
    return value or clean_text(title or "")


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


def normalize_job_url(parser: str, url: str) -> str:
    """Drop tracking query strings for sites whose robots.txt disallows them."""
    if parser == "trade-me":
        parsed = urlparse(url)
        return parsed._replace(query="", fragment="").geturl()
    return url


def iso_date(value: Any) -> str | None:
    if not value:
        return None
    match = re.match(r"\d{4}-\d{2}-\d{2}", str(value))
    return f"{match.group(0)}T00:00:00Z" if match else None


def same_site(left: str, right: str) -> bool:
    return urlparse(left).netloc.lower() == urlparse(right).netloc.lower()


def _same_site_redirect(original: str, redirected: str) -> bool:
    """Allow a redirect within the original host or its parent/subdomain.

    Redirects are a robots bypass if followed blindly, so a cross-registrable
    host is refused. Subdomains (www <-> bare, apply.example.com) are tolerated
    because they are the same site for this purpose.
    """
    left = (urlparse(original).hostname or "").lower()
    right = (urlparse(redirected).hostname or "").lower()
    if not left or not right:
        return False
    return (
        right == left
        or right.endswith("." + left)
        or left.endswith("." + right)
    )
