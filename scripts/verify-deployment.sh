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

# Round 13: /api/admin/health is gated by HEALTH_ACCESS_PASSWORD. Send it as an
# Authorization: Bearer header; when it is unset the endpoint fails closed and
# the health checks below will report FAIL (which is the correct behaviour).
HEALTH_AUTH=()
if [[ -n "${HEALTH_ACCESS_PASSWORD:-}" ]]; then
  HEALTH_AUTH=(-H "Authorization: Bearer ${HEALTH_ACCESS_PASSWORD}")
fi

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

if [[ -z "${HEALTH_ACCESS_PASSWORD:-}" ]]; then
  echo "Note: HEALTH_ACCESS_PASSWORD is unset — the health endpoint fails closed,"
  echo "      so the crawler-health checks below will report FAIL by design."
  echo
fi

check "analytics overview" "${BASE_URL}/api/analytics/overview"
check "freshness (2/7/15 days)" "${BASE_URL}/api/analytics/freshness"
check "history (P2 snapshots)" "${BASE_URL}/api/analytics/history?days=7"

printf '%-28s ' "job fields (P1)"
fields_jobs=$(mktemp)
if ! curl -fsS --max-time 30 "${BASE_URL}/api/jobs?status=all&limit=5000" -o "$fields_jobs" 2>/dev/null; then
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
sources=$(curl -fsS --max-time 20 "${HEALTH_AUTH[@]}" "${BASE_URL}/api/admin/sources" 2>/dev/null || true)
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

printf '%-28s ' "adzuna removed"
if echo "${sources:-[]}" | python3 -c '
import json, sys
data = json.load(sys.stdin)
adzuna = [s for s in data if s["name"] == "Adzuna NZ"]
if any(s["enabled"] for s in adzuna):
    print("FAIL (Adzuna NZ is still enabled)")
    raise SystemExit(1)
print("OK (Adzuna NZ present but disabled)" if adzuna else "OK (Adzuna NZ absent)")
'; then
  :
else
  fail=1
fi

printf '%-28s ' "Round 12 removals retired"
# Eight sources were deleted from the registry; they must disappear from the
# current-registry list (they are retired, not deleted, so historical jobs keep
# a source label, but retirement is tracked separately from "disabled").
if echo "${sources:-[]}" | python3 -c '
import json, sys
data = json.load(sys.stdin)
removed = ["Presto Resourcing", "WorkHere", "Trade Me", "Vista Group",
           "Summer of Tech", "H2R Technology", "Health NZ Careers", "Jora NZ"]
present = [s["name"] for s in data if s["name"] in removed]
if present:
    print("FAIL (still in the current registry: " + ", ".join(present) + ")")
    raise SystemExit(1)
print("OK (all 8 absent from the current registry)")
'; then
  :
else
  fail=1
fi

printf '%-28s ' "Round 12 revivals enabled"
if echo "${sources:-[]}" | python3 -c '
import json, sys
data = json.load(sys.stdin)
by_name = {s["name"]: s for s in data}
problems = [name for name in ("PlaceMe IT", "Talent International NZ")
            if not by_name.get(name, {}).get("enabled")]
if problems:
    print("FAIL (not enabled: " + ", ".join(problems) + ")")
    raise SystemExit(1)
print("OK (PlaceMe IT and Talent International NZ enabled)")
'; then
  :
else
  fail=1
fi

printf '%-28s ' "Round 17 SJS board"
# Round 17: SJS moved to student.sjs.co.nz/jobs. The old ?class= ICT filter is
# gone; the source renders the plain board plus technology ?q= slices and gates
# titles client-side. The registry must never keep the retired query.
if echo "${sources:-[]}" | python3 -c '
import json, sys
data = json.load(sys.stdin)
sjs = next((s for s in data if s["name"] == "Student Job Search"), None)
problems = []
if sjs is None:
    problems.append("missing from the registry")
else:
    if not sjs.get("enabled"):
        problems.append("not enabled")
    if sjs.get("baseUrl") != "https://student.sjs.co.nz/jobs":
        problems.append("baseUrl is not https://student.sjs.co.nz/jobs")
    if sjs.get("parser") != "sjs":
        problems.append("parser is not sjs")
if problems:
    print("FAIL (" + "; ".join(problems) + ")")
    raise SystemExit(1)
print("OK (Student Job Search on student.sjs.co.nz with the sjs parser)")
'; then
  :
else
  fail=1
fi

printf '%-28s ' "Round 17 URL refresh"
# Round 17: verify the operator-supplied sources are on robots-allowed URLs and
# that Employment Hero is present. The Robert Walters ?industry=it facet query
# is robots-disallowed and must never become a base URL.
if echo "${sources:-[]}" | python3 -c '
import json, sys
data = json.load(sys.stdin)
by_name = {s["name"]: s for s in data}
problems = []
xero = by_name.get("Xero")
if not xero or xero.get("baseUrl") != "https://careers.xero.com/jobs/":
    problems.append("Xero is not on careers.xero.com")
randstad = by_name.get("Randstad NZ")
if not randstad or randstad.get("baseUrl") != "https://www.randstad.co.nz/jobs/s-it-ict/":
    problems.append("Randstad is not on the IT path")
consult = by_name.get("Consult Recruitment")
if not consult or "consultrecruitment.co.nz" not in (consult.get("baseUrl") or ""):
    problems.append("Consult is not on the new domain")
robert = by_name.get("Robert Walters NZ")
if not robert or "industry=" in (robert.get("baseUrl") or ""):
    problems.append("Robert Walters base URL carries the disallowed facet")
if not robert or robert.get("baseUrl") != "https://www.robertwalters.co.nz/expertise/technology/technology-jobs.html":
    problems.append("Robert Walters is not on the path-only technology board")
hero = by_name.get("Employment Hero")
if not hero or not hero.get("enabled"):
    problems.append("Employment Hero missing or disabled")
if problems:
    print("FAIL (" + "; ".join(problems) + ")")
    raise SystemExit(1)
print("OK (Xero/Randstad/Consult/Robert Walters/Employment Hero)")
'; then
  :
else
  fail=1
fi

printf '%-28s ' "LinkedIn manual-only"
# Round 16: robots.txt disallows LinkedIn for generic agents. The source must
# stay disabled; the guest CLI is manual-only and never scheduled.
if echo "${sources:-[]}" | python3 -c '
import json, sys
data = json.load(sys.stdin)
linkedin = next((s for s in data if s["name"] == "LinkedIn Jobs"), None)
if linkedin is None:
    print("FAIL (missing from the registry)")
    raise SystemExit(1)
if linkedin.get("enabled"):
    print("FAIL (LinkedIn Jobs must stay enabled:false)")
    raise SystemExit(1)
print("OK (LinkedIn Jobs present but disabled; guest CLI stays manual-only)")
'; then
  :
else
  fail=1
fi

printf '%-28s ' "employer identity"
employers=$(curl -fsS --max-time 20 "${HEALTH_AUTH[@]}" "${BASE_URL}/api/admin/employers" 2>/dev/null || true)
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
duplicates=$(curl -fsS --max-time 20 "${HEALTH_AUTH[@]}" "${BASE_URL}/api/admin/duplicates" 2>/dev/null || true)
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
coverage=$(curl -fsS --max-time 20 "${HEALTH_AUTH[@]}" "${BASE_URL}/api/admin/coverage" 2>/dev/null || true)
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
jobs=$(curl -fsS --max-time 30 "${BASE_URL}/api/jobs?status=all&limit=5000" 2>/dev/null || true)
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

printf '%-28s ' "SEEK SSR fields"
# Round 16: the SSR-JSON parser must populate company and ISO posted dates, and
# every newly parsed row must link to the canonical www.seek.co.nz/job/<id> URL.
if [[ -z "${jobs:-}" ]]; then
  echo "FAIL (jobs feed unavailable)"
  fail=1
else
  echo "$jobs" | python3 -c '
import json, re, sys
data = json.load(sys.stdin)
seek = [job for job in data if job.get("source") == "SEEK NZ"]
company = sum(1 for job in seek if job.get("company"))
posted = sum(1 for job in seek if job.get("postedDate"))
problems = []
if not seek:
    problems.append("no SEEK NZ rows")
if company == 0:
    problems.append("no SEEK NZ row has a company")
if posted == 0:
    problems.append("no SEEK NZ row has a posted date")
canonical = [job for job in seek if job.get("company") and job.get("postedDate")]
bad_urls = [job.get("url") for job in canonical
            if not re.match(r"^https://www\.seek\.co\.nz/job/\d+$", job.get("url") or "")]
if bad_urls:
    problems.append("non-canonical SEEK URL(s): " + ", ".join(bad_urls[:3]))
if problems:
    print("FAIL (" + "; ".join(problems) + ")")
    raise SystemExit(1)
print("OK (%d SEEK rows: %d company, %d posted, %d canonical URL)" % (
    len(seek), company, posted, len(canonical)))
'
fi

printf '%-28s ' "salary parse audit"
salary_audit=$(curl -fsS --max-time 90 -X POST "${HEALTH_AUTH[@]}" "${BASE_URL}/api/admin/salaries/audit" 2>/dev/null || true)
if [[ -z "$salary_audit" ]]; then
  echo "FAIL"
  fail=1
else
  echo "$salary_audit" | python3 -c '
import json, sys
data = json.load(sys.stdin)
print("OK (scanned=%d, non-pay ignored=%d, low senior/lead flagged=%d, below-floor cleared=%d)" % (
    data["jobsScanned"], data["nonPaySalaryIgnored"], data["seniorLowPayFlagged"], data["outOfRangeCleared"]))
'
fi

printf '%-28s ' "non-pay salary tokens"
# Round 11: no stored salary may come from a bare-k page token (contact string,
# file size, quantity or decimal rate). Salary text is retained as provenance,
# so the check is on rows that still publish numeric bounds. Round 16: a bare-k
# token is legitimate when the posting text itself shows a currency prefix
# (SEEK teaser "$150K + 2% equity" stores salaryText "150K").
if [[ -z "${jobs:-}" ]]; then
  jobs=$(curl -fsS --max-time 30 "${BASE_URL}/api/jobs?status=all&limit=5000" 2>/dev/null || true)
fi
if [[ -z "$jobs" ]]; then
  echo "FAIL"
  fail=1
else
  echo "$jobs" | python3 -c '
import json, sys, re
jobs = json.load(sys.stdin)
bare_k = re.compile(r"^\d{2,3}\s?[kK]$")
def has_currency_context(job):
    token = (job.get("salaryText") or "").strip()
    if not token:
        return False
    text = (job.get("descriptionSummary") or "") + " " + (job.get("descriptionText") or "")
    return re.search(r"(?:\$|NZD)\s*" + re.escape(token), text, re.I) is not None
targets = [j for j in jobs if j.get("source") == "Potentia" and j.get("title") == "Senior Software Engineer - iOS"]
offenders = [
    j for j in jobs
    if (j.get("salaryMin") is not None or j.get("salaryMax") is not None)
    and bare_k.match((j.get("salaryText") or "").strip())
    and not has_currency_context(j)
]
problems = []
if offenders:
    problems.append("%d stored salary bound(s) are a bare-k token: %s" % (
        len(offenders), ", ".join(j["title"] for j in offenders[:5])))
if targets and targets[0].get("salaryMin") is not None:
    problems.append("Potentia Senior Software Engineer - iOS still reports salaryMin=%s" % targets[0]["salaryMin"])
if problems:
    print("FAIL (" + "; ".join(problems) + ")")
    raise SystemExit(1)
print("OK (no bare-k non-pay salary bounds; Potentia iOS misparse is cleared)")
'
fi

printf '%-28s ' "crawler health"
health=$(curl -fsS --max-time 20 "${HEALTH_AUTH[@]}" "${BASE_URL}/api/admin/health" 2>/dev/null || true)
if [[ -z "$health" ]]; then
  echo "FAIL"
  fail=1
else
  echo "$health" | python3 -c '
import json, sys
data = json.load(sys.stdin)
order = ["healthy", "degraded", "stale", "empty", "disabled", "never-run"]
counts = {state: sum(1 for source in data["sources"] if source["state"] == state) for state in order}
print("OK (" + ", ".join(state + "=" + str(counts[state]) for state in order)
      + ", retired=" + str(data["summary"].get("retired", 0)) + ")")
for state in ("degraded", "stale"):
    names = [source["source"] for source in data["sources"] if source["state"] == state]
    if names:
        print("  " + state + ": " + ", ".join(names))
print("  current-registry sources: " + str(data["summary"]["totalSources"]))
'
fi

printf '%-28s ' "health reconciliation"
if command -v python3 >/dev/null 2>&1 && [[ -f "$(dirname "$0")/verify-crawler-health.py" ]]; then
  if python3 "$(dirname "$0")/verify-crawler-health.py" "$BASE_URL" >/tmp/verify-crawler-health.out 2>&1; then
    echo "OK"
    sed 's/^/  /' /tmp/verify-crawler-health.out
  else
    echo "FAIL"
    sed 's/^/  /' /tmp/verify-crawler-health.out
    fail=1
  fi
else
  echo "SKIP (verify-crawler-health.py not found)"
fi

printf '%-28s ' "skill extraction"
skills_jobs=$(mktemp)
top_skills=$(mktemp)
curl -fsS --max-time 30 "${BASE_URL}/api/jobs?status=all&limit=5000" -o "$skills_jobs" 2>/dev/null || true
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

printf '%-28s ' "Round 14 metric consistency"
# Round 14: the dashboard reads these payloads from different endpoints, so the
# figures that describe the same thing must agree (active cohort, posted-date
# coverage, salary coverage arithmetic, the 2/7/15 windows, unique vacancies).
metric_tmp=$(mktemp -d)
for metric in overview freshness top-skills jobs-by-region; do
  curl -fsS --max-time 20 "${BASE_URL}/api/analytics/${metric}" -o "${metric_tmp}/${metric}.json" 2>/dev/null || true
done
if python3 - "$metric_tmp" <<'PY'
import json
import os
import sys

base = sys.argv[1]
for name in ("overview", "freshness", "top-skills", "jobs-by-region"):
    path = os.path.join(base, name + ".json")
    if not os.path.exists(path) or os.path.getsize(path) == 0:
        print("FAIL (no response from /api/analytics/" + name + ")")
        raise SystemExit(1)

overview = json.load(open(os.path.join(base, "overview.json"), encoding="utf-8"))
freshness = json.load(open(os.path.join(base, "freshness.json"), encoding="utf-8"))
skills = json.load(open(os.path.join(base, "top-skills.json"), encoding="utf-8"))
regions = json.load(open(os.path.join(base, "jobs-by-region.json"), encoding="utf-8"))

problems = []
active = overview["activeListings"]
if freshness["totalActiveJobs"] != active:
    problems.append("freshness totalActiveJobs %s != overview activeListings %s" % (freshness["totalActiveJobs"], active))
if overview["postedDateKnown"] + overview["postedDateMissing"] != active:
    problems.append("postedDateKnown + postedDateMissing != activeListings")
salary = overview["salary"]
expected_coverage = 0 if salary["cohortCount"] == 0 else round(salary["eligibleCount"] / salary["cohortCount"] * 100, 1)
if abs(salary["coveragePercent"] - expected_coverage) > 0.05:
    problems.append("salary coverage %s != %s" % (salary["coveragePercent"], expected_coverage))
if [window["days"] for window in freshness["windows"]] != [2, 7, 15]:
    problems.append("freshness windows are not 2/7/15")
for window in freshness["windows"]:
    if (window["postedDateKnown"], window["postedDateMissing"]) != (overview["postedDateKnown"], overview["postedDateMissing"]):
        problems.append("%sd window posted-date coverage != overview" % window["days"])
if overview["uniqueVacancies"] != freshness["uniqueVacancies"]:
    problems.append("uniqueVacancies differs between overview and freshness")
if skills and max(skill["jobCount"] for skill in skills) > active:
    problems.append("a skill count exceeds activeListings")
if regions and sum(region["jobCount"] for region in regions) > active:
    problems.append("region counts exceed activeListings")

if problems:
    print("FAIL (" + "; ".join(problems) + ")")
    raise SystemExit(1)
print("OK (active=%d, salary coverage=%s%%, windows=2/7/15, top skills=%d, regions=%d, unique vacancies=%s)" % (
    active, salary["coveragePercent"], len(skills), len(regions), overview["uniqueVacancies"]))
PY
then
  :
else
  fail=1
fi
rm -rf "$metric_tmp"

echo
if [[ "$fail" -ne 0 ]]; then
  echo "Some checks failed. If sources are missing, the worker may still be on its first pass."
  exit 1
fi

echo "All checks passed."
