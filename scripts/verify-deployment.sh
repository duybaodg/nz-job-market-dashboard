#!/usr/bin/env bash
# Verify a deployed NZ Job Market Dashboard stack.
#
# Usage:
#   ./scripts/verify-deployment.sh [base-url]     # default http://localhost:5050
#
# Checks the API is up, the source registry was synced, jobs are flowing, and
# the 2/7/15-day freshness windows respond.
set -euo pipefail

BASE_URL="${1:-http://localhost:5050}"
fail=0

check() {
  local name="$1" url="$2"
  local body
  if ! body=$(curl -fsS --max-time 20 "$url" 2>/dev/null); then
    printf '%-28s FAIL (no response from %s)\n' "$name" "$url"
    fail=1
    return
  fi
  printf '%-28s OK\n' "$name"
  printf '%s\n' "$body" | head -c 400
  printf '\n'
}

echo "Verifying ${BASE_URL} ..."
echo

check "analytics overview" "${BASE_URL}/api/analytics/overview"
check "freshness (2/7/15 days)" "${BASE_URL}/api/analytics/freshness"

printf '%-28s ' "source registry"
sources=$(curl -fsS --max-time 20 "${BASE_URL}/api/admin/sources" 2>/dev/null || true)
if [[ -z "$sources" ]]; then
  echo "FAIL"
  fail=1
else
  echo "$sources" | python3 -c '
import json, sys
data = json.load(sys.stdin)
enabled = [s for s in data if s["enabled"]]
parsers = {}
for source in enabled:
    parsers[source["parser"]] = parsers.get(source["parser"], 0) + 1
print(f"OK ({len(enabled)}/{len(data)} enabled)")
print("  parsers:", ", ".join(f"{k}={v}" for k, v in sorted(parsers.items())))
for name in ("SEEK NZ", "Trade Me Jobs"):
    match = next((s for s in data if s["name"] == name), None)
    if match:
        print(f"  {name}: enabled={match.get("enabled")} frequency={match.get("crawlFrequencyMinutes")}m (crawled last)")
'
fi

printf '%-28s ' "jobs feed"
jobs=$(curl -fsS --max-time 30 "${BASE_URL}/api/jobs?status=all" 2>/dev/null || true)
if [[ -z "$jobs" ]]; then
  echo "FAIL"
  fail=1
else
  echo "$jobs" | python3 -c '
import json, sys
from collections import Counter
data = json.load(sys.stdin)
print(f"OK ({len(data)} jobs)")
for source, count in Counter(j["source"] for j in data).most_common(10):
    print(f"  {source}: {count}")
'
fi

printf '%-28s ' "crawler health"
health=$(curl -fsS --max-time 20 "${BASE_URL}/api/admin/health" 2>/dev/null || true)
if [[ -z "$health" ]]; then
  echo "FAIL"
  fail=1
else
  echo "$health" | python3 -c '
import json, sys
data = json.load(sys.stdin)
order = ["healthy", "degraded", "stale", "empty", "disabled", "never-run"]
counts = {state: sum(1 for source in data["sources"] if source["state"] == state) for state in order}
print("OK (" + ", ".join(state + "=" + str(counts[state]) for state in order) + ")")
for state in ("degraded", "stale"):
    names = [source["source"] for source in data["sources"] if source["state"] == state]
    if names:
        print("  " + state + ": " + ", ".join(names))
print("  total sources: " + str(data["summary"]["totalSources"]))
'
fi

echo
if [[ "$fail" -ne 0 ]]; then
  echo "Some checks failed. If sources are missing, the worker may still be on its first pass."
  exit 1
fi

echo "All checks passed."
