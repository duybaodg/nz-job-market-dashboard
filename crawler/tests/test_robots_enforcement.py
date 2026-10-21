"""Round 15 robots.txt enforcement regressions.

These cover the four bypasses the code review found:
* sitemap discovery fetched XML without a robots check;
* an unavailable robots endpoint (5xx/timeout) used to mean "allow";
* a platform ATS client checked the marketing URL, not the API host it fetches;
* an HTTP redirect was followed without re-checking robots on the target.
"""
import sys
import unittest
import urllib.error
from argparse import Namespace
from email.message import Message
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import adapters.scrapling.scrapling_worker as worker  # noqa: E402


def base_args(**overrides):
    values = dict(
        source="Example",
        url="https://example.com/careers",
        parser="generic",
        fetcher_type="http",
        fetcher_override="",
        max_pages=4,
        delay_seconds=0,
        robots_txt_obey=True,
        proxy_url="",
        user_agent="test",
    )
    values.update(overrides)
    return Namespace(**values)


class RobotsEnforcementTests(unittest.TestCase):
    def setUp(self) -> None:
        worker.ROBOTS_CACHE.clear()

    def test_sitemap_discovery_never_fetches_a_robots_disallowed_sitemap(self) -> None:
        fetched: list[str] = []

        def fetch(url: str) -> tuple[str, str]:
            fetched.append(url)
            return "<urlset><url><loc>https://example.com/jobs/1</loc></url></urlset>", ""

        with mock.patch.object(
            worker,
            "obeys_robots",
            side_effect=lambda url, user_agent: "sitemap" not in url.lower(),
        ):
            found = worker.discover_sitemap_job_urls(
                "https://example.com/careers/", fetch, args=base_args()
            )

        self.assertEqual([], found)
        self.assertEqual([], fetched, "no sitemap URL may be fetched when robots disallows it")

    def test_unavailable_robots_endpoint_fails_closed_and_caches_the_deny(self) -> None:
        with mock.patch.object(
            worker,
            "http_request",
            side_effect=worker.HttpStatusError(500, "https://example.com/robots.txt"),
        ) as request:
            self.assertFalse(worker.obeys_robots("https://example.com/jobs/1", "test"))
            self.assertFalse(worker.obeys_robots("https://example.com/jobs/2", "test"))

        self.assertEqual(1, request.call_count, "the deny is cached for the short TTL")
        self.assertIsNone(worker.ROBOTS_CACHE["https://example.com"][1])

    def test_robots_timeout_fails_closed(self) -> None:
        with mock.patch.object(worker, "http_request", side_effect=TimeoutError("timed out")):
            self.assertFalse(worker.obeys_robots("https://example.com/jobs/1", "test"))

    def test_missing_robots_file_remains_an_allow(self) -> None:
        with mock.patch.object(
            worker,
            "http_request",
            side_effect=worker.HttpStatusError(404, "https://example.com/robots.txt"),
        ):
            self.assertTrue(worker.obeys_robots("https://example.com/jobs/1", "test"))

    def test_redirect_rechecks_robots_on_the_resolved_url(self) -> None:
        headers = Message()
        headers["Location"] = "/blocked/job"
        redirect = urllib.error.HTTPError(
            "https://example.com/jobs/1", 302, "Found", headers, None
        )

        with mock.patch("urllib.request.OpenerDirector.open", side_effect=redirect):
            with mock.patch.object(
                worker,
                "obeys_robots",
                side_effect=lambda url, user_agent: "/blocked" not in url,
            ):
                with self.assertRaises(RuntimeError) as raised:
                    worker.http_request(
                        "https://example.com/jobs/1", robots_obey=True, user_agent="test"
                    )

        self.assertIn("redirect target", str(raised.exception))

    def test_cross_site_redirect_is_refused(self) -> None:
        headers = Message()
        headers["Location"] = "https://evil.example/job"
        redirect = urllib.error.HTTPError(
            "https://example.com/jobs/1", 302, "Found", headers, None
        )

        with mock.patch("urllib.request.OpenerDirector.open", side_effect=redirect):
            with self.assertRaises(RuntimeError) as raised:
                worker.http_request(
                    "https://example.com/jobs/1", robots_obey=True, user_agent="test"
                )

        self.assertIn("cross-site redirect", str(raised.exception))

    def test_platform_api_checks_the_actual_api_host_not_the_marketing_url(self) -> None:
        calls: list[str] = []

        def api_fetch(url, method="GET", payload=None):
            calls.append(url)
            return {"result": []}

        args = base_args(
            source="Jade",
            url="https://www.jadeworld.com/careers",
            parser="bamboohr",
        )

        def obeys(url: str, user_agent: str) -> bool:
            # The marketing host is allowed; the BambooHR API host is not.
            return "jadeworld.com/careers" in url

        with mock.patch.object(worker, "lookup_source", return_value={"platform": "bamboohr", "token": "jade"}):
            with mock.patch.object(worker, "obeys_robots", side_effect=obeys):
                with self.assertRaises(RuntimeError) as raised:
                    worker.crawl(args, fetch=lambda _: ("", ""), api_fetch=api_fetch)

        self.assertIn("Robots.txt", str(raised.exception))
        self.assertEqual([], calls, "the API host must be checked before the request")


if __name__ == "__main__":
    unittest.main()
