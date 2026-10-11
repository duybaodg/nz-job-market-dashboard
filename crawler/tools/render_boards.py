#!/usr/bin/env python3
"""Browser-render each source and discover its real job board / ATS host.

Static HTML hides the job board on JavaScript-only career sites.  Rendering the
page with a local Chromium binary reveals the ATS host (Eightfold, Workday,
PageUp, Taleo, …) or the same-host job links, which we can then turn into a
crawlable source.

Usage::

    python3 crawler/tools/render_boards.py --report /tmp/render_boards.json
    python3 crawler/tools/render_boards.py --only "ANZ,Spark" --write
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import re
import sys
import urllib.parse as urlparse
from argparse import Namespace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from discover_boards import ATS_PATTERNS, board_url_for, is_listing_path  # noqa: E402
from probe_sources import fetch  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "crawler" / "adapters" / "scrapling"))
from scrapling_worker import DEFAULT_HTTP_USER_AGENT, fetch_with_browser  # noqa: E402

REGISTRY = REPO_ROOT / "crawler" / "sources" / "sites.json"
JOB_LINK_RE = re.compile(r'href=["\']([^"\'>]+)["\']', re.I)
JOB_HINT_RE = re.compile(r"/(?:jobs?|vacanc\w*|positions?|openings?|careers?/job)", re.I)


def render(url: str) -> str:
    args = Namespace(user_agent=DEFAULT_HTTP_USER_AGENT, browser_timeout=40, render_wait_ms=10000)
    html, _ = fetch_with_browser(args, url)
    return html


def scan_rendered(html: str, base_url: str) -> dict:
    ats: dict[str, dict] = {}
    for platform, parser, pattern in ATS_PATTERNS:
        for match in re.finditer(pattern, html, re.I):
            raw = match.group(0).split("\\")[0]
            board = board_url_for(platform, raw)
            if board and platform not in ats:
                ats[platform] = {"platform": platform, "parser": parser, "found": raw, "boardUrl": board}

    parsed = urlparse.urlparse(base_url)
    job_paths: list[str] = []
    for href in JOB_LINK_RE.findall(html):
        absolute = urlparse.urljoin(base_url, href)
        if urlparse.urlparse(absolute).netloc.lower() != parsed.netloc.lower():
            continue
        if JOB_HINT_RE.search(urlparse.urlparse(absolute).path) and absolute not in job_paths:
            job_paths.append(absolute)
    return {"ats": ats, "jobPaths": job_paths[:12]}


def investigate(site: dict) -> dict:
    url = site.get("jobs_url") or site["homepage"]
    try:
        html = render(url)
    except Exception as exc:  # noqa: BLE001
        return {"name": site["name"], "url": url, "error": str(exc)[:160], "ats": {}, "jobPaths": []}
    scan = scan_rendered(html, url)
    return {"name": site["name"], "url": url, "bytes": len(html), **scan}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--report", default="/tmp/render_boards.json")
    ap.add_argument("--only", default="")
    ap.add_argument("--strategies", default="dynamic")
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--workers", type=int, default=3)
    args = ap.parse_args()

    registry = json.loads(REGISTRY.read_text())
    wanted = [value.strip().lower() for value in args.only.split(",") if value.strip()]
    strategies = {value.strip() for value in args.strategies.split(",") if value.strip()}
    sites = [
        site
        for site in registry["sites"]
        if site.get("enabled")
        and (not strategies or site.get("strategy") in strategies)
        and (not wanted or any(value in site["name"].lower() for value in wanted))
    ]

    findings: list[dict] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(investigate, site): site for site in sites}
        for future in concurrent.futures.as_completed(futures):
            finding = future.result()
            findings.append(finding)
            ats = ",".join(finding.get("ats", {})) or "-"
            print(
                f"{finding['name']:<26} ats={ats:<28} jobPaths={len(finding.get('jobPaths', []))} "
                f"{finding.get('error', '')[:50]}",
                file=sys.stderr,
            )

    findings.sort(key=lambda item: item["name"])
    Path(args.report).write_text(json.dumps(findings, indent=2))
    print(json.dumps({"rendered": len(findings), "withAts": sum(1 for f in findings if f.get("ats"))}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
