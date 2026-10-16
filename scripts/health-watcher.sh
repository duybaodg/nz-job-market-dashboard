#!/usr/bin/env bash
# Crawler health watcher for the NZ Job Market Dashboard.
# Polls /api/admin/health, sends a Telegram alert ONLY when source health
# states change (or on first run / API failure).
set -euo pipefail

API="http://localhost:5050/api/admin/health"
CACHE="$HOME/.cache/nzjobs-health-state.txt"
ENVF="$HOME/.hermes/.env"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$(dirname "$CACHE")"

# Round 13: the health endpoint is password-gated. Prefer the environment, and
# fall back to the repo .env so a cron/systemd run without an exported var still
# authenticates. Never pass the password on the URL.
if [[ -z "${HEALTH_ACCESS_PASSWORD:-}" && -f "${HEALTH_ENV_FILE:-$SCRIPT_DIR/../.env}" ]]; then
  HEALTH_ACCESS_PASSWORD=$(grep -m1 '^HEALTH_ACCESS_PASSWORD=' "${HEALTH_ENV_FILE:-$SCRIPT_DIR/../.env}" | cut -d= -f2- || true)
fi

HEALTH_AUTH=()
if [[ -n "${HEALTH_ACCESS_PASSWORD:-}" ]]; then
  HEALTH_AUTH=(-H "Authorization: Bearer ${HEALTH_ACCESS_PASSWORD}")
fi

# Telegram credentials from the Hermes env file
TG_TOKEN=$(grep -m1 '^TELEGRAM_BOT_TOKEN=' "$ENVF" | cut -d= -f2- || true)
TG_CHAT=$(grep -m1 '^TELEGRAM_HOME_CHANNEL=' "$ENVF" | cut -d= -f2- || true)

send_telegram() {
  if [ -n "$TG_TOKEN" ] && [ -n "$TG_CHAT" ]; then
    curl -fsS --max-time 20 "https://api.telegram.org/bot${TG_TOKEN}/sendMessage" \
      --data-urlencode "chat_id=${TG_CHAT}" \
      --data-urlencode "text=$1" >/dev/null 2>&1 || true
  fi
}

ALERT=""

if [[ -z "${HEALTH_ACCESS_PASSWORD:-}" ]]; then
  ALERT="NZ Jobs Dashboard: HEALTH_ACCESS_PASSWORD is not configured, so crawler health is locked (fail closed). Set it to resume monitoring."
elif ! body=$(curl -fsS --max-time 30 "${HEALTH_AUTH[@]}" "$API" 2>/dev/null); then
  ALERT="NZ Jobs Dashboard: health API unreachable or the password was rejected ($API). Worker or backend may be down."
else
  summary=$(printf '%s' "$body" | python3 -c '
import json, sys
try:
    d = json.load(sys.stdin)
except Exception:
    print("PARSE_ERROR"); raise SystemExit
items = d if isinstance(d, list) else d.get("sources") or d.get("health") or []
bad = {}
for i in items:
    st = i.get("state")
    if st in ("degraded", "stale", "never-run"):
        bad.setdefault(st, []).append(i.get("source"))
lines = ["degraded=%d stale=%d never_run=%d" % (
    len(bad.get("degraded", [])), len(bad.get("stale", [])), len(bad.get("never-run", [])))]
for st in ("degraded", "stale", "never-run"):
    for s in sorted(bad.get(st, [])):
        lines.append(f"{st}: {s}")
print("\n".join(lines))
' 2>/dev/null) || summary="PARSE_ERROR"

  if [ "$summary" = "PARSE_ERROR" ]; then
    ALERT="NZ Jobs Dashboard: health endpoint returned unparseable data."
  elif [ ! -f "$CACHE" ]; then
    ALERT="NZ Jobs health watcher started. Current state:
$summary"
  elif ! diff -q <(echo "$summary") "$CACHE" >/dev/null 2>&1; then
    ALERT="NZ Jobs crawler health CHANGED:
$(diff <(echo "$summary") "$CACHE" | grep -e '^[<>]' | sed -e 's/^</NOW: /' -e 's/^>/WAS: /')

(full state in dashboard /health page)"
  fi
  [ "${summary:-}" != "PARSE_ERROR" ] && printf '%s\n' "$summary" > "$CACHE" || true
fi

if [ -n "$ALERT" ]; then
  echo "$ALERT"
  send_telegram "$ALERT"
fi
