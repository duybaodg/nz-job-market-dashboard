#!/usr/bin/env python3
"""
LinkedIn job crawler that feeds into the NZ Job Market Dashboard pipeline.

Uses the linkedin-mcp-server MCP to search LinkedIn and extract IT jobs
in New Zealand. Outputs the existing RawJobInput JSON format.

Usage:
    crawler/.venv/bin/python crawler/adapters/linkedin/linkedin_crawler.py \
        --keywords "software engineer,cloud architect,devops" \
        --location "New Zealand" \
        --max-jobs 30

One-time setup:
    uvx mcp-server-linkedin@latest --login --login-timeout 300 --no-headless
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
import traceback
from dataclasses import asdict
from datetime import datetime, timezone
from typing import Any

# ---- MCP Client ------------------------------------------------------------------

class MCPClient:
    """Minimal MCP stdio client for the LinkedIn MCP server."""

    def __init__(self, tool_timeout: int = 300) -> None:
        self.proc = subprocess.Popen(
            ["uvx", "mcp-server-linkedin@latest", "--tool-timeout", str(tool_timeout)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        self._send(json.dumps({
            "jsonrpc": "2.0", "id": 0, "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "nz-job-dashboard", "version": "1.0"}
            }
        }) + "\n")
        self._read()  # consume initialize response
        self._send(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self._next_id = 1

    def _send(self, payload: str) -> None:
        self.proc.stdin.write(payload)
        self.proc.stdin.flush()

    def _read(self) -> str:
        """Read one JSON-RPC response line."""
        response = ""
        while True:
            line = self.proc.stdout.readline()
            if not line:
                break
            response += line
            # JSON-RPC responses are one line per message
            if line.strip().startswith('{"jsonrpc"'):
                return line.strip()
        return response

    def call_tool(self, name: str, arguments: dict[str, Any]) -> str:
        """Call a tool and return its text content."""
        req_id = self._next_id
        self._next_id += 1
        payload = json.dumps({
            "jsonrpc": "2.0", "id": req_id, "method": "tools/call",
            "params": {"name": name, "arguments": arguments}
        }) + "\n"
        self._send(payload)
        result_line = self._read()
        try:
            data = json.loads(result_line)
            if "result" in data:
                content = data["result"].get("content", [])
                if content and isinstance(content, list):
                    return content[0].get("text", "")
            elif "error" in data:
                err = data["error"]
                raise RuntimeError(f"Tool {name} failed: {err.get('message', str(err))}")
        except json.JSONDecodeError:
            pass
        return result_line

    def close(self) -> None:
        try:
            self.proc.terminate()
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()


# ---- LinkedIn Specific Helpers ----------------------------------------------------

# NZ IT job search queries (cover the main categories)
DEFAULT_QUERIES = [
    "software engineer",
    "software developer",
    "full stack developer",
    "frontend developer",
    "backend developer",
    "devops engineer",
    "cloud engineer",
    "data engineer",
    "security engineer",
    "cyber security",
    "data scientist",
    "qa engineer",
    "test engineer",
    "solutions architect",
    "network engineer",
    "it support",
    "systems engineer",
    "mobile developer",
]


def extract_job_ids(search_text: str) -> list[str]:
    """Extract LinkedIn job IDs from search result page text.

    LinkedIn job URLs look like /jobs/view/{job_id}/
    The search results page shows these as part of the content.
    """
    ids: list[str] = []
    # The page text includes links like linkedin.com/jobs/view/4252026496/
    seen: set[str] = set()
    # Try explicit job view URLs first
    for match in re.finditer(r'/jobs/view/(\d{7,12})/', search_text):
        jid = match.group(1)
        if jid not in seen:
            seen.add(jid)
            ids.append(jid)
    # Also try currentJobId patterns
    for match in re.finditer(r'"currentJobId"\s*:\s*"(\d{7,12})"', search_text):
        jid = match.group(1)
        if jid not in seen:
            seen.add(jid)
            ids.append(jid)
    # Try jobPostingUrn
    for match in re.finditer(r'urn:li:fsd_jobPosting:(\d{7,12})', search_text):
        jid = match.group(1)
        if jid not in seen:
            seen.add(jid)
            ids.append(jid)
    return ids


def parse_job_detail(detail_text: str, job_id: str) -> dict[str, Any] | None:
    """Parse get_job_details response into normalized fields."""
    if not detail_text or "error" in detail_text.lower():
        return None

    # The response is JSON: {"url":"...","sections":{"job_posting":"<page text>"},"references":{...}}
    page_text = detail_text
    references: dict[str, Any] = {}
    try:
        data = json.loads(detail_text)
        if isinstance(data, dict):
            sections = data.get("sections", {})
            page_text = sections.get("job_posting", detail_text)
            references = data.get("references", {})
    except (json.JSONDecodeError, AttributeError):
        pass  # Fall through to parse as plain text

    lines = page_text.strip().split("\n")

    # Title: format is always:
    #   Line 0: Company Name
    #   Line 1: Job Title  
    #   Line 2: Location · metadata
    meaningful_lines = []
    for line in lines:
        stripped = line.strip()
        if not stripped or len(stripped) < 2:
            continue
        if stripped.startswith('{') or stripped.startswith('http'):
            continue
        # Skip obvious metadata
        if any(skip in stripped.lower() for skip in ('·', 'clicked apply', 'promoted', 'set alert', 'easy apply', 'save', 'apply', 'use ai', 'get ai', 'show match', 'tailor my', 'help me', 'see how', 'access exclusive', 'activate premium', 'about the job', 'about the company', 'benefits found', 'overview', 'looking for talent', 'post a job', 'unlock hiring', 'responses managed')):
            continue
        meaningful_lines.append(stripped)

    # Line 0 = company, Line 1 = title (standard LinkedIn format)
    title = meaningful_lines[1] if len(meaningful_lines) > 1 else (meaningful_lines[0] if meaningful_lines else "")

    # Company: Line 0 of meaningful lines, or from references
    company = meaningful_lines[0] if len(meaningful_lines) > 1 else None
    if not company:
        job_postings = references.get("job_posting", [])
        for ref in job_postings:
            if ref.get("kind") == "company":
                company_text = ref.get("text", "")
                company = re.sub(r'\s+\d[\d,]*\s+followers?.*', '', company_text).strip()
                break

    # Location: extract from the header area of the posting
    location = None
    # Pattern: "Auckland, Auckland, New Zealand · 1 week ago"
    loc_match = re.search(
        r'((?:Auckland|Wellington|Christchurch|Hamilton|Tauranga|Dunedin|Palmerston North|New Zealand)(?:\s*,\s*(?:Auckland|Wellington|Christchurch|Hamilton|Tauranga|Dunedin|Palmerston North|New Zealand|Remote|Hybrid|On-site))*)\s*·',
        page_text, re.I
    )
    if loc_match:
        location = loc_match.group(1).strip()

    # Salary
    salary = None
    sal_match = re.search(r'\$[\d,]+(?:/hr)?\s*(?:-|to|–)\s*\$[\d,]+(?:/hr)?', page_text)
    if sal_match:
        salary = sal_match.group(0).strip()

    # Employment type
    employment = None
    for kw in ['Full-time', 'Part-time', 'Contract', 'Temporary', 'Internship']:
        if kw.lower() in page_text.lower()[:500]:
            employment = kw
            break

    return {
        "source": "LinkedIn",
        "sourceJobId": job_id,
        "title": title or "Unknown",
        "company": company,
        "location": location,
        "salaryText": salary,
        "descriptionText": page_text[:12000],
        "url": f"https://www.linkedin.com/jobs/view/{job_id}/",
        "postedDate": None,
        "closingDate": None,
    }


def search_and_collect(
    client: MCPClient,
    queries: list[str],
    location: str,
    max_jobs: int,
    delay: float = 1.0,
) -> list[dict[str, Any]]:
    """Run all search queries and collect job details."""
    all_job_ids: list[str] = []
    seen_ids: set[str] = set()

    print(f"Searching LinkedIn for {len(queries)} IT role queries in {location}...", file=sys.stderr)

    for i, query in enumerate(queries):
        print(f"  [{i+1}/{len(queries)}] '{query}'...", file=sys.stderr)
        try:
            result = client.call_tool("search_jobs", {
                "keywords": query,
                "location": location if location else None,
            })
            job_ids = extract_job_ids(result)
            new_ids = [j for j in job_ids if j not in seen_ids]
            print(f"    Found {len(new_ids)} new job IDs (total: {len(all_job_ids) + len(new_ids)})", file=sys.stderr)
            all_job_ids.extend(new_ids)
            seen_ids.update(new_ids)
        except Exception as e:
            print(f"    Error: {e}", file=sys.stderr)
        time.sleep(delay)

    # Limit and fetch details
    if len(all_job_ids) > max_jobs:
        all_job_ids = all_job_ids[:max_jobs]

    print(f"\nFetching details for {len(all_job_ids)} jobs...", file=sys.stderr)
    jobs: list[dict[str, Any]] = []

    for i, job_id in enumerate(all_job_ids):
        print(f"  [{i+1}/{len(all_job_ids)}] {job_id}...", file=sys.stderr)
        try:
            detail = client.call_tool("get_job_details", {"job_id": job_id})
            parsed = parse_job_detail(detail, job_id)
            if parsed:
                jobs.append(parsed)
                print(f"    -> '{parsed['title'][:60]}' @ {parsed.get('location','?')}", file=sys.stderr)
        except Exception as e:
            print(f"    Error: {e}", file=sys.stderr)
        time.sleep(delay)

    return jobs


def main() -> int:
    parser = argparse.ArgumentParser(description="Crawl LinkedIn IT jobs for NZ Job Market Dashboard")
    parser.add_argument("--keywords", type=str, help="Comma-separated search queries")
    parser.add_argument("--location", type=str, default="New Zealand", help="Job location")
    parser.add_argument("--max-jobs", type=int, default=50, help="Max jobs to collect")
    parser.add_argument("--delay", type=float, default=1.5, help="Delay between requests in seconds")
    parser.add_argument("--tool-timeout", type=int, default=300, help="MCP tool timeout")
    parser.add_argument("--output", type=str, help="Output JSON file path")
    parser.add_argument("--dry-run", action="store_true", help="Only search, don't fetch details")
    args = parser.parse_args()

    queries = [q.strip() for q in (args.keywords or "").split(",") if q.strip()]
    if not queries:
        queries = DEFAULT_QUERIES

    client = MCPClient(tool_timeout=args.tool_timeout)

    try:
        if args.dry_run:
            for query in queries:
                result = client.call_tool("search_jobs", {
                    "keywords": query,
                    "location": args.location,
                })
                ids = extract_job_ids(result)
                print(f"{query}: {len(ids)} jobs")
                for jid in ids[:5]:
                    print(f"  {jid}")
            return 0

        jobs = search_and_collect(
            client, queries, args.location, args.max_jobs, args.delay
        )

        output = {
            "source": "LinkedIn",
            "rawPages": [{
                "source": "LinkedIn",
                "url": f"https://www.linkedin.com/jobs/search/?keywords={queries[0]}&location={args.location}",
                "rawHtml": None,
                "markdown": None,
                "rawJson": json.dumps({"queries": queries, "location": args.location}),
                "crawledAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            }],
            "jobs": jobs,
            "isComplete": len(jobs) >= args.max_jobs,
        }

        if args.output:
            with open(args.output, "w") as f:
                json.dump(output, f, ensure_ascii=False, indent=2)
            print(f"\nSaved {len(jobs)} jobs to {args.output}", file=sys.stderr)
        else:
            print(json.dumps(output, ensure_ascii=False))

        return 0
    finally:
        client.close()


if __name__ == "__main__":
    raise SystemExit(main())