"""LinkedIn parsing plus the bounded guest/manual CLI runners.

Round 19: mechanically moved out of ``scrapling_worker.py``.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
from typing import Any

from models import RawJobInput
from normalisation import sanitise_location

def parse_linkedin_job(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse a LinkedIn job from JSON data (passed as html or text)."""
    import json as _json
    data = {}
    try:
        if html and html.strip().startswith("{"):
            data = _json.loads(html)
        elif text and text.strip().startswith("{"):
            data = _json.loads(text)
    except _json.JSONDecodeError:
        pass
    title = data.get("title") or fallback_title
    return RawJobInput(
        source=source,
        sourceJobId=data.get("sourceJobId"),
        title=title,
        company=data.get("company"),
        location=data.get("location"),
        salaryText=data.get("salaryText"),
        descriptionText=(data.get("descriptionText") or "")[:12000] or None,
        url=data.get("url") or url,
        postedDate=data.get("postedDate"),
        closingDate=data.get("closingDate"),
    )


def linkedin_worker_paths() -> tuple[str, str]:
    """Resolve the LinkedIn helper's venv interpreter and script path.

    ``__file__`` lives at ``crawler/adapters/scrapling/scrapling_worker.py``, so
    the package root is three directories up (``crawler/``). The helper is at
    ``crawler/adapters/linkedin/linkedin_crawler.py`` — joining ``"adapters"``
    onto the ``adapters/`` directory produced the doubled
    ``adapters/adapters/linkedin/...`` path that broke the source.
    """
    crawler_dir = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    venv_python = os.path.join(crawler_dir, ".venv", "bin", "python")
    if not os.path.exists(venv_python):
        venv_python = "python3"  # fallback
    script = os.path.join(crawler_dir, "adapters", "linkedin", "linkedin_crawler.py")
    return venv_python, script


def run_linkedin_crawl(source: str, output_path: str, max_jobs: int = 80) -> list[RawJobInput]:
    """Run the LinkedIn crawler script and parse its JSON output."""
    import subprocess as _sp, json as _json, os as _os
    venv_python, script = linkedin_worker_paths()
    if not _os.path.exists(script):
        raise RuntimeError(f"LinkedIn crawler script not found: {script}")
    result = _sp.run(
        [venv_python, script, "--location", "New Zealand", "--max-jobs", str(max_jobs), "--delay", "0.3", "--output", output_path],
        capture_output=True, text=True, timeout=900
    )
    if result.returncode != 0:
        raise RuntimeError(f"LinkedIn crawler failed: {result.stderr[:500]}")
    with open(output_path) as f:
        data = _json.load(f)
    jobs_data = data.get("jobs", [])
    return [
        RawJobInput(
            source=source,
            sourceJobId=j.get("sourceJobId"),
            title=j.get("title", "Unknown"),
            company=j.get("company"),
            location=j.get("location"),
            salaryText=j.get("salaryText"),
            descriptionText=(j.get("descriptionText") or "")[:12000] or None,
            url=j.get("url", ""),
            postedDate=j.get("postedDate"),
            closingDate=j.get("closingDate"),
        )
        for j in jobs_data if j.get("title") != "Unknown" and j.get("company")
    ]


LINKEDIN_GUEST_KEYWORDS = (
    "developer",
    "engineer",
    "data",
    "cloud",
    "devops",
    "security",
    "analyst",
)
#: One boolean search page covering the small IT keyword set. Request budget is
#: enforced inside the CLI: one search page + at most ten detail fetches.
LINKEDIN_GUEST_KEYWORDS_QUERY = " OR ".join(LINKEDIN_GUEST_KEYWORDS)
LINKEDIN_GUEST_MAX_JOBS = 40
LINKEDIN_GUEST_DETAIL_LIMIT = 10
LINKEDIN_GUEST_DELAY_SECONDS = 1.5
LINKEDIN_GUEST_JOB_AGE_DAYS = 14
LINKEDIN_GUEST_TIMEOUT_SECONDS = 240
LINKEDIN_COUNTRY_SUFFIX = re.compile(r"\s*,?\s*(?:new zealand|aotearoa|nz)\s*$", re.I)
LINKEDIN_REMOTE = re.compile(r"^(?:remote|work from home|wfh)(?:\s*\(nz\))?$", re.I)


def linkedin_guest_worker_paths() -> tuple[str, str]:
    """Resolve the guest CLI interpreter and script path (stdlib only)."""
    crawler_dir = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    venv_python = os.path.join(crawler_dir, ".venv", "bin", "python")
    if not os.path.exists(venv_python):
        venv_python = "python3"
    script = os.path.join(crawler_dir, "adapters", "linkedin", "linkedin_guest_crawler.py")
    return venv_python, script


def linkedin_guest_argv(python: str, script: str) -> list[str]:
    """The fixed, bounded argv the worker is allowed to use."""
    return [
        python,
        script,
        "--keywords",
        LINKEDIN_GUEST_KEYWORDS_QUERY,
        "--location",
        "New Zealand",
        "--jobage",
        str(LINKEDIN_GUEST_JOB_AGE_DAYS),
        "--max-jobs",
        str(LINKEDIN_GUEST_MAX_JOBS),
        "--detail-limit",
        str(LINKEDIN_GUEST_DETAIL_LIMIT),
        "--delay",
        str(LINKEDIN_GUEST_DELAY_SECONDS),
        "--format",
        "json",
    ]


def normalise_linkedin_location(value: str | None) -> str | None:
    """Strip the country suffix and canonicalise remote work for LinkedIn.

    "Auckland, New Zealand" -> "Auckland"; "Remote"/"Work from home" ->
    "Remote (NZ)"; the .NET LocationSanitiser still runs on ingest.
    """
    cleaned = sanitise_location(value)
    if not cleaned:
        return None
    if LINKEDIN_REMOTE.match(cleaned):
        return "Remote (NZ)"
    stripped = LINKEDIN_COUNTRY_SUFFIX.sub("", cleaned).strip(" ,;|-")
    return stripped or "New Zealand"


def run_linkedin_guest_crawl(
    source: str, runner: Any = None
) -> tuple[list[RawJobInput], dict[str, Any]]:
    """Run the guest CLI and map its RawJobInput envelope for normal ingest.

    A non-zero exit is a FAILED run. A block signature (``BLOCKED`` on stderr)
    becomes a clear exception message so /api/admin/health surfaces it. No
    retries here and no UA rotation: the CLI already backs off and gives up.
    """
    python, script = linkedin_guest_worker_paths()
    if not os.path.exists(script):
        raise RuntimeError(f"LinkedIn guest crawler script not found: {script}")

    argv = linkedin_guest_argv(python, script)
    run = runner or subprocess.run
    result = run(
        argv,
        capture_output=True,
        text=True,
        timeout=LINKEDIN_GUEST_TIMEOUT_SECONDS,
    )
    if result.returncode != 0:
        stderr = (result.stderr or "").strip()
        code = ""
        try:
            code = json.loads(stderr).get("code", "")
        except (ValueError, AttributeError):
            pass
        if code == "BLOCKED" or "BLOCKED" in stderr:
            raise RuntimeError(f"LinkedIn guest crawl was blocked: {stderr[:500]}")
        raise RuntimeError(f"LinkedIn guest crawler failed: {stderr[:500]}")

    try:
        payload = json.loads(result.stdout or "{}")
    except ValueError as exc:
        raise RuntimeError(f"LinkedIn guest crawler returned invalid JSON: {exc}") from exc

    active_by_id = payload.get("isActiveById") or {}
    jobs: list[RawJobInput] = []
    for item in payload.get("jobs") or []:
        job_id = str(item.get("sourceJobId") or "")
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=item.get("sourceJobId"),
                title=item.get("title") or "Unknown",
                company=item.get("company"),
                location=normalise_linkedin_location(item.get("location")),
                salaryText=item.get("salaryText"),
                descriptionText=(item.get("descriptionText") or "")[:12000] or None,
                url=item.get("url") or "",
                postedDate=item.get("postedDate"),
                closingDate=item.get("closingDate"),
                isActive=active_by_id.get(job_id, True),
            )
        )

    meta = {
        "count": len(jobs),
        "inactive": sum(1 for job in jobs if not job.isActive),
        "cliMeta": payload.get("meta") or {},
    }
    return jobs, meta
