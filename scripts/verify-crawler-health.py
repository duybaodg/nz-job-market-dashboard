#!/usr/bin/env python3
"""Reconcile the crawler-health report with the registry, jobs feed and crawl runs.

This is the guard for the Round 12 live divergence: `/api/admin/health` kept
returning eight sources that had been deleted from `crawler/sources/sites.json`
as ordinary `disabled` rows, so its totals and per-source jobs counts did not
match the current registry or the jobs feed.

Checks:
  1. The active health list and `/api/admin/sources` are the same set, and the
     summary counts add up to `summary.totalSources`.
  2. Sources removed from the registry are absent from both active lists and
     appear only in the separate `retiredSources` bucket.
  3. Revived sources are present, enabled, and report their live `jobsYielded`.
  4. Every health `jobsYielded` equals the active jobs-feed count for the same
     source, and no feed source is missing from health.
  5. Each source's state, failures and zero-yield streak independently recompute
     from `/api/admin/crawl-runs` (sources caught mid-crawl are reported as
     skipped, not failed).

Usage:
  ./scripts/verify-crawler-health.py [base-url]   # default http://localhost:5050

Round 13: `/api/admin/health` is protected by the shared secret in
`HEALTH_ACCESS_PASSWORD`, sent as an `Authorization: Bearer` header. Set that
environment variable before running (the deployment verify script passes it
through). With it unset the endpoint fails closed and this script reports 401.
"""

from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.request
from collections import Counter
from datetime import datetime, timedelta

BASE_URL = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5050").rstrip("/")

# The health endpoint requires this; other admin endpoints stay open but the
# header is harmless for them.
HEALTH_ACCESS_PASSWORD = os.environ.get("HEALTH_ACCESS_PASSWORD", "").strip()

REMOVED = [
    "Presto Resourcing",
    "WorkHere",
    "Trade Me",
    "Vista Group",
    "Summer of Tech",
    "H2R Technology",
    "Health NZ Careers",
    "Jora NZ",
]
REVIVED = ["PlaceMe IT", "Talent International NZ"]

problems: list[str] = []
warnings: list[str] = []


def fetch(path: str):
    request = urllib.request.Request(f"{BASE_URL}{path}")
    if HEALTH_ACCESS_PASSWORD:
        request.add_header("Authorization", f"Bearer {HEALTH_ACCESS_PASSWORD}")
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def parse_time(value):
    if not value:
        return None
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def leading_failures(runs):
    count = 0
    for run in runs:
        if run["status"] == "Running":
            continue
        if run["status"] == "Failed":
            count += 1
            continue
        break
    return count


def leading_zero_yield(runs):
    count = 0
    for run in runs:
        if run["status"] == "Running":
            continue
        if run["status"] != "Completed" or run["totalJobsFound"] > 0:
            break
        count += 1
    return count


def evaluate(now, enabled, last_crawled, frequency, jobs_yielded, runs):
    """Independent port of Application.Health.SourceHealthEvaluator.Evaluate."""
    failures = leading_failures(runs)
    zero_yield = leading_zero_yield(runs)
    last_run = runs[0] if runs else None
    last_status = last_run["status"] if last_run else None
    last_crawled = last_crawled or (last_run["startedAt"] if last_run else None)

    if not enabled:
        return "disabled", failures, zero_yield, last_status
    if last_crawled is None and last_status is None:
        return "never-run", failures, zero_yield, last_status
    if failures >= 3:
        return "degraded", failures, zero_yield, last_status

    has_ever_yielded = jobs_yielded > 0 or any(run["totalJobsFound"] > 0 for run in runs)
    if last_status == "Completed" and zero_yield >= 3 and has_ever_yielded:
        return "degraded", failures, zero_yield, last_status

    stale_before = now - timedelta(minutes=3 * max(frequency, 1))
    if last_crawled < stale_before:
        return "stale", failures, zero_yield, last_status
    if last_status == "Completed" and jobs_yielded == 0:
        return "empty", failures, zero_yield, last_status
    return "healthy", failures, zero_yield, last_status


def fail(message: str) -> None:
    problems.append(message)


def main() -> int:
    health = fetch("/api/admin/health")
    generated_at = parse_time(health["generatedAt"])
    summary = health["summary"]
    active = {source["source"]: source for source in health.get("sources", [])}
    retired = {source["source"]: source for source in health.get("retiredSources", [])}

    sources = fetch("/api/admin/sources")
    current = {source["name"]: source for source in sources}

    # --- 1. active list matches the current registry ------------------------
    if set(active) != set(current):
        missing = sorted(set(current) - set(active))
        extra = sorted(set(active) - set(current))
        if missing:
            fail(f"health is missing current-registry sources: {', '.join(missing)}")
        if extra:
            fail(f"health lists non-registry sources: {', '.join(extra)}")
    if len(active) != summary["totalSources"]:
        fail(f"summary.totalSources={summary['totalSources']} but sources[]={len(active)}")

    state_counts = Counter(source["state"] for source in active.values())
    expected_states = {
        "healthy": "healthy",
        "degraded": "degraded",
        "stale": "stale",
        "empty": "empty",
        "disabled": "disabled",
        "neverRun": "never-run",
    }
    for summary_key, state in expected_states.items():
        if summary[summary_key] != state_counts[state]:
            fail(f"summary.{summary_key}={summary[summary_key]} but sources[] has {state_counts[state]} '{state}'")
    if "retired" in state_counts:
        fail(f"{state_counts['retired']} retired source(s) leaked into the active sources[] list")
    if summary.get("retired", 0) != len(retired):
        fail(f"summary.retired={summary.get('retired')} but retiredSources[]={len(retired)}")

    # --- 2. registry removals are gone --------------------------------------
    for name in REMOVED:
        if name in active:
            fail(f"removed source '{name}' is still in active sources[]")
        if name in current:
            fail(f"removed source '{name}' is still in /api/admin/sources")
        if name not in retired:
            warnings.append(f"removed source '{name}' has no row in retiredSources[]")

    # --- 3. revived sources report their live state -------------------------
    for name in REVIVED:
        entry = active.get(name)
        if entry is None:
            fail(f"revived source '{name}' is missing from active sources[]")
            continue
        if not entry["enabled"]:
            fail(f"revived source '{name}' is not enabled")
        if entry["state"] in ("disabled", "retired"):
            fail(f"revived source '{name}' reports state '{entry['state']}'")
        print(
            f"  revived {name}: state={entry['state']} jobsYielded={entry['jobsYielded']} "
            f"lastStatus={entry['lastStatus']}"
        )

    # --- 4. jobsYielded matches the active jobs feed ------------------------
    jobs = fetch("/api/jobs")
    feed_counts = Counter(job["source"] for job in jobs)
    for name, entry in active.items():
        expected = feed_counts.get(name, 0)
        if entry["jobsYielded"] != expected:
            fail(f"{name}: health jobsYielded={entry['jobsYielded']} but jobs feed has {expected} active")
    feed_without_health = sorted(set(feed_counts) - set(active))
    if feed_without_health:
        fail(f"jobs feed yields from sources absent from health: {', '.join(feed_without_health)}")
    yielding = sum(1 for entry in active.values() if entry["jobsYielded"] > 0)
    print(f"  yielding sources: health={yielding}, jobs feed={len(feed_counts)}")

    # --- 5. states recompute from crawl_runs --------------------------------
    runs_by_source: dict[str, list] = {}
    for run in fetch("/api/admin/crawl-runs"):
        started = parse_time(run["startedAt"])
        if started is None or started > generated_at:
            continue
        runs_by_source.setdefault(run["source"], []).append(run)
    for runs in runs_by_source.values():
        runs.sort(key=lambda run: parse_time(run["startedAt"]), reverse=True)

    compared = 0
    skipped = 0
    for name, entry in active.items():
        runs = runs_by_source.get(name, [])
        newest = runs[0] if runs else None
        finished = parse_time(newest["finishedAt"]) if newest else None
        if newest is not None and (finished is None or finished > generated_at):
            # The crawl was in flight when health was computed; its status may
            # have changed since. Report it, but do not call it a divergence.
            skipped += 1
            continue
        expected, failures, zero_yield, last_status = evaluate(
            generated_at,
            entry["enabled"],
            parse_time(entry["lastCrawledAt"]),
            entry["crawlFrequencyMinutes"],
            entry["jobsYielded"],
            runs,
        )
        if expected != entry["state"]:
            fail(f"{name}: health state={entry['state']} but crawl_runs recompute to {expected}")
        if failures != entry["consecutiveFailures"]:
            fail(f"{name}: health failures={entry['consecutiveFailures']} but crawl_runs give {failures}")
        if zero_yield != entry["consecutiveZeroYield"]:
            fail(
                f"{name}: health zeroYield={entry['consecutiveZeroYield']} but crawl_runs give {zero_yield}"
            )
        if last_status != entry["lastStatus"]:
            fail(f"{name}: health lastStatus={entry['lastStatus']} but newest run is {last_status}")
        compared += 1

    print(f"  states recomputed from crawl_runs: {compared} compared, {skipped} skipped (crawl in flight)")

    for warning in warnings:
        print(f"  WARN: {warning}")
    if problems:
        print()
        for problem in problems:
            print(f"FAIL: {problem}")
        return 1
    print("OK: health matches the registry, the jobs feed and crawl_runs")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except urllib.error.HTTPError as error:
        if error.code == 429:
            hint = "Too many failed attempts — wait five minutes, then retry with HEALTH_ACCESS_PASSWORD."
        elif error.code == 401:
            hint = "Set HEALTH_ACCESS_PASSWORD to the shared health secret."
        else:
            hint = ""
        print(f"FAIL: {error.url} returned HTTP {error.code}. {hint}".rstrip())
        raise SystemExit(1)
