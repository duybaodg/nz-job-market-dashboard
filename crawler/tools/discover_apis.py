#!/usr/bin/env python3
"""Discover JSON job APIs behind JavaScript-only career sites.

Sites that cannot be read from static HTML often call a JSON endpoint that we
*can* read without a browser.  For every source this tool:

1. fetches the listing page and its same-host JS bundles,
2. extracts candidate API endpoints (``/api/...``, ``.json``, ``graphql``),
3. probes each candidate plus a list of well-known job endpoints,
4. reports any JSON payload that looks like a list of jobs.

Usage::

    python3 crawler/tools/discover_apis.py --report /tmp/apis.json
    python3 crawler/tools/discover_apis.py --only "Tribe,Beyond" --write
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
from probe_sources import fetch  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]
REGISTRY = REPO_ROOT / "crawler" / "sources" / "sites.json"

STANDARD_PATHS = (
    "/api/jobs",
    "/api/jobs/search",
    "/api/v1/jobs",
    "/api/v1/jobs/search",
    "/api/careers",
    "/api/vacancies",
    "/api/positions",
    "/api/opportunities",
    "/jobs.json",
    "/careers.json",
    "/api/jobs.json",
    "/wp-json/wp/v2/job_listing?per_page=20",
    "/wp-json/wp-job-manager/v1/jobs",
    "/jobs/feed/",
    "/?feed=job_feed",
    "/job-feed/",
)

API_HINT_RE = re.compile(r"[\"'](/(?:api|graphql|wp-json)[a-zA-Z0-9/_\-.?=&]*)", re.I)
JSON_HINT_RE = re.compile(r"[\"']([a-zA-Z0-9/_\-.]+\.json(?:\?[^\"']*)?)", re.I)
SCRIPT_SRC_RE = re.compile(r'<script[^>]+src=["\']([^"\'>]+)["\']', re.I)

JOB_TITLE_KEYS = ("title", "name", "jobtitle", "position", "role")
JOB_CONTEXT_KEYS = ("location", "company", "description", "url", "link", "jobid", "city", "brand", "postedat")


def candidate_endpoints(body: str, base_url: str, max_candidates: int = 14) -> list[str]:
    parsed = urlparse.urlparse(base_url)
    origin = f"{parsed.scheme}://{parsed.netloc}"
    found: list[str] = []

    for raw in list(API_HINT_RE.findall(body)) + list(JSON_HINT_RE.findall(body)):
        cleaned = raw.split("\\")[0].rstrip(".,\"'")
        if len(cleaned) < 4 or cleaned.startswith("//"):
            continue
        absolute = cleaned if cleaned.startswith("http") else origin + ("" if cleaned.startswith("/") else "/") + cleaned
        if absolute not in found:
            found.append(absolute)

    # Also look inside same-host JS bundles for API paths.
    for src in SCRIPT_SRC_RE.findall(body)[:6]:
        absolute = urlparse.urljoin(base_url, src)
        if urlparse.urlparse(absolute).netloc != parsed.netloc:
            continue
        result = fetch(absolute)
        if not result.get("ok"):
            continue
        bundle = result.get("body", "")[:3_000_000]
        for raw in list(API_HINT_RE.findall(bundle)) + list(JSON_HINT_RE.findall(bundle)):
            cleaned = raw.split("\\")[0].rstrip(".,\"'")
            if len(cleaned) < 4:
                continue
            candidate = origin + ("" if cleaned.startswith("/") else "/") + cleaned
            if candidate not in found:
                found.append(candidate)
        if len(found) >= max_candidates:
            break

    return found[:max_candidates]


def classify_payload(payload: object) -> dict | None:
    """Return a summary when the payload looks like a job collection."""
    if isinstance(payload, list):
        items = payload
        container = None
    elif isinstance(payload, dict):
        container = next(
            (
                key
                for key, value in payload.items()
                if isinstance(value, list) and value and all(isinstance(item, dict) for item in value[:5])
            ),
            None,
        )
        if container is None:
            return None
        items = payload[container]
    else:
        return None

    if not items or not isinstance(items[0], dict):
        return None
    keys = {key.lower() for item in items[:5] for key in item}
    has_title = any(any(candidate in keys for candidate in (t, t.replace("_", "")) ) for t in JOB_TITLE_KEYS)
    if not has_title:
        # Some APIs nest the role title.
        has_title = any(
            isinstance(item.get("job"), dict) and any(t in {k.lower() for k in item["job"]} for t in JOB_TITLE_KEYS)
            for item in items[:5]
        )
    context_hits = sum(1 for key in JOB_CONTEXT_KEYS if key in keys)
    if not (has_title and context_hits >= 1):
        return None
    return {
        "container": container,
        "count": len(items),
        "keys": sorted(keys)[:14],
        "sampleTitle": str(
            items[0].get("title") or items[0].get("name") or (items[0].get("job") or {}).get("title") or ""
        )[:80],
    }


def probe_url(url: str) -> dict | None:
    result = fetch(url)
    if not result.get("ok"):
        return None
    body = result.get("body", "")
    if body[:1] not in "[{":
        return None
    try:
        payload = json.loads(body)
    except json.JSONDecodeError:
        return None
    summary = classify_payload(payload)
    return {**summary, "url": url} if summary else None


def investigate(site: dict) -> dict:
    base_url = site.get("jobs_url") or site["homepage"]
    result = fetch(base_url)
    body = result.get("body", "")
    candidates = candidate_endpoints(body, base_url) if body else []
    endpoints = list(dict.fromkeys(candidates + [
        urlparse.urljoin(base_url, path) for path in STANDARD_PATHS
    ]))

    hits = [hit for hit in (probe_url(url) for url in endpoints) if hit]
    hits.sort(key=lambda item: -item["count"])
    return {"name": site["name"], "baseUrl": base_url, "candidates": candidates[:8], "hits": hits[:5]}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--report", default="/tmp/nz_apis.json")
    ap.add_argument("--only", default="")
    ap.add_argument("--strategies", default="dynamic,generic,api")
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--workers", type=int, default=8)
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
            hit = finding["hits"][0] if finding["hits"] else None
            print(
                f"{'OK ' if hit else '-- '}{finding['name']:<26} "
                f"{('n=' + str(hit['count']) + ' ' + hit['url']) if hit else ''}",
                file=sys.stderr,
            )

    findings.sort(key=lambda item: (not item["hits"], item["name"]))
    Path(args.report).write_text(json.dumps(findings, indent=2))
    print(json.dumps({"sources": len(findings), "withJobApi": sum(1 for f in findings if f["hits"])}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
