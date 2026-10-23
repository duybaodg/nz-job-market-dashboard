"""Offline tests for the MANUAL-ONLY LinkedIn guest-endpoint CLI (Round 16)."""
from __future__ import annotations

import io
import json
import sys
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from adapters.linkedin import linkedin_guest_crawler as guest  # noqa: E402

SEARCH_HTML = """
<ul>
<li>
  <div class="base-card" data-entity-urn="urn:li:jobPosting:4000000001">
    <a class="base-card__full-link" href="https://www.linkedin.com/jobs/view/4000000001/?refId=abc&amp;trackingId=xyz">View</a>
    <h3 class="base-search-card__title">Senior &amp; Lead Developer</h3>
    <h4 class="base-search-card__subtitle"><a href="https://www.linkedin.com/company/acme?trk=x">Acme &#x27;Cloud&#x27; Ltd</a></h4>
    <span class="job-search-card__location">Wellington &amp; Region</span>
    <time class="job-search-card__listdate" datetime="2026-10-20">2 days ago</time>
  </div>
</li>
<li>
  <div class="base-card" data-entity-urn="urn:li:jobPosting:4000000002">
    <div>malformed card with no title</div>
  </div>
</li>
<li>
  <div class="base-card" data-entity-urn="urn:li:jobPosting:4000000003">
    <a class="base-card__full-link" href="/jobs/view/4000000003/">View</a>
    <h3 class="base-search-card__title">Data Engineer &#39;Azure&#39;</h3>
    <h4 class="base-search-card__subtitle">Contoso</h4>
    <span class="job-search-card__location">Auckland, New Zealand</span>
    <time class="job-search-card__listdate--new" datetime="2026-10-22">Today</time>
  </div>
</li>
</ul>
"""

DETAIL_HTML = """
<html><body>
<div class="top-card-layout">
<h1 class="top-card-layout__title">Senior &amp; Lead Developer</h1>
<a class="topcard__org-name-link" href="https://www.linkedin.com/company/acme">Acme &#x27;Cloud&#x27; Ltd</a>
<span class="topcard__flavor--bullet">Auckland, New Zealand</span>
<time class="job-search-card__listdate" datetime="2026-10-20"></time>
</div>
<h3 class="description__job-criteria-subheader">Seniority level</h3>
<span class="description__job-criteria-text">Mid-Senior level</span>
<h3 class="description__job-criteria-subheader">Employment type</h3>
<span class="description__job-criteria-text">Full-time</span>
<div class="show-more-less-html__markup">
<p>Build &amp; ship services.</p>
<br>
<p>No longer accepting applications in our team? That is recruiter boilerplate.</p>
</div>
</body></html>
"""

CLOSED_DETAIL_HTML = DETAIL_HTML.replace(
    '<div class="top-card-layout">',
    '<div class="closed-job__flavor">No longer accepting applications</div>'
    '<div class="top-card-layout">',
)


class _FakeResponse:
    status = 200

    def __init__(self, body: bytes = b"<html>ok</html>", status: int = 200) -> None:
        self._body = body
        self.status = status

    def read(self) -> bytes:
        return self._body

    def __enter__(self) -> "_FakeResponse":
        return self

    def __exit__(self, *args: object) -> bool:
        return False


def _http_error(code: int):
    import urllib.error

    return urllib.error.HTTPError("https://www.linkedin.com/x", code, "blocked", None, None)


class SearchParserTests(unittest.TestCase):
    def test_chunk_isolation_keeps_neighbours_when_one_card_is_malformed(self) -> None:
        cards = guest.parse_search_cards(SEARCH_HTML)

        self.assertEqual(["4000000001", "4000000003"], [card["id"] for card in cards])

    def test_named_and_numeric_entities_are_decoded(self) -> None:
        first, second = guest.parse_search_cards(SEARCH_HTML)

        self.assertEqual("Senior & Lead Developer", first["title"])
        self.assertEqual("Acme 'Cloud' Ltd", first["company"])
        self.assertEqual("Wellington & Region", first["location"])
        self.assertEqual("Data Engineer 'Azure'", second["title"])

    def test_url_query_is_stripped_and_relative_urls_are_absolutised(self) -> None:
        first, second = guest.parse_search_cards(SEARCH_HTML)

        self.assertEqual("https://www.linkedin.com/jobs/view/4000000001/", first["url"])
        self.assertEqual("https://www.linkedin.com/jobs/view/4000000003/", second["url"])
        self.assertEqual("https://www.linkedin.com/company/acme", first["companyUrl"])

    def test_dates_come_from_the_datetime_attribute(self) -> None:
        first, second = guest.parse_search_cards(SEARCH_HTML)

        self.assertEqual("2026-10-20", first["date"])
        self.assertEqual("2026-10-22", second["date"])

    def test_empty_fragment_returns_no_cards(self) -> None:
        self.assertEqual([], guest.parse_search_cards(""))


class DetailParserTests(unittest.TestCase):
    def test_description_and_criteria_are_parsed(self) -> None:
        job, is_active, criteria = guest.parse_detail(DETAIL_HTML, "4000000001")

        self.assertEqual("Senior & Lead Developer", job["title"])
        self.assertEqual("Acme 'Cloud' Ltd", job["company"])
        self.assertEqual("Auckland, New Zealand", job["location"])
        self.assertIn("Build & ship services.", job["descriptionText"])
        self.assertEqual("Mid-Senior level", criteria["seniority"])
        self.assertEqual("Full-time", criteria["employmentType"])
        self.assertTrue(is_active)

    def test_isactive_ignores_closed_phrase_inside_description(self) -> None:
        _, is_active, _ = guest.parse_detail(DETAIL_HTML, "4000000001")

        self.assertTrue(is_active)

    def test_isactive_detects_closed_banner_above_description(self) -> None:
        _, is_active, _ = guest.parse_detail(CLOSED_DETAIL_HTML, "4000000001")

        self.assertFalse(is_active)

    def test_job_id_extraction_accepts_id_url_and_urn(self) -> None:
        self.assertEqual("4252026496", guest.extract_job_id("4252026496"))
        self.assertEqual(
            "4252026496",
            guest.extract_job_id("https://www.linkedin.com/jobs/view/4252026496/?trk=x"),
        )
        self.assertEqual("4252026496", guest.extract_job_id("urn:li:jobPosting:4252026496"))

    def test_job_id_extraction_rejects_garbage(self) -> None:
        with self.assertRaises(guest.CliError) as context:
            guest.extract_job_id("not-a-job")

        self.assertEqual("NO_ID", context.exception.code)


class BackoffTests(unittest.TestCase):
    def test_429_then_200_retries_and_succeeds(self) -> None:
        attempts = {"count": 0}
        sleeps: list[float] = []

        def fake_urlopen(request, timeout=None):
            attempts["count"] += 1
            if attempts["count"] == 1:
                raise _http_error(429)
            return _FakeResponse()

        with mock.patch.object(guest, "urlopen", side_effect=fake_urlopen):
            body = guest.html_fetch(
                "https://www.linkedin.com/x",
                sleeper=lambda seconds: sleeps.append(seconds),
                jitter=lambda: 0.0,
            )

        self.assertIn("ok", body)
        self.assertEqual(2, attempts["count"])
        self.assertEqual([0.5], sleeps)

    def test_always_429_raises_after_bounded_retries(self) -> None:
        attempts = {"count": 0}
        sleeps: list[float] = []

        def fake_urlopen(request, timeout=None):
            attempts["count"] += 1
            raise _http_error(429)

        with mock.patch.object(guest, "urlopen", side_effect=fake_urlopen):
            with self.assertRaises(guest.FetchError):
                guest.html_fetch(
                    "https://www.linkedin.com/x",
                    sleeper=lambda seconds: sleeps.append(seconds),
                    jitter=lambda: 0.0,
                )

        self.assertEqual(7, attempts["count"])
        self.assertEqual([0.5, 1.0, 2.0, 4.0, 8.0, 8.0], sleeps)

    def test_404_returns_empty(self) -> None:
        with mock.patch.object(guest, "urlopen", side_effect=_http_error(404)):
            self.assertEqual("", guest.html_fetch("https://www.linkedin.com/x"))


class CliContractTests(unittest.TestCase):
    def _run(self, argv: list[str]) -> tuple[int, dict]:
        stderr = io.StringIO()
        with redirect_stderr(stderr):
            code = guest.main(argv)
        return code, json.loads(stderr.getvalue())

    def test_unknown_flag_exits_one_with_json_error(self) -> None:
        code, error = self._run(["--bogus"])

        self.assertEqual(1, code)
        self.assertEqual("UNKNOWN_FLAG", error["code"])
        self.assertIn("error", error)

    def test_bad_jobage_exits_one_with_bad_arg(self) -> None:
        code, error = self._run(["--location", "Wellington", "--jobage", "1.5"])

        self.assertEqual(1, code)
        self.assertEqual("BAD_ARG", error["code"])

    def test_missing_location_exits_one_with_no_location(self) -> None:
        code, error = self._run(["--keywords", "developer"])

        self.assertEqual(1, code)
        self.assertEqual("NO_LOCATION", error["code"])

    def test_bad_detail_exits_one_with_no_id(self) -> None:
        code, error = self._run(["--detail", "garbage"])

        self.assertEqual(1, code)
        self.assertEqual("NO_ID", error["code"])

    def test_delay_below_politeness_floor_is_rejected(self) -> None:
        code, error = self._run(["--location", "Wellington", "--delay", "1.4"])

        self.assertEqual(1, code)
        self.assertEqual("BAD_ARG", error["code"])

    def test_unknown_format_is_rejected(self) -> None:
        code, error = self._run(["--location", "Wellington", "--format", "xml"])

        self.assertEqual(1, code)
        self.assertEqual("BAD_ARG", error["code"])


class ManualOnlyIsolationTests(unittest.TestCase):
    def test_worker_never_references_or_schedules_the_guest_cli(self) -> None:
        crawler_root = Path(__file__).resolve().parents[1]
        worker = (crawler_root / "adapters" / "scrapling" / "scrapling_worker.py").read_text(
            encoding="utf-8"
        )
        self.assertNotIn("linkedin_guest", worker)

    def test_registry_entry_stays_disabled_and_unscheduled(self) -> None:
        crawler_root = Path(__file__).resolve().parents[1]
        registry = json.loads((crawler_root / "sources" / "sites.json").read_text(encoding="utf-8"))
        linkedin = next(site for site in registry["sites"] if site["name"] == "LinkedIn Jobs")

        self.assertFalse(linkedin["enabled"])
        self.assertFalse(linkedin["robotsAllows"])
        self.assertEqual("linkedin", linkedin["parser"])
        self.assertNotIn("listingUrls", linkedin)
        self.assertIn("MANUAL-ONLY", linkedin["notes"])


class SearchRunTests(unittest.TestCase):
    def test_dry_run_outputs_raw_job_envelope_without_detail_fetches(self) -> None:
        from argparse import Namespace

        calls: list[str] = []

        def fetch(url: str) -> str:
            calls.append(url)
            return SEARCH_HTML

        args = Namespace(
            keywords="developer",
            location="Wellington, New Zealand",
            jobage=14,
            max_jobs=2,
            page=1,
            detail=None,
            format="json",
            dry_run=True,
            delay=1.5,
        )

        payload = guest.run_search(args, fetch=fetch)

        self.assertEqual(1, len(calls))
        self.assertEqual(["4000000001", "4000000003"], [job["sourceJobId"] for job in payload["jobs"]])
        self.assertEqual("LinkedIn", payload["source"])
        self.assertEqual(2, payload["meta"]["count"])
        self.assertNotIn("isActiveById", payload)
        self.assertEqual("r1209600", guest.build_search_url("developer", "Wellington", 14, 1).split("f_TPR=")[1])

    def test_detail_mode_carries_isactive_side_channel(self) -> None:
        from argparse import Namespace

        args = Namespace(detail="https://www.linkedin.com/jobs/view/4000000001/")
        payload = guest.run_detail(args, fetch=lambda url: DETAIL_HTML)

        self.assertEqual(["4000000001"], [job["sourceJobId"] for job in payload["jobs"]])
        self.assertTrue(payload["isActiveById"]["4000000001"])
        self.assertEqual("Mid-Senior level", payload["criteriaById"]["4000000001"]["seniority"])
        self.assertNotIn("isActive", payload["jobs"][0])


if __name__ == "__main__":
    unittest.main()
