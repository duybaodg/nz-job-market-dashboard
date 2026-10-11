#!/usr/bin/env python3
"""Discover the real job-board endpoint behind each site's careers landing page.

Careers landing pages are usually marketing pages; the actual openings live on a
job-board URL (``/jobs``, ``/vacancies`` …) or on a hosted ATS platform
(Workday, PageUp, Taleo, Greenhouse, …).  This tool scans the landing page, its
links, iframes and embedded JSON for those endpoints and, with ``--write``,
updates ``jobs_url``/``platform``/``strategy`` in the registry.

Usage::

    python3 crawler/tools/discover_boards.py --report /tmp/boards.json
    python3 crawler/tools/discover_boards.py --write --only "Xero,ANZ"
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import re
import sys
import urllib.parse as urlparse
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from probe_sources import fetch, strip_tags  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]
REGISTRY = REPO_ROOT / "crawler" / "sources" / "sites.json"

# Hosted ATS platforms: (platform, parser, regex capturing the board URL/tenant).
ATS_PATTERNS: tuple[tuple[str, str, str], ...] = (
    ("workday", "workday", r"https?://([a-z0-9-]+\.wd\d+\.myworkdayjobs\.com(?:/[A-Za-z0-9_-]+)?)"),
    ("pageup", "generic", r"https?://([a-z0-9-]+\.pageuppeople\.com[^\"'<>\\ ]*)"),
    ("taleo", "generic", r"https?://([a-z0-9-]+\.taleo\.net[^\"'<>\\ ]*)"),
    ("successfactors", "generic", r"https?://([a-z0-9-]+\.successfactors\.(?:com|eu)[^\"'<>\\ ]*)"),
    ("icims", "generic", r"https?://([a-z0-9-]+\.icims\.com[^\"'<>\\ ]*)"),
    ("greenhouse", "greenhouse", r"https?://(?:boards|job-boards)\.greenhouse\.io/([a-z0-9_-]+)"),
    ("lever", "lever", r"https?://jobs\.lever\.co/([a-z0-9_-]+)"),
    ("ashby", "ashby", r"https?://jobs\.ashbyhq\.com/([a-z0-9_-]+)"),
    ("workable", "workable", r"https?://apply\.workable\.com/([a-z0-9_-]+)"),
    ("smartrecruiters", "smartrecruiters", r"https?://(?:jobs|api)\.smartrecruiters\.com/(?:v1/companies/)?([a-z0-9_-]+)"),
    ("bamboohr", "bamboohr", r"https?://([a-z0-9-]+)\.bamboohr\.com"),
    ("jobadder", "jobadder", r"https?://([a-z0-9-]+)\.jobadder\.com[^\"'<>\\ ]*"),
    ("teamtailor", "generic", r"https?://([a-z0-9-]+\.teamtailor\.com[^\"'<>\\ ]*)"),
    ("recruitee", "generic", r"https?://([a-z0-9-]+\.recruitee\.com[^\"'<>\\ ]*)"),
    ("elmo", "generic", r"https?://([a-z0-9-]+\.elmo(?:hr|cloud)\.com[^\"'<>\\ ]*)"),
    ("snaphire", "generic", r"https?://([a-z0-9-]+\.snaphire\.com[^\"'<>\\ ]*)"),
    ("cornerstone", "generic", r"https?://([a-z0-9-]+\.(?:csod|cornerstoneondemand)\.com[^\"'<>\\ ]*)"),
    ("oracle", "generic", r"https?://([a-z0-9-]+\.oraclecloud\.com/hcmUI/CandidateExperience[^\"'<>\\ ]*)"),
    ("eightfold", "eightfold", r"https?://([a-z0-9-]+\.eightfold\.ai/careers[^\"'<>\\ ]*)"),
    ("jobvite", "generic", r"https?://([a-z0-9-]+\.jobvite\.com[^\"'<>\\ ]*)"),
)

# Same-site paths that usually hold the real listing.
JOB_PATH_PATTERNS = (
    "/jobs", "/job", "/vacancies", "/vacancy", "/careers/jobs", "/careers/vacancies",
    "/job-search", "/search-jobs", "/current-vacancies", "/opportunities",
    "/work-with-us", "/join-us", "/careers/current", "/about/careers", "/career",
)
JOB_LINK_HINT = re.compile(r"/(?:jobs?|vacanc\w*|positions?|openings?|opportunit\w*|careers?)", re.I)
HREF_RE = re.compile(r'href=["\']([^"\'>]+)["\']', re.I)
IFRAME_RE = re.compile(r'<iframe[^>]+src=["\']([^"\'>]+)["\']', re.I)
NEXT_DATA_RE = re.compile(r'__NEXT_DATA__[^>]*>(\{.*?\})</script>', re.I | re.S)
SCRIPT_SRC_RE = re.compile(r'<script[^>]+src=["\']([^"\'>]+)["\']', re.I)


def scan_scripts(body: str, base_url: str, max_scripts: int = 8) -> dict:
    """Fetch same-host JS bundles and look for ATS hosts inside them."""
    host = urlparse.urlparse(base_url).netloc.lower()
    sources: list[str] = []
    for src in SCRIPT_SRC_RE.findall(body):
        absolute = urlparse.urljoin(base_url, src)
        if urlparse.urlparse(absolute).netloc.lower() == host and absolute not in sources:
            sources.append(absolute)
        if len(sources) >= max_scripts:
            break

    ats_hits: list[dict] = []
    for url in sources:
        result = fetch(url)
        if not result.get("ok"):
            continue
        bundle = result.get("body", "")[:4_000_000]
        for platform, parser, pattern in ATS_PATTERNS:
            for match in re.finditer(pattern, bundle, re.I):
                ats_hits.append(
                    {
                        "platform": platform,
                        "parser": parser,
                        "url": match.group(0).split("\\")[0],
                        "foundOn": url,
                    }
                )
    deduped: dict[str, dict] = {}
    for hit in ats_hits:
        current = deduped.get(hit["platform"])
        if not current or len(hit["url"]) > len(current["url"]):
            deduped[hit["platform"]] = hit
    return {"scriptsScanned": len(sources), "ats": list(deduped.values())}


def scan_body(body: str, base_url: str) -> dict:
    lower = body.lower()
    ats_hits: list[dict] = []
    for platform, parser, pattern in ATS_PATTERNS:
        for match in re.finditer(pattern, body, re.I):
            url = match.group(0)
            ats_hits.append({"platform": platform, "parser": parser, "url": url.split("\\")[0]})
    # De-duplicate ATS hits, preferring the longest (most specific) URL per platform.
    deduped: dict[str, dict] = {}
    for hit in ats_hits:
        current = deduped.get(hit["platform"])
        if not current or len(hit["url"]) > len(current["url"]):
            deduped[hit["platform"]] = hit

    same_host = urlparse.urlparse(base_url).netloc.lower()
    job_paths: list[str] = []
    for href in HREF_RE.findall(body) + IFRAME_RE.findall(body):
        absolute = urlparse.urljoin(base_url, href)
        parsed = urlparse.urlparse(absolute)
        if parsed.netloc.lower() != same_host:
            continue
        path = parsed.path.rstrip("/").lower()
        if not path or absolute in job_paths:
            continue
        if any(path.endswith(pattern) or pattern in path for pattern in JOB_PATH_PATTERNS) or JOB_LINK_HINT.search(path):
            job_paths.append(absolute)

    return {
        "ats": list(deduped.values()),
        "jobPaths": job_paths[:12],
        "hasNextData": "__next_data__" in lower,
        "hasNuxt": "__nuxt__" in lower or "window.__nuxt" in lower,
        "hasJsonLd": "application/ld+json" in lower,
        "bytes": len(body),
    }


BAD_PATH_RE = re.compile(r"(#|/job-alerts|/search\?|/jobs/\d+|/\d{4,}|/veterans|/privacy)", re.I)


def is_listing_path(url: str) -> bool:
    """Reject detail pages, alerts sign-ups and fragment-only links."""
    if BAD_PATH_RE.search(url):
        return False
    path = urlparse.urlparse(url).path.rstrip("/")
    if not path:
        return False
    return not path.rsplit("/", 1)[-1].isdigit()


def board_url_for(platform: str, raw: str) -> str | None:
    """Turn an ATS fingerprint into a canonical, crawlable board URL."""
    if platform == "workday":
        host = raw.split("//", 1)[-1].split("/")[0]
        return f"https://{host}"
    if platform == "greenhouse":
        token = raw.rstrip("/").split("/")[-1]
        return f"https://boards-api.greenhouse.io/v1/boards/{token}/jobs?content=true"
    if platform == "lever":
        token = raw.rstrip("/").split("/")[-1]
        return f"https://api.lever.co/v0/postings/{token}?mode=json"
    if platform == "ashby":
        token = raw.rstrip("/").split("/")[-1]
        return f"https://api.ashbyhq.com/posting-api/job-board/{token}"
    if platform == "workable":
        token = raw.rstrip("/").split("/")[-1]
        return f"https://apply.workable.com/api/v1/widget/accounts/{token}?details=true"
    if platform == "bamboohr":
        return raw if raw.endswith("/careers/list") else raw.rstrip("/") + "/careers/list"
    return raw


def investigate(site: dict, deep: bool = True, scan_js: bool = False) -> dict:
    candidates: list[str] = []
    for url in (site.get("jobs_url"), site.get("homepage")):
        if url and url not in candidates:
            candidates.append(url)

    scans: list[dict] = []
    for url in candidates:
        result = fetch(url)
        if not result.get("ok") or len(result.get("body", "")) < 200:
            continue
        scan = scan_body(result["body"], result.get("finalUrl") or url)
        scan["url"] = url
        scan["finalUrl"] = result.get("finalUrl")
        scans.append(scan)
        if scan_js:
            js = scan_scripts(result["body"], result.get("finalUrl") or url)
            scan["ats"].extend(js["ats"])
            scan["scriptsScanned"] = js["scriptsScanned"]
        # Follow the strongest same-site job path once to find the board/API.
        if deep:
            for path in scan["jobPaths"][:2]:
                if any(path == existing.get("url") for existing in scans):
                    continue
                follow = fetch(path)
                if follow.get("ok") and len(follow.get("body", "")) > 200:
                    deeper = scan_body(follow["body"], follow.get("finalUrl") or path)
                    deeper["url"] = path
                    if scan_js:
                        js = scan_scripts(follow["body"], follow.get("finalUrl") or path)
                        deeper["ats"].extend(js["ats"])
                    scans.append(deeper)

    ats: dict[str, dict] = {}
    for scan in scans:
        for hit in scan["ats"]:
            board = board_url_for(hit["platform"], hit["url"])
            if board and hit["platform"] not in ats:
                ats[hit["platform"]] = {**hit, "boardUrl": board, "foundOn": scan["url"]}

    best_job_path = None
    for scan in scans:
        for path in scan["jobPaths"]:
            if not is_listing_path(path):
                continue
            if not best_job_path or path.count("/") < best_job_path.count("/"):
                best_job_path = path

    return {
        "name": site["name"],
        "homepage": site["homepage"],
        "currentJobsUrl": site.get("jobs_url"),
        "ats": ats,
        "bestJobPath": best_job_path,
        "scans": [
            {
                "url": scan["url"],
                "status": scan.get("status"),
                "bytes": scan["bytes"],
                "jobPaths": scan["jobPaths"][:6],
                "hasNextData": scan["hasNextData"],
                "hasNuxt": scan["hasNuxt"],
            }
            for scan in scans
        ],
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--report", default="/tmp/nz_boards.json")
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--only", default="")
    ap.add_argument("--strategies", default="dynamic", help="comma separated strategies to investigate")
    ap.add_argument("--scan-js", action="store_true", help="also scan same-host JS bundles for ATS hosts")
    ap.add_argument("--workers", type=int, default=8)
    args = ap.parse_args()

    registry = json.loads(REGISTRY.read_text())
    wanted = [value.strip().lower() for value in args.only.split(",") if value.strip()]
    strategies = {value.strip() for value in args.strategies.split(",") if value.strip()}
    sites = [
        site
        for site in registry["sites"]
        if (not strategies or site.get("strategy") in strategies)
        and (not wanted or any(value in site["name"].lower() for value in wanted))
    ]

    findings: list[dict] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(investigate, site, True, args.scan_js): site for site in sites}
        for future in concurrent.futures.as_completed(futures):
            site = futures[future]
            try:
                finding = future.result()
            except Exception as exc:  # noqa: BLE001
                finding = {"name": site["name"], "error": str(exc), "ats": {}, "bestJobPath": None}
            findings.append(finding)
            ats = ",".join(finding.get("ats", {})) or "-"
            print(f"{finding['name']:<26} ats={ats:<24} jobPath={finding.get('bestJobPath')}", file=sys.stderr)

    findings.sort(key=lambda item: item["name"])
    Path(args.report).write_text(json.dumps(findings, indent=2))
    print(json.dumps({"investigated": len(findings), "withAts": sum(1 for f in findings if f.get("ats"))}, indent=2))

    if args.write:
        by_name = {f["name"]: f for f in findings}
        for site in registry["sites"]:
            finding = by_name.get(site["name"])
            if not finding:
                continue
            ats = finding.get("ats") or {}
            if ats:
                platform, hit = next(iter(ats.items()))
                site["platform"] = platform
                site["parser"] = hit["parser"] if hit["parser"] in API_PLATFORMS else hit["parser"]
                site["strategy"] = "api" if hit["parser"] in API_PLATFORMS else "dynamic"
                site["jobs_url"] = hit["boardUrl"]
                site["discoveredFrom"] = hit["foundOn"]
            elif finding.get("bestJobPath") and not site.get("jobs_url", "").endswith("/jobs"):
                site["jobs_url"] = finding["bestJobPath"]
        REGISTRY.write_text(json.dumps(registry, indent=2) + "\n")
        print(f"updated {REGISTRY}", file=sys.stderr)
    return 0


API_PLATFORMS = {"ashby", "greenhouse", "lever", "workable", "smartrecruiters", "bamboohr", "workday", "recruitee"}

if __name__ == "__main__":
    raise SystemExit(main())
