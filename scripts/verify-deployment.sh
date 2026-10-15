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
check "history (P2 snapshots)" "${BASE_URL}/api/analytics/history?days=7"

printf '%-28s ' "job fields (P1)"
fields_jobs=$(mktemp)
if ! curl -fsS --max-time 30 "${BASE_URL}/api/jobs?status=all" -o "$fields_jobs" 2>/dev/null; then
  echo "FAIL"
  fail=1
elif ! fields_output=$(python3 - "$fields_jobs" <<'PY'
import json
import sys

jobs = json.load(open(sys.argv[1], encoding="utf-8"))
required = ["roleFamily", "advertiserType", "employerDisclosure", "sourceCategory", "salaryPeriod", "duplicateCount", "alsoOnSources", "employerCanonicalName"]
missing = [key for key in required if any(key not in job for job in jobs)]
if missing:
    print("missing keys: " + ", ".join(missing))
    raise SystemExit(1)
employers = sum(1 for job in jobs if job.get("employerDisclosure") == "Disclosed")
agency = sum(1 for job in jobs if job.get("employerDisclosure") == "AgencyOnly")
duplicates = sum(1 for job in jobs if job.get("duplicateCount", 0) > 0)
families = {job["roleFamily"] for job in jobs}
print(f"OK ({len(jobs)} jobs, {employers} disclosed employers, {agency} agency-only, {duplicates} duplicate-badged, {len(families)} role families)")
PY
); then
  echo "FAIL ($fields_output)"
  fail=1
else
  echo "$fields_output"
fi
rm -f "$fields_jobs"

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

printf '%-28s ' "employer identity"
employers=$(curl -fsS --max-time 20 "${BASE_URL}/api/admin/employers" 2>/dev/null || true)
if [[ -z "$employers" ]]; then
  echo "FAIL"
  fail=1
else
  echo "$employers" | python3 -c '
import json, sys
data = json.load(sys.stdin)
print("OK (%d canonicals: %d agency, %d direct; %d listings mapped, %d unmatched)" % (
    data["canonicalCount"], data["agencyCount"], data["directEmployerCount"],
    data["listingsWithCanonical"], data["listingsUnmatched"]))
for item in data["unmatchedNames"][:5]:
    print("  unmatched: %s (%d)" % (item["name"], item["jobCount"]))
'
fi

printf '%-28s ' "deduplication"
duplicates=$(curl -fsS --max-time 20 "${BASE_URL}/api/admin/duplicates" 2>/dev/null || true)
if [[ -z "$duplicates" ]]; then
  echo "FAIL"
  fail=1
else
  echo "$duplicates" | python3 -c '
import json, sys
data = json.load(sys.stdin)
unique = data["uniqueVacancies"]
print("OK (%d groups, %d listings merged, unique vacancies %s of %d listings, %d review candidates, active=%s)" % (
    data["groupCount"], data["listingsMerged"], unique if unique is not None else "not yet validated",
    data["activeListings"], len(data["reviewCandidates"]), data["deduplicated"]))
'
fi

printf '%-28s ' "field coverage"
coverage=$(curl -fsS --max-time 20 "${BASE_URL}/api/admin/coverage" 2>/dev/null || true)
if [[ -z "$coverage" ]]; then
  echo "FAIL"
  fail=1
else
  echo "$coverage" | python3 -c '
import json, sys
data = json.load(sys.stdin)
fields = {field["field"]: field for field in data["fields"]}
required = ["salary", "workMode", "seniority", "employmentType", "skills", "postedDate", "employerCanonical"]
missing = [name for name in required if name not in fields]
if missing:
    print("FAIL (missing fields: " + ", ".join(missing) + ")")
    raise SystemExit(1)
print("OK (%d active listings)" % data["activeJobs"])
for name in required:
    field = fields[name]
    print("  %-18s %5.1f%% (%d/%d)" % (name, field["percentage"], field["known"], field["total"]))
salary = data["salary"]
print("  hourly/daily converted: %d, open-ended: %d, questionable: %d" % (
    salary["converted"], salary["openEnded"], salary["questionable"]))
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

printf '%-28s ' "skill extraction"
skills_jobs=$(mktemp)
top_skills=$(mktemp)
curl -fsS --max-time 30 "${BASE_URL}/api/jobs?status=all" -o "$skills_jobs" 2>/dev/null || true
curl -fsS --max-time 20 "${BASE_URL}/api/analytics/top-skills" -o "$top_skills" 2>/dev/null || true
if [[ ! -s "$skills_jobs" || ! -s "$top_skills" ]]; then
  echo "FAIL"
  fail=1
else
  python3 - "$skills_jobs" "$top_skills" <<'PY'
import json
import sys

jobs = json.load(open(sys.argv[1], encoding="utf-8"))
top = json.load(open(sys.argv[2], encoding="utf-8"))
with_skills = sum(1 for job in jobs if job.get("skills"))
print("OK (" + str(with_skills) + "/" + str(len(jobs)) + " jobs have skills)")
print("  top skills: " + ", ".join(item["skillName"] + "=" + str(item["jobCount"]) for item in top[:15]))
PY
fi
rm -f "$skills_jobs" "$top_skills"

echo
if [[ "$fail" -ne 0 ]]; then
  echo "Some checks failed. If sources are missing, the worker may still be on its first pass."
  exit 1
fi

echo "All checks passed."
