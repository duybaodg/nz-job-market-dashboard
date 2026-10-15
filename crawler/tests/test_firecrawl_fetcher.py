"""Tests for the optional Firecrawl fetch layer and its automatic fallback.

Firecrawl is only a *fetch* backend: the worker still discovers links, follows
detail pages and enforces robots.txt with the shared :mod:`robots` matcher.
These tests keep that contract honest without touching the network (except for
one loopback server that verifies the scrape request shape).
"""
import json
import os
import sys
import threading
import unittest
from argparse import Namespace
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import adapters.scrapling.scrapling_worker as worker  # noqa: E402


def base_args(**overrides):
    values = dict(
        source="Generic",
        url="https://example.com/jobs",
        parser="generic",
        fetcher_type="dynamic",
        max_pages=10,
        delay_seconds=0,
        robots_txt_obey=False,
        proxy_url="",
        user_agent="test",
        fetch_timeout=30,
        firecrawl_timeout=60,
    )
    values.update(overrides)
    return Namespace(**values)


class _ScrapeHandler(BaseHTTPRequestHandler):
    def do_POST(self) -> None:  # noqa: N802 - stdlib naming
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length)
        self.server.requests.append(  # type: ignore[attr-defined]
            (self.path, self.headers.get("Authorization"), json.loads(body))
        )
        payload = {
            "success": True,
            "data": {"markdown": "# Job\n[Dev](https://x/1)", "rawHtml": "<html>job</html>"},
        }
        encoded = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def log_message(self, *args) -> None:  # noqa: D401 - silence the test server
        pass


class FirecrawlFetcherTests(unittest.TestCase):
    def test_firecrawl_scrape_posts_to_versioned_endpoint_with_bearer(self) -> None:
        server = ThreadingHTTPServer(("127.0.0.1", 0), _ScrapeHandler)
        server.requests = []  # type: ignore[attr-defined]
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            base = f"http://127.0.0.1:{server.server_address[1]}/v2"
            with mock.patch.dict(
                os.environ,
                {"FIRECRAWL_BASE_URL": base, "FIRECRAWL_API_KEY": "test-key"},
                clear=False,
            ):
                data = worker.firecrawl_scrape("https://example.com/jobs/1", timeout=5)
        finally:
            server.shutdown()
            server.server_close()

        self.assertEqual("# Job\n[Dev](https://x/1)", data["markdown"])
        path, authorization, payload = server.requests[0]  # type: ignore[attr-defined]
        self.assertEqual("/v2/scrape", path)
        self.assertEqual("Bearer test-key", authorization)
        self.assertEqual("https://example.com/jobs/1", payload["url"])
        self.assertEqual(["markdown", "html", "rawHtml"], payload["formats"])

    def test_fetch_with_firecrawl_enforces_robots_before_scraping(self) -> None:
        with mock.patch.object(worker, "obeys_robots", return_value=False), mock.patch.object(
            worker, "firecrawl_scrape"
        ) as scrape:
            with self.assertRaises(RuntimeError) as raised:
                worker.fetch_with_firecrawl(base_args(robots_txt_obey=True), "https://example.com/job/1")

        self.assertIn("Robots.txt does not allow", str(raised.exception))
        scrape.assert_not_called()

    def test_fetch_with_firecrawl_returns_raw_html_and_markdown(self) -> None:
        with mock.patch.object(
            worker,
            "firecrawl_scrape",
            return_value={"markdown": "listing text", "rawHtml": "<html>listing</html>"},
        ) as scrape:
            html, text = worker.fetch_with_firecrawl(base_args(), "https://example.com/jobs")

        self.assertEqual("<html>listing</html>", html)
        self.assertEqual("listing text", text)
        scrape.assert_called_once()

    def test_fetch_with_firecrawl_raises_when_no_content(self) -> None:
        with mock.patch.object(worker, "firecrawl_scrape", return_value={"markdown": "", "rawHtml": ""}):
            with self.assertRaises(RuntimeError):
                worker.fetch_with_firecrawl(base_args(), "https://example.com/jobs")

    def test_build_fetcher_uses_firecrawl_when_selected(self) -> None:
        calls: list[str] = []

        def fake(args, url):
            calls.append(url)
            return "<html></html>", "text"

        with mock.patch.object(worker, "fetch_with_firecrawl", side_effect=fake):
            fetch = worker.build_fetcher(base_args(), "firecrawl")
            fetch("https://example.com/jobs/1")

        self.assertEqual(["https://example.com/jobs/1"], calls)

    def test_build_fetcher_falls_back_to_firecrawl_when_browser_render_fails(self) -> None:
        calls: list[tuple[str, str]] = []

        def broken_scrapling(args, url, fetcher_type=None):
            raise RuntimeError("navigation timeout")

        def firecrawl(args, url):
            calls.append(("firecrawl", url))
            return "<html>rendered by firecrawl</html>", "rendered"

        with mock.patch.object(worker, "fetch_with_scrapling", side_effect=broken_scrapling), mock.patch.object(
            worker, "fetch_with_firecrawl", side_effect=firecrawl
        ), mock.patch.object(worker, "firecrawl_config", return_value=("http://firecrawl.test/v2", "key")):
            fetch = worker.build_fetcher(base_args(), "dynamic")
            html, _ = fetch("https://example.com/jobs/1")

        self.assertEqual("<html>rendered by firecrawl</html>", html)
        self.assertEqual([("firecrawl", "https://example.com/jobs/1")], calls)

    def test_build_fetcher_does_not_fall_back_when_firecrawl_is_unconfigured(self) -> None:
        with mock.patch.object(
            worker, "fetch_with_scrapling", side_effect=RuntimeError("navigation timeout")
        ) as scrapling, mock.patch.object(worker, "firecrawl_config", return_value=None), mock.patch.object(
            worker, "fetch_with_firecrawl"
        ) as firecrawl:
            fetch = worker.build_fetcher(base_args(), "dynamic")
            with self.assertRaises(RuntimeError):
                fetch("https://example.com/jobs/1")

        scrapling.assert_called_once()
        firecrawl.assert_not_called()


if __name__ == "__main__":
    unittest.main()
