"""Offline tests for the scheduled LinkedIn guest-endpoint crawl (Round 18)."""
from __future__ import annotations

import io
import json
import sys
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from adapters.linkedin import linkedin_guest_crawler as guest  # noqa: E402
from adapters.scrapling import scrapling_worker as worker  # noqa: E402

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
            with self.assertRaises(guest.BlockedError) as context:
                guest.html_fetch(
                    "https://www.linkedin.com/x",
                    sleeper=lambda seconds: sleeps.append(seconds),
                    jitter=lambda: 0.0,
                )

        self.assertEqual("BLOCKED", context.exception.code)
        self.assertEqual(3, attempts["count"])
        self.assertEqual([0.5, 1.0], sleeps)

    def test_404_returns_empty(self) -> None:
        with mock.patch.object(guest, "urlopen", side_effect=_http_error(404)):
            self.assertEqual("", guest.html_fetch("https://www.linkedin.com/x"))

    def test_999_is_a_block(self) -> None:
        with mock.patch.object(guest, "urlopen", side_effect=_http_error(999)):
            with self.assertRaises(guest.BlockedError):
                guest.html_fetch("https://www.linkedin.com/x")

    def test_403_is_a_block(self) -> None:
        with mock.patch.object(guest, "urlopen", side_effect=_http_error(403)):
            with self.assertRaises(guest.BlockedError):
                guest.html_fetch("https://www.linkedin.com/x")

    def test_challenge_page_is_a_block(self) -> None:
        challenge = _FakeResponse(
            b'<html><body><div id="captcha">Let\'s do a quick security check</div></body></html>'
        )
        with mock.patch.object(guest, "urlopen", return_value=challenge):
            with self.assertRaises(guest.BlockedError):
                guest.html_fetch("https://www.linkedin.com/x")


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

    def test_detail_limit_above_hard_cap_is_rejected(self) -> None:
        code, error = self._run(["--location", "Wellington", "--detail-limit", "11"])

        self.assertEqual(1, code)
        self.assertEqual("BAD_ARG", error["code"])

    def test_max_jobs_above_hard_cap_is_rejected(self) -> None:
        code, error = self._run(["--location", "Wellington", "--max-jobs", "41"])

        self.assertEqual(1, code)
        self.assertEqual("BAD_ARG", error["code"])


class RobotsExemptionTests(unittest.TestCase):
    """The operator exemption is an explicit allowlist of exactly one source."""

    def test_exemption_is_an_allowlist_of_one(self) -> None:
        self.assertEqual(frozenset({"LinkedIn Jobs"}), worker.ROBOTS_EXEMPT_SOURCES)
        self.assertEqual("linkedin-guest", worker.LINKEDIN_GUEST_PARSER)

    def test_registry_entry_is_scheduled_with_truthful_robots_flag(self) -> None:
        crawler_root = Path(__file__).resolve().parents[1]
        registry = json.loads((crawler_root / "sources" / "sites.json").read_text(encoding="utf-8"))
        linkedin = next(site for site in registry["sites"] if site["name"] == "LinkedIn Jobs")

        self.assertTrue(linkedin["enabled"])
        self.assertFalse(linkedin["robotsAllows"], "robotsAllows must stay truthfully false")
        self.assertEqual("linkedin-guest", linkedin["parser"])
        self.assertEqual(1440, linkedin["frequency_minutes"])
        self.assertEqual(
            "https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search",
            linkedin["jobs_url"],
        )
        self.assertIn("OPERATOR EXEMPTION 2026-10-23", linkedin["notes"])
        self.assertIn("allowlist-of-one", linkedin["notes"])

    def test_every_other_enabled_source_still_passes_through_the_robots_gate(self) -> None:
        crawler_root = Path(__file__).resolve().parents[1]
        registry = json.loads((crawler_root / "sources" / "sites.json").read_text(encoding="utf-8"))
        checked = 0
        for site in registry["sites"]:
            if not site.get("enabled") or site["name"] == "LinkedIn Jobs":
                continue
            with self.subTest(site=site["name"]):
                with mock.patch.object(worker, "obeys_robots", return_value=False) as gate:
                    allowed = worker.robots_allows_crawl(
                        site.get("jobs_url") or site.get("homepage") or "https://example.com",
                        "test-agent",
                        parser=site["parser"],
                        source=site["name"],
                    )
                self.assertFalse(allowed)
                gate.assert_called_once()
                checked += 1
        self.assertGreater(checked, 40)

    def test_exemption_cannot_leak_to_another_parser_or_source(self) -> None:
        with mock.patch.object(worker, "obeys_robots", return_value=False) as gate:
            self.assertFalse(
                worker.robots_allows_crawl(
                    "https://www.linkedin.com/jobs/",
                    "test-agent",
                    parser="linkedin",
                    source="LinkedIn Jobs",
                )
            )
            self.assertFalse(
                worker.robots_allows_crawl(
                    "https://nz.seek.com/jobs",
                    "test-agent",
                    parser="linkedin-guest",
                    source="SEEK NZ",
                )
            )
        self.assertEqual(2, gate.call_count)
        with mock.patch.object(worker, "obeys_robots", return_value=False) as gate:
            self.assertTrue(
                worker.robots_allows_crawl(
                    "https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search",
                    "test-agent",
                    parser="linkedin-guest",
                    source="LinkedIn Jobs",
                )
            )
        gate.assert_not_called()


class WorkerGuestCrawlTests(unittest.TestCase):
    PAYLOAD = {
        "source": "LinkedIn",
        "jobs": [
            {
                "source": "LinkedIn",
                "sourceJobId": "4000000001",
                "title": "Senior Developer",
                "company": "Acme",
                "location": "Auckland, New Zealand",
                "salaryText": None,
                "descriptionText": "Build things.",
                "url": "https://www.linkedin.com/jobs/view/4000000001/",
                "postedDate": "2026-10-20",
                "closingDate": None,
            },
            {
                "source": "LinkedIn",
                "sourceJobId": "4000000002",
                "title": "Data Engineer",
                "company": "Contoso",
                "location": "Remote",
                "salaryText": None,
                "descriptionText": None,
                "url": "https://www.linkedin.com/jobs/view/4000000002/",
                "postedDate": None,
                "closingDate": None,
            },
        ],
        "isActiveById": {"4000000001": False, "4000000002": True},
        "meta": {"count": 2, "detailFetches": 2, "requestsUsed": 3},
    }

    def test_argv_is_fixed_and_bounded(self) -> None:
        calls: list[list[str]] = []

        def runner(argv, **kwargs):
            calls.append(argv)
            return mock.Mock(returncode=0, stdout=json.dumps(self.PAYLOAD), stderr="")

        jobs, _ = worker.run_linkedin_guest_crawl("LinkedIn Jobs", runner=runner)

        self.assertEqual(2, len(jobs))
        argv = calls[0]
        self.assertTrue(argv[1].endswith("linkedin_guest_crawler.py"))
        self.assertEqual("New Zealand", argv[argv.index("--location") + 1])
        self.assertEqual("14", argv[argv.index("--jobage") + 1])
        self.assertEqual("40", argv[argv.index("--max-jobs") + 1])
        self.assertEqual("10", argv[argv.index("--detail-limit") + 1])
        self.assertEqual("1.5", argv[argv.index("--delay") + 1])
        self.assertEqual("json", argv[argv.index("--format") + 1])
        self.assertEqual("developer OR engineer OR data OR cloud OR devops OR security OR analyst",
                         argv[argv.index("--keywords") + 1])

    def test_json_maps_to_raw_jobs_with_deactivation_and_location_normalisation(self) -> None:
        jobs, meta = worker.run_linkedin_guest_crawl(
            "LinkedIn Jobs",
            runner=lambda argv, **kwargs: mock.Mock(
                returncode=0, stdout=json.dumps(self.PAYLOAD), stderr=""
            ),
        )

        self.assertEqual("Auckland", jobs[0].location)
        self.assertFalse(jobs[0].isActive)
        self.assertEqual("Remote (NZ)", jobs[1].location)
        self.assertTrue(jobs[1].isActive)
        self.assertEqual(1, meta["inactive"])

    def test_blocked_stderr_raises_a_clear_failed_run(self) -> None:
        blocked = json.dumps({"error": "LinkedIn returned a challenge page", "code": "BLOCKED"})
        with self.assertRaises(RuntimeError) as context:
            worker.run_linkedin_guest_crawl(
                "LinkedIn Jobs",
                runner=lambda argv, **kwargs: mock.Mock(returncode=1, stdout="", stderr=blocked),
            )
        self.assertIn("blocked", str(context.exception).lower())

    def test_challenge_html_via_main_saves_nothing(self) -> None:
        challenge = _FakeResponse(
            b'<html><body><form id="challenge-form">verify that you are human</form></body></html>'
        )
        stderr = io.StringIO()
        stdout = io.StringIO()
        with mock.patch.object(guest, "urlopen", return_value=challenge):
            with redirect_stderr(stderr), redirect_stdout(stdout):
                code = guest.main(["--keywords", "developer", "--location", "New Zealand"])

        self.assertEqual(1, code)
        self.assertEqual("", stdout.getvalue())
        self.assertEqual("BLOCKED", json.loads(stderr.getvalue())["code"])

    def test_request_budget_caps_search_plus_details(self) -> None:
        from argparse import Namespace

        calls: list[str] = []

        def fetch(url: str) -> str:
            calls.append(url)
            return SEARCH_HTML if "seeMoreJobPostings" in url else DETAIL_HTML

        args = Namespace(
            keywords="developer",
            location="New Zealand",
            jobage=14,
            max_jobs=40,
            detail_limit=1,
            page=1,
            detail=None,
            format="json",
            dry_run=False,
            delay=1.5,
        )

        payload = guest.run_search(args, fetch=fetch)

        self.assertEqual(2, len(calls), "one search page + one bounded detail fetch")
        self.assertEqual(1, payload["meta"]["detailFetches"])
        self.assertEqual(2, payload["meta"]["requestsUsed"])
        self.assertEqual(11, payload["meta"]["requestBudget"])

    def test_budget_exhaustion_is_explicit(self) -> None:
        budget = guest.RequestBudget(limit=1)
        budget.spend("https://www.linkedin.com/search")
        with self.assertRaises(guest.BudgetExceeded):
            budget.spend("https://www.linkedin.com/detail")

    def test_crawl_branch_propagates_a_block_as_a_failed_run(self) -> None:
        from argparse import Namespace

        args = Namespace(
            source="LinkedIn Jobs",
            url="https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search",
            parser="linkedin-guest",
            fetcher_type="http",
            max_pages=10,
            delay_seconds=0,
            robots_txt_obey=True,
            proxy_url="",
            user_agent="test",
        )
        with mock.patch.object(
            worker, "run_linkedin_guest_crawl", side_effect=RuntimeError("blocked")
        ):
            with self.assertRaises(RuntimeError):
                worker.crawl(args, fetch=lambda url: ("", ""))

    def test_guest_parser_on_a_non_exempt_source_still_obeys_robots(self) -> None:
        from argparse import Namespace

        args = Namespace(
            source="SEEK NZ",
            url="https://nz.seek.com/jobs",
            parser="linkedin-guest",
            fetcher_type="http",
            max_pages=10,
            delay_seconds=0,
            robots_txt_obey=True,
            proxy_url="",
            user_agent="test",
        )
        with mock.patch.object(worker, "obeys_robots", return_value=False):
            with self.assertRaises(RuntimeError) as context:
                worker.crawl(args, fetch=lambda url: ("", ""))
        self.assertIn("Robots.txt does not allow", str(context.exception))


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
