"""Round 16: SEEK SSR-JSON parser, recency filter and 429/5xx backoff."""
from __future__ import annotations

import json
import sys
import unittest
import urllib.error
from argparse import Namespace
from datetime import datetime, timezone
from email.message import Message
from pathlib import Path
from unittest import mock
from urllib.parse import urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from adapters.scrapling.scrapling_worker import (  # noqa: E402
    HttpStatusError,
    crawl,
    extract_embedded_json,
    fetch_with_http,
    http_request,
    parse_seek_listing,
)

FIXTURES = Path(__file__).parent / "fixtures"
SEEK_FIXTURE = FIXTURES / "seek_search_developer.html"
FIXED_NOW = datetime(2026, 10, 23, 12, 0, tzinfo=timezone.utc)
MARKER = "window.SEEK_REDUX_DATA"


def _redux_html(payload: dict) -> str:
    return (
        "<html><head><script>"
        f"{MARKER} = {json.dumps(payload)};"
        "</script></head><body></body></html>"
    )


def _job(job_id: str = "12345678", **overrides) -> dict:
    job = {
        "id": job_id,
        "title": "Senior Software Engineer",
        "companyName": "Acme Ltd",
        "locations": [{"label": "Wellington Central, Wellington"}],
        "listingDate": "2026-10-22T04:56:08.000Z",
        "teaser": "Build reliable Azure services.",
        "workTypes": ["Full time"],
        "displayType": "standard",
    }
    job.update(overrides)
    return job


def _payload(*jobs: dict) -> dict:
    return {"results": {"results": {"jobs": list(jobs)}}}


class _FakeResponse:
    def __init__(self, body: bytes = b"<html>ok</html>") -> None:
        self._body = body
        self.headers = Message()
        self.headers["Content-Encoding"] = ""
        self.headers["Content-Type"] = "text/html; charset=utf-8"

    def read(self) -> bytes:
        return self._body

    def __enter__(self) -> "_FakeResponse":
        return self

    def __exit__(self, *args: object) -> bool:
        return False


def _status_error(code: int) -> urllib.error.HTTPError:
    return urllib.error.HTTPError("https://example.com/page", code, "blocked", Message(), None)


class EmbeddedJsonExtractorTests(unittest.TestCase):
    def test_survives_closing_brace_inside_string_values(self) -> None:
        html = (
            '<script>window.SEEK_REDUX_DATA = '
            '{"a":"a } brace and \\" quote","nested":{"b":2}};'
            "</script>"
        )

        payload = extract_embedded_json(html, MARKER)

        self.assertEqual("a } brace and \" quote", payload["a"])
        self.assertEqual({"b": 2}, payload["nested"])

    def test_missing_marker_returns_none(self) -> None:
        self.assertIsNone(extract_embedded_json("<html>no payload</html>", MARKER))

    def test_unbalanced_payload_returns_none_without_crashing(self) -> None:
        html = '<script>window.SEEK_REDUX_DATA = {"a":{"b":1}</script>'

        self.assertIsNone(extract_embedded_json(html, MARKER))

    def test_invalid_json_after_balanced_scan_returns_none(self) -> None:
        html = '<script>window.SEEK_REDUX_DATA = {"a": nope};</script>'

        self.assertIsNone(extract_embedded_json(html, MARKER))


class SeekReduxParserTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.fixture_html = SEEK_FIXTURE.read_text(encoding="utf-8", errors="replace")
        cls.fixture_payload = extract_embedded_json(cls.fixture_html, MARKER)

    def test_fixture_extracts_unique_jobs_with_company_and_iso_date(self) -> None:
        raw_jobs = self.fixture_payload["results"]["results"]["jobs"]
        expected_companies = sum(
            1
            for job in raw_jobs
            if job.get("companyName") or (job.get("advertiser") or {}).get("description")
        )

        jobs = parse_seek_listing(
            "SEEK NZ",
            "https://nz.seek.com/information-technology-jobs?keywords=developer&daterange=31",
            self.fixture_html,
            "",
            now=FIXED_NOW,
        )

        self.assertGreater(len(jobs), 0)
        self.assertEqual(len({job.sourceJobId for job in jobs}), len(jobs))
        self.assertEqual(expected_companies, sum(1 for job in jobs if job.company))
        for job in jobs:
            self.assertRegex(job.postedDate or "", r"^\d{4}-\d{2}-\d{2}$")
            self.assertRegex(job.url, r"^https://www\.seek\.co\.nz/job/\d+$")
            self.assertTrue(job.location)
            if "," in job.location:
                self.assertIn(" ", job.location)

    def test_company_falls_back_to_advertiser_description(self) -> None:
        payload = _payload(
            _job(companyName=None, advertiser={"description": "Advertiser Name Ltd"})
        )

        jobs = parse_seek_listing(
            "SEEK NZ", "https://nz.seek.com/jobs?keywords=x", _redux_html(payload), "", now=FIXED_NOW
        )

        self.assertEqual(["Advertiser Name Ltd"], [job.company for job in jobs])

    def test_promoted_and_organic_duplicate_ids_are_deduped(self) -> None:
        payload = _payload(
            _job("99999999", displayType="promoted"),
            _job("99999999", title="Same job organic", displayType="standard"),
            _job("88888888"),
        )

        jobs = parse_seek_listing(
            "SEEK NZ", "https://nz.seek.com/jobs?keywords=x", _redux_html(payload), "", now=FIXED_NOW
        )

        self.assertEqual(["99999999", "88888888"], [job.sourceJobId for job in jobs])
        self.assertEqual("Senior Software Engineer", jobs[0].title)

    def test_client_side_recency_filter_drops_jobs_older_than_31_days(self) -> None:
        payload = _payload(
            _job("11111111", listingDate="2026-09-01T00:00:00.000Z"),
            _job("22222222", listingDate="2026-10-01T00:00:00.000Z"),
        )

        jobs = parse_seek_listing(
            "SEEK NZ", "https://nz.seek.com/jobs?keywords=x", _redux_html(payload), "", now=FIXED_NOW
        )

        self.assertEqual(["22222222"], [job.sourceJobId for job in jobs])

    def test_payload_without_company_or_advertiser_stays_null(self) -> None:
        payload = _payload(_job(companyName=None, advertiser=None))

        jobs = parse_seek_listing(
            "SEEK NZ", "https://nz.seek.com/jobs?keywords=x", _redux_html(payload), "", now=FIXED_NOW
        )

        self.assertIsNone(jobs[0].company)

    def test_200_without_marker_or_cards_raises(self) -> None:
        with self.assertRaises(ValueError):
            parse_seek_listing(
                "SEEK NZ",
                "https://nz.seek.com/jobs?keywords=x",
                "<html><body>Just a moment...</body></html>",
                "",
                now=FIXED_NOW,
            )

    def test_marker_without_jobs_array_raises(self) -> None:
        with self.assertRaises(ValueError):
            parse_seek_listing(
                "SEEK NZ",
                "https://nz.seek.com/jobs?keywords=x",
                _redux_html({"results": {"results": {}}}),
                "",
                now=FIXED_NOW,
            )


class SeekRobotsUrlTests(unittest.TestCase):
    def test_every_configured_listing_url_starts_query_with_keywords(self) -> None:
        registry = json.loads(
            (Path(__file__).resolve().parents[1] / "sources" / "sites.json").read_text(
                encoding="utf-8"
            )
        )
        seek = next(site for site in registry["sites"] if site["name"] == "SEEK NZ")
        listing_urls = seek["listingUrls"]

        self.assertGreater(len(listing_urls), 0)
        for url in listing_urls:
            parsed = urlsplit(url)
            self.assertTrue(parsed.query.startswith("keywords="), url)
            self.assertIn("daterange=31", url)

        # SEEK's redirect drops ?keywords, so the crawl fetches these pinned
        # 200 canonical paths directly. No query means robots' Disallow: *?
        # does not apply, and no disallowed redirect target is ever fetched.
        canonical_urls = seek["canonicalListingUrls"]
        self.assertEqual(len(listing_urls), len(canonical_urls))
        for url in canonical_urls:
            parsed = urlsplit(url)
            self.assertEqual("", parsed.query, url)
            self.assertTrue(url.startswith("https://nz.seek.com/information-technology-"), url)


class SeekCrawlResilienceTests(unittest.TestCase):
    CARD = """
    <article data-automation="normalJob">
      <a href="/job/95156028">View</a>
      <span data-automation="jobTitle">Senior Software Engineer</span>
      <span data-automation="jobCompany">Acme Ltd</span>
      <span data-automation="jobLocation">Auckland</span>
      <span data-automation="jobListingDate">Listed six days ago</span>
      <span data-automation="jobShortDescription">Build things.</span>
    </article>
    """

    def _args(self) -> Namespace:
        return Namespace(
            source="SEEK NZ",
            url="https://nz.seek.com/information-technology-jobs",
            parser="seek",
            fetcher_type="http",
            max_pages=50,
            delay_seconds=0,
            robots_txt_obey=False,
            proxy_url="",
            user_agent="test",
        )

    def test_one_rate_limited_page_does_not_fail_the_whole_crawl(self) -> None:
        calls: list[str] = []

        def fetch(url: str) -> tuple[str, str]:
            calls.append(url)
            if "data-jobs" in url:
                raise HttpStatusError(429, url)
            return self.CARD, "cards"

        result = crawl(self._args(), fetch=fetch, sleeper=lambda _: None)

        self.assertEqual(12, len(calls))
        self.assertGreater(len(result["jobs"]), 0)
        self.assertFalse(result["isComplete"], "a partial sweep must not reconcile as complete")

    def test_all_pages_rate_limited_raises_instead_of_returning_zero(self) -> None:
        def fetch(url: str) -> tuple[str, str]:
            raise HttpStatusError(429, url)

        with self.assertRaises(RuntimeError):
            crawl(self._args(), fetch=fetch, sleeper=lambda _: None)


class SeekBackoffTests(unittest.TestCase):
    def test_429_then_200_retries_and_succeeds(self) -> None:
        attempts = {"count": 0}
        sleeps: list[float] = []

        def fake_open(request, timeout=None):
            attempts["count"] += 1
            if attempts["count"] == 1:
                raise _status_error(429)
            return _FakeResponse()

        with mock.patch("urllib.request.OpenerDirector.open", side_effect=fake_open):
            with mock.patch("time.sleep", side_effect=lambda seconds: sleeps.append(seconds)):
                body = http_request(
                    "https://example.com/page",
                    retries=6,
                    backoff_base_ms=500,
                    backoff_max_ms=8000,
                    jitter_ms=0,
                    retry_on_5xx=True,
                )

        self.assertIn("ok", body)
        self.assertEqual(2, attempts["count"])
        self.assertEqual([0.5], sleeps)

    def test_always_429_raises_after_bounded_retries(self) -> None:
        attempts = {"count": 0}
        sleeps: list[float] = []

        def fake_open(request, timeout=None):
            attempts["count"] += 1
            raise _status_error(429)

        with mock.patch("urllib.request.OpenerDirector.open", side_effect=fake_open):
            with mock.patch("time.sleep", side_effect=lambda seconds: sleeps.append(seconds)):
                with self.assertRaises(RuntimeError):
                    http_request(
                        "https://example.com/page",
                        retries=6,
                        backoff_base_ms=500,
                        backoff_max_ms=8000,
                        jitter_ms=0,
                        retry_on_5xx=True,
                    )

        self.assertEqual(7, attempts["count"])
        self.assertEqual([0.5, 1.0, 2.0, 4.0, 8.0, 8.0], sleeps)

    def test_500_is_retried_only_when_the_source_opts_in(self) -> None:
        attempts = {"count": 0}

        def fake_open(request, timeout=None):
            attempts["count"] += 1
            if attempts["count"] == 1:
                raise _status_error(500)
            return _FakeResponse()

        with mock.patch("urllib.request.OpenerDirector.open", side_effect=fake_open):
            with mock.patch("time.sleep"):
                body = http_request(
                    "https://example.com/page",
                    retries=2,
                    backoff_base_ms=500,
                    jitter_ms=0,
                    retry_on_5xx=True,
                )

        self.assertIn("ok", body)
        self.assertEqual(2, attempts["count"])

    def test_seek_404_is_an_empty_page(self) -> None:
        from argparse import Namespace

        args = Namespace(source="SEEK NZ", user_agent="test", fetch_timeout=10, robots_txt_obey=False)
        with mock.patch(
            "adapters.scrapling.scrapling_worker.lookup_source",
            return_value={"notFoundEmpty": True, "httpRetries": 6},
        ):
            with mock.patch(
                "adapters.scrapling.scrapling_worker.http_request",
                side_effect=HttpStatusError(404, "https://nz.seek.com/jobs?keywords=x"),
            ):
                html, text = fetch_with_http(args, "https://nz.seek.com/jobs?keywords=x")

        self.assertEqual(("", ""), (html, text))


if __name__ == "__main__":
    unittest.main()
