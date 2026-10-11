#!/usr/bin/env python3
"""Probe every source in sources/sites.json and detect the best crawl strategy.

For each site we fetch the listing URL (falling back to the homepage) and record:

* HTTP status, final URL after redirects
* whether the page ships Schema.org ``JobPosting`` JSON-LD (the most reliable,
  entirely public extraction surface)
* which applicant-tracking / job-board platform fingerprints appear
* whether a sitemap is discoverable (used for career pages that only expose
  jobs through XML sitemaps)
* whether robots.txt allows our user agent to fetch the listing URL

The tool then proposes a ``platform``/``parser``/``fetcher`` for the site and,
with ``--write``, patches ``sources/sites.json`` in place.

Usage::

    python3 crawler/tools/probe_sources.py --report /tmp/probe.json
    python3 crawler/tools/probe_sources.py --write --only "Xero,ASB"
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import re
import ssl
import sys
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import urljoin, urlparse
from urllib.robotparser import RobotFileParser

USER_AGENT = "Mozilla/5.0 (compatible; NZJobMarketDashboardBot/0.2; +https://example.invalid/bot)"
REGISTRY = Path(__file__).resolve().parents[1] / "sources" / "sites.json"
TIMEOUT = 25

# Ordered most-specific first: (platform, parser, needles-in-html-or-url)
PLATFORM_FINGERPRINTS: tuple[tuple[str, str, tuple[str, ...]], ...] = (
    ("ashby", "ashby", ("jobs.ashbyhq.com", "api.ashbyhq.com", "ashbyhq.com")),
    ("greenhouse", "greenhouse", ("boards.greenhouse.io", "job-boards.greenhouse.io", "greenhouse.io", "gh_jid")),
    ("lever", "lever", ("jobs.lever.co", "api.lever.co", "lever.co/")),
    ("workable", "workable", ("apply.workable.com", "workable.com")),
    ("smartrecruiters", "smartrecruiters", ("jobs.smartrecruiters.com", "api.smartrecruiters.com", "smartrecruiters.com")),
    ("jobadder", "jobadder", ("jobs.jobadder.com", "jobadder.com")),
    ("volcanic", "volcanic", ("volcanic.co.uk", "results-list", "ja_job_link", "job-title")),
    ("teamtailor", "teamtailor", ("teamtailor.com", "career.teamtailor")),
    ("recruitee", "recruitee", ("recruitee.com",)),
    ("personio", "personio", ("personio.com", "personio.de", "jobs.personio")),
    ("workday", "workday", ("myworkdayjobs.com", "workday.com/"),),
    ("taleo", "taleo", ("taleo.net",)),
    ("pageup", "pageup", ("pageuppeople.com", "pageup.com")),
    ("icims", "icims", ("icims.com",)),
    ("successfactors", "successfactors", ("successfactors.com", "sapsf.com", "sapsf.eu")),
    ("jobvite", "jobvite", ("jobvite.com",)),
    ("bamboohr", "bamboohr", ("bamboohr.com",)),
    ("snaphire", "snaphire", ("snaphire.com", "snaphire")),
    ("cornerstone", "cornerstone", ("cornerstoneondemand.com", "csod.com")),
    ("jazzhr", "jazzhr", ("jazzhr.com", "applytojob.com")),
    ("breezy", "breezy", ("breezy.hr",)),
    ("jobscore", "jobscore", ("jobscore.com",)),
)

# Platforms that expose a public, unauthenticated JSON postings API.
API_PLATFORMS = frozenset(
    {"ashby", "greenhouse", "lever", "workable", "smartrecruiters", "bamboohr", "workday", "recruitee"}
)

# Hand-written HTML parsers that the crawler already implements and tests; the
# probe must not replace them with a generic strategy.
CUSTOM_PARSERS = frozenset(
    {
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
    }
)

LD_JSON_RE = re.compile(
    r'<script[^>]+type=["\']application/ld\+json["\'][^>]*>(.*?)</script>', re.I | re.S
)
JOBPOSTING_RE = re.compile(r'"@type"\s*:\s*"?(?:\[[^\]]*)?JobPosting', re.I)
LOC_RE = re.compile(r"<loc>\s*([^<\s]+)\s*</loc>", re.I)
H1_RE = re.compile(r"<h1[^>]*>(.*?)</h1>", re.I | re.S)
TITLE_TAG_RE = re.compile(r"<title[^>]*>(.*?)</title>", re.I | re.S)

# Reuse the worker's precise job-URL predicate so the probe and the crawler agree
# on what counts as a job posting (sitemaps also contain blogs and marketing pages).
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "adapters" / "scrapling"))
from scrapling_worker import is_probable_job_url  # noqa: E402


def strip_tags(value: str) -> str:
    return re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", value)).strip()


def build_opener() -> urllib.request.OpenerDirector:
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    handler = urllib.request.HTTPSHandler(context=context)
    opener = urllib.request.build_opener(handler)
    opener.addheaders = [
        ("User-Agent", USER_AGENT),
        ("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"),
        ("Accept-Language", "en-NZ,en;q=0.9"),
    ]
    return opener


OPENER = build_opener()


def fetch(url: str) -> dict:
    try:
        with OPENER.open(url, timeout=TIMEOUT) as response:
            raw = response.read(3_000_000)
            charset = response.headers.get_content_charset() or "utf-8"
            return {
                "ok": True,
                "status": response.status,
                "finalUrl": response.geturl(),
                "body": raw.decode(charset, errors="replace"),
            }
    except urllib.error.HTTPError as exc:
        body = ""
        try:
            body = exc.read(200_000).decode("utf-8", errors="replace")
        except Exception:  # pragma: no cover - best effort body
            pass
        return {"ok": False, "status": exc.code, "finalUrl": url, "body": body, "error": str(exc)}
    except Exception as exc:  # noqa: BLE001 - probe is best-effort
        return {"ok": False, "status": None, "finalUrl": url, "body": "", "error": f"{type(exc).__name__}: {exc}"}


def robots_allows(url: str) -> bool | None:
    parsed = urlparse(url)
    robots_url = f"{parsed.scheme}://{parsed.netloc}/robots.txt"
    result = fetch(robots_url)
    if not result["ok"]:
        # No robots.txt (404) means everything is allowed.
        return True if result["status"] == 404 else None
    parser = RobotFileParser()
    parser.parse(result["body"].splitlines())
    try:
        return parser.can_fetch(USER_AGENT, url)
    except Exception:  # noqa: BLE001
        return None


def detect_platform(text: str, url: str) -> tuple[str | None, str | None]:
    haystack = f"{text[:400_000]}\n{url}".lower()
    for platform, parser, needles in PLATFORM_FINGERPRINTS:
        if any(needle in haystack for needle in needles):
            return platform, parser
    return None, None


def sitemap_available(homepage: str) -> bool:
    for candidate in ("sitemap.xml", "sitemap_index.xml", "sitemap-index.xml"):
        result = fetch(urljoin(homepage, "/" + candidate))
        if result["ok"] and "<" in result["body"][:2000]:
            return True
    return False


def sitemap_job_urls(homepage: str, limit_sitemaps: int = 6) -> list[str]:
    """Follow sitemap indexes and collect URLs that look like job postings."""
    roots = ("sitemap.xml", "sitemap_index.xml", "sitemap-index.xml", "sitemap/sitemap.xml")
    queue = [urljoin(homepage, "/" + root) for root in roots]
    seen: set[str] = set()
    job_urls: list[str] = []
    while queue and len(seen) < limit_sitemaps:
        candidate = queue.pop(0)
        if candidate in seen:
            continue
        seen.add(candidate)
        result = fetch(candidate)
        if not result["ok"] or "<" not in result["body"][:1000]:
            continue
        for loc in LOC_RE.findall(result["body"]):
            lowered = loc.lower()
            if lowered.endswith(".xml") or "sitemap" in lowered:
                if loc not in seen:
                    queue.append(loc)
            elif is_probable_job_url(loc):
                job_urls.append(loc)
        if len(job_urls) >= 40:
            break
    # De-duplicate, keep order.
    return list(dict.fromkeys(job_urls))[:40]


def listing_detail_links(html: str, base_url: str, limit: int = 4) -> list[str]:
    links: list[str] = []
    base_host = urlparse(base_url).netloc.lower()
    for href in re.findall(r'href=["\']([^"\'#]+)["\']', html, re.I):
        absolute = urljoin(base_url, href)
        if urlparse(absolute).netloc.lower() != base_host:
            continue
        if is_probable_job_url(absolute) and absolute not in links:
            links.append(absolute)
        if len(links) >= limit:
            break
    return links


def probe_detail(url: str) -> dict:
    result = fetch(url)
    body = result.get("body", "")
    ld_blocks = LD_JSON_RE.findall(body)
    h1 = H1_RE.search(body)
    title_tag = TITLE_TAG_RE.search(body)
    return {
        "url": url,
        "status": result.get("status"),
        "ok": bool(result.get("ok")),
        "bytes": len(body),
        "jobPostingJsonLd": bool(ld_blocks) and any(JOBPOSTING_RE.search(block) for block in ld_blocks),
        "h1": strip_tags(h1.group(1))[:120] if h1 else None,
        "titleTag": strip_tags(title_tag.group(1))[:120] if title_tag else None,
    }


def probe_site(site: dict, deep: bool = False) -> dict:
    listing_url = site.get("jobs_url") or site["homepage"]
    result = fetch(listing_url)
    used_url = listing_url
    if not result["ok"] or result["status"] >= 400 or len(result["body"]) < 500:
        fallback = fetch(site["homepage"])
        if fallback["ok"] and (not result["ok"] or len(fallback["body"]) > len(result["body"])):
            result, used_url = fallback, site["homepage"]

    body = result.get("body", "")
    final_url = result.get("finalUrl", used_url)
    ld_blocks = LD_JSON_RE.findall(body)
    has_jobposting = bool(ld_blocks) and any(JOBPOSTING_RE.search(block) for block in ld_blocks)
    platform, parser = detect_platform(body, final_url)

    is_custom = site.get("parser") in CUSTOM_PARSERS
    if is_custom:  # bespoke HTML parser always wins
        chosen_parser = site["parser"]
        chosen_platform = site.get("platform") or platform or "custom"
    elif platform in API_PLATFORMS:
        chosen_platform, chosen_parser = platform, platform
    elif platform:
        chosen_platform, chosen_parser = platform, parser
    elif has_jobposting:
        chosen_platform, chosen_parser = "jsonld", "jsonld"
    else:
        chosen_platform, chosen_parser = None, "generic"

    finding = {
        "name": site["name"],
        "probingUrl": used_url,
        "finalUrl": final_url,
        "status": result.get("status"),
        "ok": bool(result.get("ok")),
        "error": result.get("error"),
        "bytes": len(body),
        "jobPostingJsonLd": has_jobposting,
        "jsonLdBlocks": len(ld_blocks),
        "sitemap": sitemap_available(site["homepage"]),
        "robotsAllows": robots_allows(used_url),
        "detectedPlatform": platform,
        "proposedPlatform": chosen_platform,
        "proposedParser": chosen_parser,
    }

    if deep:
        sitemap_urls = sitemap_job_urls(site["homepage"])
        sample_urls = sitemap_urls[:3] or listing_detail_links(body, final_url, 3)
        details = [probe_detail(url) for url in sample_urls]
        finding["sitemapJobUrlCount"] = len(sitemap_urls)
        finding["sitemapJobUrlSample"] = sitemap_urls[:5]
        finding["detailSamples"] = details
        jsonld_details = sum(1 for detail in details if detail["jobPostingJsonLd"])
        finding["detailJsonLdCount"] = jsonld_details
        if is_custom:
            finding["strategy"] = "custom"
        elif chosen_platform in API_PLATFORMS:
            finding["strategy"] = "api"
        elif jsonld_details:
            finding["strategy"] = "jsonld"
            finding["proposedPlatform"] = "jsonld"
            finding["proposedParser"] = "jsonld"
        elif sitemap_urls:
            finding["strategy"] = "sitemap-html"
        elif finding["ok"]:
            finding["strategy"] = "generic"
        else:
            finding["strategy"] = "blocked"
    return finding


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--report", default="/tmp/nz_source_probe.json")
    ap.add_argument("--write", action="store_true", help="patch sources/sites.json with proposals")
    ap.add_argument("--deep", action="store_true", help="also probe sitemaps and sample detail pages")
    ap.add_argument("--only", default="", help="comma separated site names substring filter")
    ap.add_argument("--workers", type=int, default=8)
    args = ap.parse_args()

    registry = json.loads(REGISTRY.read_text())
    sites = registry["sites"]
    if args.only:
        wanted = [value.strip().lower() for value in args.only.split(",") if value.strip()]
        sites = [s for s in sites if any(w in s["name"].lower() for w in wanted)]

    findings: list[dict] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(probe_site, site, args.deep): site for site in sites}
        for future in concurrent.futures.as_completed(futures):
            site = futures[future]
            try:
                finding = future.result()
            except Exception as exc:  # noqa: BLE001
                finding = {"name": site["name"], "ok": False, "error": str(exc)}
            findings.append(finding)
            flag = "OK " if finding.get("ok") else "ERR"
            print(
                f"{flag} {finding['name']:<24} status={finding.get('status')} "
                f"jsonld={finding.get('jobPostingJsonLd')} platform={finding.get('detectedPlatform')} "
                f"-> {finding.get('proposedParser')} strategy={finding.get('strategy')}",
                file=sys.stderr,
            )

    findings.sort(key=lambda item: item["name"])
    Path(args.report).write_text(json.dumps(findings, indent=2))

    summary = {
        "total": len(findings),
        "reachable": sum(1 for f in findings if f.get("ok")),
        "jobPostingJsonLd": sum(1 for f in findings if f.get("jobPostingJsonLd")),
    }
    print(json.dumps(summary, indent=2))

    if args.write:
        by_name = {f["name"]: f for f in findings}
        for site in registry["sites"]:
            finding = by_name.get(site["name"])
            if not finding:
                continue
            site["platform"] = finding["proposedPlatform"]
            site["parser"] = finding["proposedParser"]
            if finding.get("strategy"):
                site["strategy"] = finding["strategy"]
        REGISTRY.write_text(json.dumps(registry, indent=2) + "\n")
        print(f"updated {REGISTRY}", file=sys.stderr)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
