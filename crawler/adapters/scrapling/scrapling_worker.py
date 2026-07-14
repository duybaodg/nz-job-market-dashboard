#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from html import unescape
from html.parser import HTMLParser
from typing import Any
from urllib.parse import urljoin
from urllib.robotparser import RobotFileParser


JOB_KEYWORDS = (
    "developer",
    "engineer",
    "analyst",
    "architect",
    "consultant",
    "administrator",
    "manager",
    "designer",
    "specialist",
    "graduate",
    "intern",
    "technician",
)

SKIP_LINK_WORDS = (
    "privacy",
    "terms",
    "login",
    "sign in",
    "register",
    "saved",
    "contact",
    "about",
)


@dataclass(frozen=True)
class RawJobInput:
    source: str
    sourceJobId: str | None
    title: str
    company: str | None
    location: str | None
    salaryText: str | None
    descriptionText: str | None
    url: str
    postedDate: str | None
    closingDate: str | None


@dataclass(frozen=True)
class RawJobPageInput:
    source: str
    url: str
    rawHtml: str | None
    markdown: str | None
    rawJson: str | None
    crawledAt: str


class LinkParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.links: list[tuple[str, str]] = []
        self._href_stack: list[str | None] = []
        self._text_parts: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag != "a":
            return

        href = dict(attrs).get("href")
        self._href_stack.append(href)
        self._text_parts = []

    def handle_data(self, data: str) -> None:
        if self._href_stack:
            self._text_parts.append(data)

    def handle_endtag(self, tag: str) -> None:
        if tag != "a" or not self._href_stack:
            return

        href = self._href_stack.pop()
        text = clean_text(" ".join(self._text_parts))
        if href and text:
            self.links.append((text, href))
        self._text_parts = []


def clean_text(value: str | None) -> str:
    if not value:
        return ""

    value = re.sub(r"<script\b[^<]*(?:(?!</script>)<[^<]*)*</script>", " ", value, flags=re.I)
    value = re.sub(r"<style\b[^<]*(?:(?!</style>)<[^<]*)*</style>", " ", value, flags=re.I)
    value = re.sub(r"<[^>]+>", " ", value)
    value = unescape(value)
    return re.sub(r"\s+", " ", value).strip(" -*#\t\r\n")


def extract_links(html: str, base_url: str) -> list[tuple[str, str]]:
    parser = LinkParser()
    parser.feed(html)
    return [(text, urljoin(base_url, href)) for text, href in parser.links]


def looks_like_job_title(title: str) -> bool:
    lowered = title.lower()
    return any(keyword in lowered for keyword in JOB_KEYWORDS) and not any(
        word in lowered for word in SKIP_LINK_WORDS
    )


def extract_label(text: str, label: str) -> str | None:
    match = re.search(rf"(?:\*\*)?{label}(?:\*\*)?\s*:\s*([^\n\r|]+)", text, re.I)
    return clean_text(match.group(1)) if match else None


def extract_salary(text: str) -> str | None:
    labelled = extract_label(text, "Salary")
    if labelled:
        return labelled

    match = re.search(
        r"(\$?\s*\d{2,3}(?:,\d{3}|k)?\s*(?:-|to|–)\s*\$?\s*\d{2,3}(?:,\d{3}|k)?)",
        text,
        re.I,
    )
    return clean_text(match.group(1)) if match else None


def extract_title(html: str, text: str) -> str:
    h1 = re.search(r"<h1[^>]*>(.*?)</h1>", html, re.I | re.S)
    if h1:
        title = clean_text(h1.group(1))
        if title:
            return title

    title = re.search(r"<title[^>]*>(.*?)</title>", html, re.I | re.S)
    if title:
        value = clean_text(title.group(1))
        if value:
            return value

    return text[:80] if text else "Untitled job"


def parse_jobs(source: str, url: str, html: str, text: str) -> list[RawJobInput]:
    salary = extract_salary(text)
    location = extract_label(text, "Location") or extract_label(text, "Region")
    company = extract_label(text, "Company") or extract_label(text, "Employer")
    jobs: list[RawJobInput] = []
    seen_urls: set[str] = set()

    for title, link_url in extract_links(html, url):
        if not looks_like_job_title(title) or link_url in seen_urls:
            continue

        seen_urls.add(link_url)
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=None,
                title=title,
                company=company,
                location=location,
                salaryText=salary,
                descriptionText=text[:1200] or None,
                url=link_url,
                postedDate=None,
                closingDate=None,
            )
        )

    if jobs:
        return jobs

    return [
        RawJobInput(
            source=source,
            sourceJobId=None,
            title=extract_title(html, text),
            company=company,
            location=location,
            salaryText=salary,
            descriptionText=text[:1200] or None,
            url=url,
            postedDate=None,
            closingDate=None,
        )
    ]


def obeys_robots(url: str, user_agent: str) -> bool:
    match = re.match(r"^(https?://[^/]+)", url)
    if not match:
        return False

    parser = RobotFileParser()
    parser.set_url(urljoin(match.group(1), "/robots.txt"))
    parser.read()
    return parser.can_fetch(user_agent, url)


def fetch_with_scrapling(args: argparse.Namespace) -> tuple[str, str]:
    try:
        if args.fetcher_type == "dynamic":
            from scrapling.fetchers import DynamicFetcher as Fetcher
        elif args.fetcher_type == "stealth":
            from scrapling.fetchers import StealthyFetcher as Fetcher
        else:
            from scrapling.fetchers import Fetcher
    except Exception as exc:  # pragma: no cover - depends on local Python env
        raise RuntimeError(
            "Scrapling is not installed. Run `python -m pip install -e crawler` or install crawler dependencies."
        ) from exc

    fetch_kwargs: dict[str, Any] = {"url": args.url}
    if args.proxy_url:
        fetch_kwargs["proxy"] = args.proxy_url

    page = Fetcher.get(**fetch_kwargs)
    html = getattr(page, "html", None) or getattr(page, "body", None) or str(page)
    if isinstance(html, bytes):
        html = html.decode("utf-8", errors="replace")

    text = getattr(page, "text", None)
    if not isinstance(text, str) or not text.strip():
        text = clean_text(html)

    return html, text


def build_result(args: argparse.Namespace, html: str, text: str) -> dict[str, Any]:
    raw_page = RawJobPageInput(
        source=args.source,
        url=args.url,
        rawHtml=html,
        markdown=text,
        rawJson=json.dumps(
            {
                "fetcherType": args.fetcher_type,
                "robotsTxtObey": args.robots_txt_obey,
                "proxyConfigured": bool(args.proxy_url),
            }
        ),
        crawledAt=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    )

    return {
        "rawPage": asdict(raw_page),
        "jobs": [asdict(job) for job in parse_jobs(args.source, args.url, html, text)],
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Fetch and parse job pages with Scrapling.")
    parser.add_argument("--source", default="Scrapling")
    parser.add_argument("--url", required=True)
    parser.add_argument("--fetcher-type", choices=("fetcher", "dynamic", "stealth"), default="fetcher")
    parser.add_argument("--robots-txt-obey", action="store_true")
    parser.add_argument("--proxy-url", default="")
    parser.add_argument("--user-agent", default="NZJobMarketDashboardBot/0.1")
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    if args.robots_txt_obey and not obeys_robots(args.url, args.user_agent):
        print(f"Robots.txt does not allow crawling {args.url}", file=sys.stderr)
        return 2

    try:
        html, text = fetch_with_scrapling(args)
        print(json.dumps(build_result(args, html, text), ensure_ascii=False))
        return 0
    except Exception as exc:  # pragma: no cover - defensive CLI boundary
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
