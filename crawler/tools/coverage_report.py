#!/usr/bin/env python3
"""Measure per-source crawl coverage by running the real worker against each site.

Each source is crawled in its own subprocess with a hard timeout, so one slow or
hostile site cannot stall the report.  Results tell us which of the "other sites"
already yield jobs with the static fetcher and which genuinely need a browser or
a bespoke parser.

Usage::

    python3 crawler/tools/coverage_report.py --report /tmp/coverage.json
    python3 crawler/tools/coverage_report.py --only "Xero,ANZ" --max-pages 3
    python3 crawler/tools/coverage_report.py --include-last   # include SEEK/Trade Me
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
REGISTRY = REPO_ROOT / "crawler" / "sources" / "sites.json"
WORKER = REPO_ROOT / "crawler" / "adapters" / "scrapling" / "scrapling_worker.py"


def run_source(
    site: dict,
    max_pages: int,
    delay: float,
    timeout: int,
    robots: bool,
    fetcher_override: str = "",
    browser_timeout: int = 45,
    render_wait_ms: int = 8000,
) -> dict:
    url = site.get("jobs_url") or site.get("homepage")
    command = [
        sys.executable,
        str(WORKER),
        "--source",
        site["name"],
        "--url",
        url,
        "--parser",
        site.get("parser") or "generic",
        "--fetcher-type",
        "http",
        "--max-pages",
        str(max_pages),
        "--delay-seconds",
        str(delay),
        "--fetch-timeout",
        "12",
    ]
    if robots:
        command.append("--robots-txt-obey")
    if fetcher_override:
        command.extend(["--fetcher-override", fetcher_override])
    command.extend(["--browser-timeout", str(browser_timeout), "--render-wait-ms", str(render_wait_ms)])

    started = time.time()
    base = {
        "name": site["name"],
        "strategy": site.get("strategy"),
        "parser": site.get("parser"),
        "url": url,
    }
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return {**base, "status": "timeout", "jobs": 0, "elapsed": round(time.time() - started, 1)}

    elapsed = round(time.time() - started, 1)
    if completed.returncode != 0:
        error = (completed.stderr or "").strip().splitlines()
        return {
            **base,
            "status": "error",
            "jobs": 0,
            "elapsed": elapsed,
            "error": (error[-1] if error else "unknown failure")[:180],
        }

    # Split on newlines only: str.splitlines() also breaks on Unicode line
    # separators (U+2028) that legitimately appear inside job descriptions.
    payload_line = next(
        (line for line in reversed(completed.stdout.split("\n")) if line.startswith("{")), None
    )
    if not payload_line:
        return {**base, "status": "badjson", "jobs": 0, "elapsed": elapsed, "error": "no JSON output"}
    try:
        payload = json.loads(payload_line)
    except json.JSONDecodeError as exc:
        return {**base, "status": "badjson", "jobs": 0, "elapsed": elapsed, "error": str(exc)[:150]}

    jobs = payload.get("jobs", [])
    return {
        **base,
        "status": "ok",
        "jobs": len(jobs),
        "pages": len(payload.get("rawPages", [])),
        "elapsed": elapsed,
        "sample": [job.get("title", "")[:60] for job in jobs[:3]],
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--report", default="/tmp/nz_coverage.json")
    ap.add_argument("--only", default="")
    ap.add_argument("--include-last", action="store_true", help="include deprioritised sources (SEEK/Trade Me)")
    ap.add_argument("--strategies", default="", help="comma separated strategy filter")
    ap.add_argument("--max-pages", type=int, default=5)
    ap.add_argument("--delay", type=float, default=0.2)
    ap.add_argument("--timeout", type=int, default=90)
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--no-robots", action="store_true")
    ap.add_argument("--fetcher-override", default="", help="force a fetcher, e.g. browser")
    ap.add_argument("--browser-timeout", type=int, default=45)
    ap.add_argument("--render-wait-ms", type=int, default=8000)
    args = ap.parse_args()

    registry = json.loads(REGISTRY.read_text())
    wanted = [value.strip().lower() for value in args.only.split(",") if value.strip()]
    strategies = {value.strip() for value in args.strategies.split(",") if value.strip()}

    sites = []
    for site in registry["sites"]:
        if not site.get("enabled"):
            continue
        if site.get("priority") == "last" and not args.include_last:
            continue
        if strategies and site.get("strategy") not in strategies:
            continue
        if wanted and not any(value in site["name"].lower() for value in wanted):
            continue
        sites.append(site)

    results: list[dict] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {
            pool.submit(
                run_source,
                site,
                args.max_pages,
                args.delay,
                args.timeout,
                not args.no_robots,
                args.fetcher_override,
                args.browser_timeout,
                args.render_wait_ms,
            ): site
            for site in sites
        }
        for future in concurrent.futures.as_completed(futures):
            result = future.result()
            results.append(result)
            marker = "OK " if result.get("jobs") else "-- "
            print(
                f"{marker}{result['name']:<26} jobs={result['jobs']:<4} pages={result.get('pages', 0):<3} "
                f"{result['status']:<8} {round(result.get('elapsed', 0), 1)}s {str(result.get('error', ''))[:60]}",
                file=sys.stderr,
            )

    results.sort(key=lambda item: (-item["jobs"], item["name"]))
    Path(args.report).write_text(json.dumps(results, indent=2))

    working = [item for item in results if item["jobs"]]
    failing = [item for item in results if not item["jobs"]]
    print(
        json.dumps(
            {
                "sources": len(results),
                "withJobs": len(working),
                "totalJobs": sum(item["jobs"] for item in results),
                "failing": len(failing),
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
