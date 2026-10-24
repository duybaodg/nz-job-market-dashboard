#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import random
import re
import subprocess
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

# The shared skill taxonomy lives at crawler/skills_taxonomy.json; adding the
# crawler root lets both the script and the imported-package test path reach it.
_CRAWLER_ROOT = os.path.dirname(os.path.dirname(_THIS_DIR))
if _CRAWLER_ROOT not in sys.path:
    sys.path.insert(0, _CRAWLER_ROOT)

from platform_api import API_PLATFORMS, crawl_platform  # noqa: E402
from robots import RobotsRules, can_fetch, parse_robots  # noqa: E402
from site_registry import lookup_source  # noqa: E402
from skills_taxonomy import default_extractor  # noqa: E402

# Round 19: the parsing/normalisation code lives in sibling modules now. The
# wildcard imports are a compatibility surface: tests and crawler/tools still
# import these names from ``adapters.scrapling.scrapling_worker``.
from models import *  # noqa: E402,F401,F403
from normalisation import *  # noqa: E402,F401,F403
from parsers.generic import *  # noqa: E402,F401,F403
from parsers.dispatch import *  # noqa: E402,F401,F403
from parsers.recruiters import *  # noqa: E402,F401,F403
from parsers.seek import *  # noqa: E402,F401,F403
from parsers.ats import *  # noqa: E402,F401,F403
from linkedin import *  # noqa: E402,F401,F403
from normalisation import _same_site_redirect  # noqa: E402


# Round 15: cache the parsed rules, or a short-lived explicit deny when the
# robots endpoint is unavailable (timeout, 5xx, 429, TLS error, 401). An
# unreachable robots.txt is unknown permission, not a grant. 404/410 means the
# site has no robots file and remains an allow.
ROBOTS_CACHE: dict[str, tuple[float, RobotsRules | None]] = {}
ROBOTS_RULES_TTL_SECONDS = 3600
ROBOTS_UNAVAILABLE_TTL_SECONDS = 300


class HttpStatusError(RuntimeError):
    """An HTTP response the caller must be able to distinguish from a transport error."""

    def __init__(self, status: int, url: str, detail: str = "") -> None:
        super().__init__(f"HTTP {status} for {url}: {detail}")
        self.status = status
        self.url = url


def load_robots(origin: str) -> RobotsRules | None:
    """Fetch and cache robots.txt for an origin.

    Returns ``None`` for an unavailable endpoint (cached as a short deny), an
    empty rule set for a genuine 404/410, and the parsed rules otherwise.
    """
    now = time.monotonic()
    cached = ROBOTS_CACHE.get(origin)
    if cached is not None:
        expires_at, rules = cached
        if now < expires_at:
            return rules

    try:
        text = http_request(
            urljoin(origin, "/robots.txt"),
            user_agent=DEFAULT_HTTP_USER_AGENT,
            timeout=20,
            retries=0,
            accept="text/plain,*/*;q=0.8",
        )
        rules = parse_robots(text)
        ROBOTS_CACHE[origin] = (now + ROBOTS_RULES_TTL_SECONDS, rules)
        return rules
    except HttpStatusError as exc:
        if exc.status in (404, 410):
            rules = RobotsRules()
            ROBOTS_CACHE[origin] = (now + ROBOTS_RULES_TTL_SECONDS, rules)
            return rules
        ROBOTS_CACHE[origin] = (now + ROBOTS_UNAVAILABLE_TTL_SECONDS, None)
        return None
    except Exception:  # noqa: BLE001 - timeout/TLS/other: unknown permission, fail closed
        ROBOTS_CACHE[origin] = (now + ROBOTS_UNAVAILABLE_TTL_SECONDS, None)
        return None


def obeys_robots(url: str, user_agent: str) -> bool:
    match = re.match(r"^(https?://[^/]+)", url)
    if not match:
        return False

    origin = match.group(1).lower()
    rules = load_robots(origin)
    if rules is None:
        return False
    return can_fetch(rules, user_agent, url)


#: Operator decision, 2026-10-23 (Bao Duy): the LinkedIn guest-endpoint crawl is
#: a scheduled source and hard rule #1 is waived FOR LINKEDIN GUEST CRAWLING
#: ONLY. linkedin.com robots.txt is ``User-agent: *`` -> ``Disallow: /``; this
#: is a knowing, operator-authorised exception (personal use, no login, honest
#: UA, low volume). It applies to NOTHING else. This is an ALLOWLIST OF ONE:
#: never add a second name, and never turn it into a config flag. Test
#: ``RobotsExemptionTests`` asserts the set is exactly {"LinkedIn Jobs"} and
#: that every other source still goes through :func:`obeys_robots`.
ROBOTS_EXEMPT_SOURCES = frozenset({"LinkedIn Jobs"})
LINKEDIN_GUEST_PARSER = "linkedin-guest"


def robots_allows_crawl(
    url: str, user_agent: str, *, parser: str, source: str | None
) -> bool:
    """Robots gate for a parser branch, with the single scoped exemption.

    The exemption is reachable only when BOTH the ``linkedin-guest`` parser and
    the allowlisted source name match. Every other parser/source combination
    (including the manual MCP ``linkedin`` parser) calls :func:`obeys_robots`.
    """
    if parser == LINKEDIN_GUEST_PARSER and source in ROBOTS_EXEMPT_SOURCES:
        return True
    return obeys_robots(url, user_agent)


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
    accept: str | None = None,
    retry_on_403: bool = False,
    retry_on_5xx: bool = False,
    backoff_base_ms: int = 5000,
    backoff_max_ms: int = 60000,
    jitter_ms: int = 0,
    robots_obey: bool = False,
    robots_strip_disallowed_query: bool = False,
) -> str:
    """Minimal urllib client used for JSON APIs and static pages.

    Scrapling is the primary browser runtime, but public ATS APIs and plain HTML
    endpoints need no browser, so the worker can run without the heavy Playwright
    dependency (and CI/tests stay fast).  429 responses are always retried; a
    source may opt in to retrying every 5xx (``retry_on_5xx``) and 403
    (``retry_on_403``, used by Consult Recruitment).  The delay grows
    exponentially from ``backoff_base_ms`` to ``backoff_max_ms`` with optional
    jitter, honouring a server ``Retry-After`` up to the same cap.  If a server
    answers with brotli and no brotli decoder is installed, the request is
    retried once asking for an identity encoding so we never parse compressed
    bytes as text.

    ``robots_strip_disallowed_query`` handles a real SEEK quirk: its SEO 301
    drops ``?keywords`` from the target, leaving a query-only URL that
    ``Disallow: *?`` forbids. When enabled, the redirect is followed only at
    the same canonical path with the disallowed query removed (which robots
    allows); the disallowed URL itself is never fetched. The caller's own
    client-side recency filter replaces the lost ``daterange`` hint.
    """
    import ssl
    import urllib.error
    import urllib.request

    data = None
    base_headers = {
        "User-Agent": user_agent or DEFAULT_HTTP_USER_AGENT,
        "Accept": accept or "application/json, text/html;q=0.9, */*;q=0.8",
        "Accept-Language": "en-NZ,en;q=0.9",
    }
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        base_headers["Content-Type"] = "application/json"

    attempt = 0
    encoding = "gzip"
    redirects = 0
    original_url = url

    class _NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: ANN001
            return None

    # Round 17: use urllib's default HTTPS context rather than an explicit
    # ``ssl.create_default_context()``. Both validate certificates, but some
    # WP Engine/Cloudflare WAFs fingerprint the ClientHello and answer 403 to the
    # explicit-context variant even for /robots.txt (Consult Recruitment). The
    # default handler's TLS fingerprint is accepted and keeps the same security
    # defaults, so robots.txt is readable and the fail-closed policy can make a
    # real decision instead of treating a WAF block as an unknown permission.
    opener = urllib.request.build_opener(
        urllib.request.HTTPSHandler(),
        _NoRedirect,
    )

    while True:
        headers = {**base_headers, "Accept-Encoding": encoding}
        request = urllib.request.Request(url, data=data, headers=headers, method=method.upper())
        try:
            with opener.open(request, timeout=timeout) as response:
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
            elif is_redirect and method.upper() in ("GET", "HEAD"):
                if redirects >= 5:
                    raise RuntimeError(f"Too many redirects for {original_url}") from exc
                resolved = urljoin(url, location.strip())
                parsed = urlparse(resolved)
                if parsed.scheme not in ("http", "https"):
                    raise RuntimeError(f"Refusing non-http(s) redirect from {url} to {resolved}") from exc
                if not _same_site_redirect(original_url, resolved):
                    raise RuntimeError(
                        f"Refusing cross-site redirect from {original_url} to {resolved}"
                    ) from exc
                if robots_obey and not obeys_robots(
                    resolved, user_agent or DEFAULT_HTTP_USER_AGENT
                ):
                    stripped = None
                    if robots_strip_disallowed_query and parsed.query:
                        candidate = parsed._replace(query="", fragment="").geturl()
                        if obeys_robots(
                            candidate, user_agent or DEFAULT_HTTP_USER_AGENT
                        ):
                            stripped = candidate
                    if stripped is None:
                        raise RuntimeError(
                            f"Robots.txt does not allow redirect target {resolved}"
                        ) from exc
                    print(
                        "Redirect target disallowed by robots; following the "
                        f"robots-allowed canonical path instead: {stripped}",
                        file=sys.stderr,
                    )
                    resolved = stripped
                url = resolved
                redirects += 1
                continue
            elif (
                exc.code == 429
                or exc.code == 503
                or (retry_on_5xx and 500 <= exc.code < 600)
                or (exc.code == 403 and retry_on_403)
            ) and attempt < retries:
                max_delay = max(backoff_max_ms, 1) / 1000
                retry_after = (exc.headers.get("Retry-After") or "").strip()
                if retry_after.replace(".", "", 1).isdigit():
                    delay = min(float(retry_after), max_delay)
                elif exc.code == 403:
                    # Flaky bot-management: back off quickly but repeatedly rather
                    # than waiting a full minute for a transient challenge.
                    delay = min(2 * (2**attempt), 30)
                else:
                    delay = min((backoff_base_ms / 1000) * (2**attempt), max_delay)
                if jitter_ms > 0:
                    delay += random.uniform(0, jitter_ms / 1000)
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
                raise HttpStatusError(exc.code, url, detail) from exc
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

    Page fetches do not retry by default: a slow or challenged page must not
    stall a whole crawl run. A registry entry may opt in to retries for a
    flaky bot-tolerant host via ``httpRetries``/``retryOn403`` (Consult
    Recruitment's WAF answers 403 for a fraction of requests). The document
    ``Accept`` header is deliberate; see :data:`DEFAULT_HTML_ACCEPT`.
    """
    timeout = int(getattr(args, "fetch_timeout", 30) or 30)
    config = lookup_source(getattr(args, "source", None)) or {}
    retries = int(config.get("httpRetries", 0) or 0)
    try:
        html = http_request(
            url,
            user_agent=getattr(args, "user_agent", None),
            timeout=timeout,
            retries=retries,
            accept=DEFAULT_HTML_ACCEPT,
            retry_on_403=bool(config.get("retryOn403")),
            retry_on_5xx=bool(config.get("retryOn5xx")),
            backoff_base_ms=int(config.get("httpBackoffBaseMs", 5000) or 5000),
            backoff_max_ms=int(config.get("httpBackoffMaxMs", 60000) or 60000),
            jitter_ms=int(config.get("httpJitterMs", 0) or 0),
            robots_obey=bool(getattr(args, "robots_txt_obey", False)),
            robots_strip_disallowed_query=bool(
                config.get("robotsStripDisallowedQuery")
            ),
        )
    except HttpStatusError as exc:
        # A missing page is an empty page for a source that opts in (SEEK's
        # keyword slices can disappear); every other HTTP error still raises.
        if exc.status == 404 and config.get("notFoundEmpty"):
            return "", ""
        raise
    return html, clean_text(html)


# --------------------------------------------------------------------------- #
# Firecrawl — optional remote fetch layer (self-hosted or cloud)
# --------------------------------------------------------------------------- #
#: Default timeout for a Firecrawl scrape, overridable by
#: FIRECRAWL_TIMEOUT_SECONDS / --firecrawl-timeout. Rendering a JavaScript board
#: through the Firecrawl browser service is slower than a static fetch.
FIRECRAWL_DEFAULT_TIMEOUT_SECONDS = 60


def firecrawl_config() -> tuple[str, str] | None:
    """Return ``(base_url, api_key)`` when Firecrawl is configured, else ``None``.

    ``FIRECRAWL_BASE_URL`` may include the API version segment (the self-hosted
    stack answers on both ``/v1`` and ``/v2``); the scrape endpoint is appended.
    """
    base = (os.environ.get("FIRECRAWL_BASE_URL") or "").strip().rstrip("/")
    if not base:
        return None
    return base, (os.environ.get("FIRECRAWL_API_KEY") or "").strip()


def firecrawl_scrape(url: str, timeout: int | None = None) -> dict[str, Any]:
    """POST a single-page scrape to Firecrawl and return its ``data`` object."""
    import ssl
    import urllib.error
    import urllib.request

    config = firecrawl_config()
    if config is None:
        raise RuntimeError("Firecrawl is not configured (set FIRECRAWL_BASE_URL).")
    base, api_key = config

    seconds = int(timeout or FIRECRAWL_DEFAULT_TIMEOUT_SECONDS)
    payload = {
        "url": url,
        "formats": ["markdown", "html", "rawHtml"],
        # Keep the full document so job links survive; the generic HTML parser
        # needs the surrounding listing markup, not just the main article.
        "onlyMainContent": False,
        "blockAds": True,
        "timeout": max(seconds, 1) * 1000,
    }
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json",
        "User-Agent": DEFAULT_HTTP_USER_AGENT,
    }
    if api_key:
        headers["Authorization"] = f"Bearer {api_key}"

    request = urllib.request.Request(
        f"{base}/scrape",
        data=json.dumps(payload).encode("utf-8"),
        headers=headers,
        method="POST",
    )
    try:
        with urllib.request.urlopen(
            request, timeout=seconds, context=ssl.create_default_context()
        ) as response:
            body = response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        detail = _sanitize(exc.read(2000).decode("utf-8", errors="replace"))
        raise RuntimeError(f"Firecrawl HTTP {exc.code} for {url}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Firecrawl network error for {url}: {exc.reason}") from exc

    try:
        document = json.loads(body)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"Firecrawl returned invalid JSON for {url}") from exc

    if not isinstance(document, dict) or document.get("success") is False:
        raise RuntimeError(f"Firecrawl scrape failed for {url}: {_sanitize(body)}")

    data = document.get("data")
    if not isinstance(data, dict):
        raise RuntimeError(f"Firecrawl returned no data for {url}")
    return data


def fetch_with_firecrawl(args: argparse.Namespace, url: str) -> tuple[str, str]:
    """Fetch one page through Firecrawl, enforcing robots.txt first.

    Firecrawl's ``/scrape`` endpoint is a single-URL fetch and does **not**
    consult robots.txt (unlike its ``/crawl`` endpoint), so this worker checks
    the shared :mod:`robots` matcher before handing the URL to the self-hosted
    instance. The Firecrawl path is therefore never more permissive than the
    Scrapling path.
    """
    if getattr(args, "robots_txt_obey", False) and not obeys_robots(
        url, getattr(args, "user_agent", DEFAULT_HTTP_USER_AGENT)
    ):
        raise RuntimeError(f"Robots.txt does not allow crawling {url}")

    timeout = getattr(args, "firecrawl_timeout", None) or FIRECRAWL_DEFAULT_TIMEOUT_SECONDS
    data = firecrawl_scrape(url, int(timeout))
    final_url = data.get("url") or data.get("metadata", {}).get("url") if isinstance(data.get("metadata"), dict) else data.get("url")
    if (
        getattr(args, "robots_txt_obey", False)
        and isinstance(final_url, str)
        and final_url
        and final_url != url
        and not obeys_robots(final_url, getattr(args, "user_agent", DEFAULT_HTTP_USER_AGENT))
    ):
        raise RuntimeError(f"Robots.txt does not allow Firecrawl redirect target {final_url}")
    html = data.get("rawHtml") or data.get("html") or ""
    markdown = data.get("markdown") or ""
    if not isinstance(html, str):
        html = ""
    if not isinstance(markdown, str):
        markdown = ""
    if not html and not markdown:
        raise RuntimeError(f"Firecrawl returned no content for {url}")
    return html, markdown or clean_text(html)


def discover_sitemap_job_urls(
    base_url: str,
    fetch: Any,
    max_urls: int = 60,
    max_sitemaps: int = 8,
    args: Any = None,
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
        if (
            args is not None
            and getattr(args, "robots_txt_obey", False)
            and not obeys_robots(sitemap_url, getattr(args, "user_agent", DEFAULT_HTTP_USER_AGENT))
        ):
            # sitemap.xml is a normal URL and is subject to robots.txt.
            seen_sitemaps.add(sitemap_url)
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


def fetch_with_scrapling(
    args: argparse.Namespace, url: str, fetcher_type: str | None = None
) -> tuple[str, str]:
    # Use the *resolved* fetcher (registry override or CLI), not args.fetcher_type:
    # a per-site "dynamic"/"stealth" choice must select the matching Scrapling
    # fetcher even when the global --fetcher-type is something else.
    mode = fetcher_type or getattr(args, "fetcher_type", "fetcher")
    try:
        if mode == "dynamic":
            from scrapling.fetchers import DynamicFetcher as Fetcher
        elif mode == "stealth":
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

    # A registry ``renderWaitSelector`` overrides the network-idle default for
    # boards that never reach idle. Student Job Search keeps long-lived
    # analytics/Maps connections open, so waiting for network silence cost ~40 s
    # per page and still raced the job-data XHR; waiting for the rendered card or
    # detail container returns as soon as the content exists.
    source_config = lookup_source(getattr(args, "source", None)) or {}
    wait_selector = str(source_config.get("renderWaitSelector") or "").strip()

    is_dynamic = mode in ("dynamic", "stealth")
    if is_dynamic:
        fetch_kwargs.setdefault("headless", True)
        # Wait for the SPA's network to settle. Several boards (Robert Walters,
        # Datacom, ZEIL) only render the posting list after their XHR finishes;
        # an early snapshot looks empty and is easily mistaken for a bot block.
        if wait_selector:
            fetch_kwargs["network_idle"] = False
            fetch_kwargs["wait_selector"] = wait_selector
        else:
            fetch_kwargs.setdefault("network_idle", True)
        fetch_kwargs.setdefault(
            "timeout", max(int(getattr(args, "browser_timeout", 45) or 45), 45) * 1000
        )
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


def _firecrawl_fallback(args: argparse.Namespace, url: str, primary_error: Exception) -> tuple[str, str]:
    """Retry a failed browser fetch through Firecrawl; raise the primary cause.

    Used by the degradation chain only when Firecrawl is configured. Both causes
    are reported so an operator can tell a site block from a Firecrawl outage.
    """
    if firecrawl_config() is None:
        raise primary_error
    print(f"Browser fetch failed for {url}; retrying with Firecrawl.", file=sys.stderr)
    try:
        return fetch_with_firecrawl(args, url)
    except Exception as fallback_error:  # noqa: BLE001 - surface both causes
        raise RuntimeError(
            f"Browser fetch failed for {url} ({_sanitize(str(primary_error))}); "
            f"Firecrawl fallback also failed ({_sanitize(str(fallback_error))})."
        ) from fallback_error


def build_fetcher(args: argparse.Namespace, fetcher_type: str) -> Any:
    """Return a page fetcher with graceful degradation.

    JavaScript sites prefer Scrapling's browser; if that render fails or times
    out, the self-hosted Firecrawl instance is tried as an automatic fallback
    (it drives its own browser service). If Scrapling is not installed we still
    prefer a plain Chromium ``--dump-dom`` render, then Firecrawl, and only if
    none of those are available do we use the static HTTP fetcher (correct for
    JSON APIs and server-rendered pages).

    Firecrawl enforces robots.txt itself in :func:`fetch_with_firecrawl`, so the
    fallback can never fetch a URL the primary path would have refused.
    """
    if fetcher_type == "http":
        return lambda url: fetch_with_http(args, url)
    if fetcher_type == "firecrawl":
        return lambda url: fetch_with_firecrawl(args, url)

    state = {"mode": "scrapling", "fetcher": fetcher_type}

    def _fetch(url: str) -> tuple[str, str]:
        if state["mode"] == "http":
            return fetch_with_http(args, url)
        if state["mode"] == "browser":
            try:
                return fetch_with_browser(args, url)
            except Exception as exc:  # noqa: BLE001 - Firecrawl is the fallback
                return _firecrawl_fallback(args, url, exc)
        try:
            return fetch_with_scrapling(args, url, state["fetcher"])
        except Exception as exc:  # noqa: BLE001 - render failures fall back
            if "Scrapling is not installed" not in str(exc):
                return _firecrawl_fallback(args, url, exc)
            if find_browser_binary():
                print(
                    "Scrapling is unavailable; rendering with the local Chromium binary.",
                    file=sys.stderr,
                )
                state["mode"] = "browser"
                try:
                    return fetch_with_browser(args, url)
                except Exception as browser_error:  # noqa: BLE001
                    if firecrawl_config() is not None:
                        print(
                            f"Local browser render failed for {url}; retrying with Firecrawl.",
                            file=sys.stderr,
                        )
                        try:
                            return fetch_with_firecrawl(args, url)
                        except Exception as fallback_error:  # noqa: BLE001
                            print(
                                f"Firecrawl fallback failed for {url}: {_sanitize(str(fallback_error))}",
                                file=sys.stderr,
                            )
                    raise browser_error
            if firecrawl_config() is not None:
                print(
                    f"Scrapling and Chromium are unavailable; trying Firecrawl for {url}.",
                    file=sys.stderr,
                )
                try:
                    return fetch_with_firecrawl(args, url)
                except Exception as fallback_error:  # noqa: BLE001
                    print(
                        f"Firecrawl fallback failed for {url}: {_sanitize(str(fallback_error))}",
                        file=sys.stderr,
                    )
            print(
                "Scrapling and Chromium are unavailable; using the static HTTP fetcher.",
                file=sys.stderr,
            )
            state["mode"] = "http"
            return fetch_with_http(args, url)

    return _fetch


def resolve_max_pages(args: argparse.Namespace, source_config: dict[str, Any]) -> int:
    """Cap the CLI ``--max-pages`` with a registry ``maxPages`` override.

    JavaScript-rendered boards can have dozens of paginated listing URLs; a
    single global limit of 50 makes the source exceed the adapter timeout. A
    per-site cap keeps those crawls bounded and polite. A source that enumerates
    a finite sitemap (Xero: 64 technical candidates from a 117-URL sitemap) may
    set ``extendMaxPages`` to use its own larger, still-bounded cap.
    """
    cli_value = int(getattr(args, "max_pages", 50) or 50)
    configured = source_config.get("maxPages")
    if isinstance(configured, int) and configured > 0:
        if source_config.get("extendMaxPages"):
            return max(cli_value, configured)
        return min(cli_value, configured)
    return cli_value


def crawl(
    args: argparse.Namespace,
    fetch: Any = None,
    sleeper: Any = time.sleep,
    api_fetch: Any = None,
) -> dict[str, Any]:
    parser = getattr(args, "parser", "generic")
    source_config = lookup_source(getattr(args, "source", None)) or {}
    platform = str(source_config.get("platform") or "").lower()
    args.max_pages = resolve_max_pages(args, source_config)
    # A source may pin a smaller/larger inter-request delay than the global
    # SCRAPLING_DOWNLOAD_DELAY_SECONDS. Xero's sitemap crawl fetches ~64 pages,
    # so the global 5 s would consume the whole 360 s worker budget; 0.5 s is
    # still polite for a same-host sequential read of public postings.
    configured_delay = source_config.get("delaySeconds")
    if isinstance(configured_delay, (int, float)) and configured_delay >= 0:
        args.delay_seconds = float(configured_delay)

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
        # Round 15: the documented public API is still fetched over HTTP, so
        # robots.txt applies. Its robots endpoint answers 401 (unknown
        # permission), and the fail-closed policy refuses the crawl. Halter is
        # disabled in the registry; re-enabling requires an operator-approved
        # documented exemption, not a silent bypass.
        if args.robots_txt_obey and not obeys_robots(args.url, args.user_agent):
            raise RuntimeError(f"Robots.txt does not allow crawling {args.url}")
        html, text = fetch(args.url)
        jobs = parse_halter_ashby(args.source, html)
        return {
            "rawPages": [asdict(raw_page(args, args.url, html, text))],
            "jobs": [asdict(job) for job in jobs],
            "isComplete": True,
            "stats": {"candidates": len(jobs), "kept": len(jobs)},
        }

    if parser == "seek":
        # SEEK disallows */job/ detail pages, so we only ever read listing pages,
        # which robots.txt allows (including ?keywords= slices).
        # SEEK's SEO 301 drops ?keywords from these keyword URLs, leaving a
        # query-only target that robots.txt forbids. The registry therefore pins
        # the verified 200 canonical paths to fetch directly; one request per
        # keyword also avoids the redirect double-fetch that triggered 429s.
        configured_urls = (
            source_config.get("canonicalListingUrls")
            or source_config.get("listingUrls")
            or []
        )
        listing_urls = [item for item in configured_urls if item]
        if not listing_urls:
            listing_urls = [args.url]
        seek_jobs: list[RawJobInput] = []
        seek_pages: list[RawJobPageInput] = []
        seen_ids: set[str] = set()
        rate_limited: list[str] = []
        skipped_by_robots = 0
        for listing_url in listing_urls:
            if len(seek_pages) >= args.max_pages:
                break
            if not same_site(args.url, listing_url):
                continue
            if args.robots_txt_obey and not obeys_robots(listing_url, args.user_agent):
                skipped_by_robots += 1
                continue
            if seek_pages:
                sleeper(args.delay_seconds)
            try:
                listing_html, listing_text = fetch(listing_url)
            except HttpStatusError as exc:
                # A single keyword slice being rate-limited after the full
                # backoff should not throw away the pages that succeeded. A run
                # with NO usable pages still fails loudly below.
                if exc.status in (429, 503):
                    rate_limited.append(listing_url)
                    print(
                        f"SEEK listing rate-limited after backoff: {listing_url}",
                        file=sys.stderr,
                    )
                    continue
                raise
            seek_pages.append(raw_page(args, listing_url, listing_html, listing_text))
            for job in parse_seek_listing(args.source, listing_url, listing_html, listing_text):
                if job.url in seen_ids or not is_plausible_job_title(job.title, args.source):
                    continue
                seen_ids.add(job.url)
                seek_jobs.append(job)
        if not seek_pages:
            if rate_limited:
                raise RuntimeError(
                    "SEEK listing pages were rate-limited after backoff: "
                    + ", ".join(rate_limited)
                )
            raise RuntimeError(
                "SEEK crawl fetched no listing pages "
                f"(robots-skipped={skipped_by_robots}, configured={len(listing_urls)})"
            )
        return {
            "rawPages": [asdict(page) for page in seek_pages],
            "jobs": [asdict(job) for job in seek_jobs],
            # A partial page set must not let reconciliation mark unfetched jobs
            # as missing; only a complete listing sweep may do that.
            "isComplete": not rate_limited and skipped_by_robots == 0,
            "stats": {"candidates": len(seen_ids), "kept": len(seek_jobs)},
        }

    if parser == "xero":
        # Round 17: api.ashbyhq.com's robots endpoint answers 401 and the strict
        # policy correctly refuses it. careers.xero.com is the robots-allowed
        # board and its sitemap enumerates every posting, so read the public
        # JobPosting JSON embedded in each detail page instead of the ATS API.
        if args.robots_txt_obey and not obeys_robots(args.url, args.user_agent):
            raise RuntimeError(f"Robots.txt does not allow crawling {args.url}")
        sitemap_url = urljoin(args.url, "/sitemap.xml")
        if args.robots_txt_obey and not obeys_robots(sitemap_url, args.user_agent):
            raise RuntimeError(f"Robots.txt does not allow crawling {sitemap_url}")
        sitemap_xml, _ = fetch(sitemap_url)
        all_urls = parse_xero_sitemap(sitemap_xml)
        if not all_urls:
            raise RuntimeError(f"Xero sitemap returned no job URLs: {sitemap_url}")
        candidates = [
            job_url
            for job_url in all_urls
            if xero_title_is_it_candidate(title_from_url(job_url))
        ]
        xero_jobs: list[RawJobInput] = []
        xero_pages: list[RawJobPageInput] = []
        xero_skipped = 0
        for job_url in candidates:
            if len(xero_pages) >= args.max_pages:
                break
            if args.robots_txt_obey and not obeys_robots(job_url, args.user_agent):
                xero_skipped += 1
                continue
            if xero_pages:
                sleeper(args.delay_seconds)
            html, page_text = fetch(job_url)
            xero_pages.append(raw_page(args, job_url, html, page_text))
            source_data = parse_structured_job(html)
            if not source_data:
                continue
            job = structured_job_to_raw(
                args.source, job_url, source_data, page_text, title_from_url(job_url)
            )
            # Xero's board is global. Keep only New Zealand locations; AU/UK/
            # US/Canada roles must never leak into the NZ market feed.
            if "new zealand" not in (job.location or "").lower():
                continue
            industry = clean_text(str(source_data.get("industry") or "")).lower()
            if not (looks_like_it_job(job.title) or industry in XERO_TECH_INDUSTRIES):
                continue
            if not is_plausible_job_title(job.title, args.source):
                continue
            if candidate_rejection_reason(
                job, crawl_url=args.url, page_title=page_title_from_html(html)
            ):
                continue
            xero_jobs.append(job)
        if not xero_pages:
            raise RuntimeError(
                "Xero crawl fetched no job pages "
                f"(sitemap={len(all_urls)}, candidates={len(candidates)}, "
                f"robots-skipped={xero_skipped})"
            )
        return {
            "rawPages": [asdict(page) for page in xero_pages],
            "jobs": [asdict(job) for job in xero_jobs],
            "isComplete": len(xero_pages) == len(candidates) and xero_skipped == 0,
            "stats": {"candidates": len(candidates), "kept": len(xero_jobs)},
        }

    if parser == "sjs":
        # The new /jobs board is one rendered card grid. Its all-industry feed
        # currently contains no IT cards, but the UI's ?q= search parameter is
        # reflected in the rendered grid, so the registry lists a few technology
        # keyword slices. Every URL is robots-checked before it is fetched and
        # the sjs parser applies a concrete-IT-title gate to the union.
        listing_urls = [args.url] + [
            item for item in (source_config.get("listingUrls") or []) if item
        ]
        sjs_pages: list[RawJobPageInput] = []
        cards_by_url: dict[str, RawJobInput] = {}
        for listing_url in listing_urls:
            if not same_site(args.url, listing_url):
                continue
            if args.robots_txt_obey and not obeys_robots(listing_url, args.user_agent):
                continue
            if sjs_pages:
                sleeper(args.delay_seconds)
            html, page_text = fetch(listing_url)
            sjs_pages.append(raw_page(args, listing_url, html, page_text))
            for card in parse_sjs_listing(args.source, listing_url, html):
                cards_by_url.setdefault(card.url, card)
        if not sjs_pages or not cards_by_url:
            raise RuntimeError(
                f"Student Job Search rendered no job cards at {args.url}"
            )
        jobs = [
            job
            for job in cards_by_url.values()
            if is_plausible_job_title(job.title, args.source)
            and SJS_IT_TITLE_RE.search(job.title)
            and looks_like_it_job(job.title, job.descriptionText or "")
            and candidate_rejection_reason(job, crawl_url=args.url)
            is None
        ]
        return {
            "rawPages": [asdict(page) for page in sjs_pages],
            "jobs": [asdict(job) for job in jobs],
            "isComplete": len(sjs_pages) == len(listing_urls),
            "stats": {"candidates": len(cards_by_url), "kept": len(jobs)},
        }

    if parser == "employment-hero":
        # New source (Round 17): server-rendered cards filtered to IT, then the
        # detail page's JobPosting JSON-LD supplies description and provenance.
        listing_urls = [args.url] + [
            item for item in (source_config.get("listingUrls") or []) if item
        ]
        listing_pages: list[RawJobPageInput] = []
        cards_by_url: dict[str, RawJobInput] = {}
        for listing_url in listing_urls:
            if not same_site(args.url, listing_url):
                continue
            if args.robots_txt_obey and not obeys_robots(listing_url, args.user_agent):
                continue
            if listing_pages:
                sleeper(args.delay_seconds)
            listing_html, listing_text = fetch(listing_url)
            listing_pages.append(raw_page(args, listing_url, listing_html, listing_text))
            for card in parse_employment_hero_listing(
                args.source, listing_url, listing_html
            ):
                cards_by_url.setdefault(card.url, card)
        if not listing_pages:
            raise RuntimeError("Employment Hero crawl fetched no listing pages")

        candidates = [
            card
            for card in cards_by_url.values()
            if is_plausible_job_title(card.title, args.source)
        ]
        detail_pages: list[RawJobPageInput] = []
        jobs: list[RawJobInput] = []
        detail_failures = 0
        truncated = False
        for card in candidates:
            if len(detail_pages) >= args.max_pages:
                truncated = True
                break
            if args.robots_txt_obey and not obeys_robots(card.url, args.user_agent):
                detail_failures += 1
                continue
            if detail_pages:
                sleeper(args.delay_seconds)
            detail_page_title: str | None = None
            try:
                html, page_text = fetch(card.url)
            except Exception:  # noqa: BLE001 - keep the valid listing card
                detail_failures += 1
                job = card
            else:
                detail_pages.append(raw_page(args, card.url, html, page_text))
                detail_page_title = page_title_from_html(html)
                job = parse_job_detail(
                    args.source, card.url, html, page_text, card.title, "jsonld"
                )
                job = RawJobInput(
                    source=job.source,
                    sourceJobId=job.sourceJobId or card.sourceJobId,
                    title=job.title or card.title,
                    company=job.company or card.company,
                    location=job.location or card.location,
                    salaryText=job.salaryText or card.salaryText,
                    descriptionText=job.descriptionText or card.descriptionText,
                    url=card.url,
                    postedDate=job.postedDate,
                    closingDate=job.closingDate,
                )
            if not is_plausible_job_title(job.title, args.source):
                continue
            # The sector filter still returns sales/partnership roles, so the
            # final gate reads the detail description (title+description). A
            # title-only gate dropped real roles such as Group Systems
            # Specialist and Senior Technical Support Representative.
            if not looks_like_it_job(job.title, job.descriptionText or ""):
                continue
            if candidate_rejection_reason(
                job, crawl_url=args.url, page_title=detail_page_title
            ):
                continue
            jobs.append(job)
        return {
            "rawPages": [asdict(page) for page in listing_pages + detail_pages],
            "jobs": [asdict(job) for job in jobs],
            "isComplete": not truncated and detail_failures == 0,
            "stats": {"candidates": len(cards_by_url), "kept": len(jobs)},
        }

    if parser == "rss":
        # Job RSS/XML feeds (SuccessFactors SearchJobs/feed) list every vacancy.
        if args.robots_txt_obey and not obeys_robots(args.url, args.user_agent):
            raise RuntimeError(f"Robots.txt does not allow crawling {args.url}")
        xml, xml_text = fetch(args.url)
        parsed_rss = parse_rss_jobs(args.source, xml or xml_text)
        rss_jobs = [
            job
            for job in parsed_rss
            if is_plausible_job_title(job.title, args.source) and looks_like_it_job(job.title, "")
        ]
        return {
            "rawPages": [asdict(raw_page(args, args.url, xml, xml_text))],
            "jobs": [asdict(job) for job in rss_jobs],
            "isComplete": True,
            "stats": {"candidates": len(parsed_rss), "kept": len(rss_jobs)},
        }

    if parser == "linkedin-guest":
        # Operator-authorised scoped exemption (2026-10-23): LinkedIn guest
        # crawling ONLY. The robot gate is still consulted so a future source
        # name change cannot silently inherit the exemption.
        if args.robots_txt_obey and not robots_allows_crawl(
            args.url, args.user_agent, parser=parser, source=args.source
        ):
            raise RuntimeError(f"Robots.txt does not allow crawling {args.url}")
        guest_jobs, guest_meta = run_linkedin_guest_crawl(args.source)
        return {
            "rawPages": [
                asdict(
                    RawJobPageInput(
                        source=args.source,
                        url=args.url,
                        rawHtml=None,
                        markdown=None,
                        rawJson=json.dumps(
                            {
                                "linkedinGuest": True,
                                "operatorExemption": "2026-10-23",
                                **guest_meta,
                            }
                        ),
                        crawledAt=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                    )
                )
            ],
            "jobs": [asdict(job) for job in guest_jobs],
            # A bounded single-page subset is not a full listing sweep, so
            # reconciliation must never mark unfetched postings missing.
            "isComplete": False,
            "stats": {
                "candidates": guest_meta["count"],
                "kept": len(guest_jobs),
            },
        }

    if parser == "linkedin":
        # LinkedIn MCP crawler (manual, authenticated) — unchanged.
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
    # A registry ``skipSitemap`` disables the sitemap probe. Some dynamic boards
    # (Student Job Search) publish a sitemap that contains no job-details and
    # whose probe URLs never match the render selector, so probing it only added
    # four timeout waits per crawl.
    discover_via_sitemap = (
        getattr(args, "use_sitemap", False)
        or strategy in ("sitemap-html", "dynamic")
        or parser == "jsonld"
    )
    if source_config.get("skipSitemap"):
        discover_via_sitemap = False
    if discover_via_sitemap:
        try:
            for job_url in discover_sitemap_job_urls(
                args.url,
                fetch,
                max_urls=int(getattr(args, "sitemap_max_urls", 60) or 60),
                args=args,
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
    # Some boards mix job links with navigation/solution links that happen to
    # carry a technology-looking label (Datacom's "Software engineering" service
    # page). A registry ``jobUrlContains`` narrows candidates to real postings.
    job_url_filter = str(source_config.get("jobUrlContains") or "").lower()

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
            if (
                same_site(args.url, job_url)
                and is_job_link(parser, title, job_url)
                and (not job_url_filter or job_url_filter in job_url.lower())
            ):
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
            if job_url_filter and job_url_filter not in job_url.lower():
                continue
            if job_url not in candidates:
                candidates[job_url] = fallback_title

    for job_url, fallback_title in sitemap_candidates.items():
        normalized = normalize_job_url(parser, job_url)
        if job_url_filter and job_url_filter not in normalized.lower():
            continue
        candidates.setdefault(normalized, fallback_title)

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
        # Candidates counts link-discovered plus inline JSON-LD adverts; kept is
        # the number that survived the IT/technology and content gates. The
        # orchestration logs "0 IT roles of N total" from this so an
        # empty-but-healthy source is visibly not a failure.
        "stats": {"candidates": len(candidates) + len(inline_jobs), "kept": len(jobs)},
    }


def _crawl_api_platform(
    args: argparse.Namespace,
    source_config: dict[str, Any],
    platform: str,
    api_fetch: Any,
) -> dict[str, Any]:
    """Fetch and normalise jobs straight from a public ATS JSON API."""
    # Public ATS APIs are still fetched over HTTP, so robots still applies. This
    # is why Air New Zealand moved off api.smartrecruiters.com (whose robots.txt
    # is "User-agent: * / Disallow: /") to the public board host.
    if getattr(args, "robots_txt_obey", False) and not obeys_robots(args.url, args.user_agent):
        raise RuntimeError(f"Robots.txt does not allow crawling {args.url}")

    # The registry URL (often the marketing site) is not necessarily the host the
    # ATS client requests (e.g. BambooHR/Workday). Gate every actual request URL
    # so a disallow on the API host is never missed.
    def robots_guarded_fetch(url: str, method: str = "GET", payload: dict[str, Any] | None = None) -> Any:
        if getattr(args, "robots_txt_obey", False) and not obeys_robots(url, args.user_agent):
            raise RuntimeError(f"Robots.txt does not allow crawling {url}")
        return api_fetch(url, method, payload)

    config = dict(source_config)
    raw_jobs = crawl_platform(platform, robots_guarded_fetch, source=args.source, config=config)
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
        "stats": {"candidates": len(raw_jobs), "kept": len(jobs)},
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
    "linkedin-guest",
    "seek",
    "trade-me",
    "pageup",
    "rss",
    "hays",
    "ashby",
    "xero",
    "sjs",
    "employment-hero",
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

#: Fetcher back-ends accepted on the CLI.
FETCHER_CHOICES = ("fetcher", "dynamic", "stealth", "http", "browser", "firecrawl")


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
    parser.add_argument(
        "--firecrawl-timeout",
        type=int,
        default=int(os.environ.get("FIRECRAWL_TIMEOUT_SECONDS") or FIRECRAWL_DEFAULT_TIMEOUT_SECONDS),
        help="seconds to wait for a Firecrawl scrape",
    )
    parser.add_argument("--browser-timeout", type=int, default=45)
    parser.add_argument("--render-wait-ms", type=int, default=8000)
    parser.add_argument("--robots-txt-obey", action="store_true")
    parser.add_argument("--proxy-url", default="")
    parser.add_argument("--user-agent", default=DEFAULT_HTTP_USER_AGENT)
    return parser.parse_args()


def attach_skills(result: dict[str, Any]) -> dict[str, Any]:
    """Attach the shared-taxonomy skills to every job in a crawl result.

    The taxonomy lives once in ``crawler/skills_taxonomy.json`` and is read by
    both this worker and the .NET ingestion pipeline. The .NET side prefers
    this array and only falls back to extracting itself when a crawler (for
    example Firecrawl) did not provide one.
    """
    extractor = default_extractor()
    for job in result.get("jobs", []):
        job["skills"] = extractor.extract_for_job(job)
    return result


def main() -> int:
    args = parse_args()

    try:
        print(json.dumps(attach_skills(crawl(args)), ensure_ascii=False))
        return 0
    except Exception as exc:  # pragma: no cover - defensive CLI boundary
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
