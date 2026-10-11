"""Regression tests for the shared content-quality gate.

Fixtures are real junk records observed in the live API on 2026-10-11.
"""
import json
import os
import sys
import unittest
from argparse import Namespace
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

FIXTURES = Path(__file__).resolve().parent / "fixtures"

from adapters.scrapling.scrapling_worker import (  # noqa: E402
    RawJobInput,
    build_fetcher,
    candidate_rejection_reason,
    crawl,
    extract_location,
    is_acceptable_candidate,
    linkedin_worker_paths,
    page_title_from_html,
    parse_comspek_detail,
    sanitise_location,
)


def job(
    title,
    url="https://example.com/jobs/1",
    source="Example",
    location=None,
    salary=None,
    description=None,
    posted=None,
):
    return RawJobInput(
        source, None, title, None, location, salary, description, url, posted, None
    )


def base_args(**overrides):
    values = dict(
        source="Example",
        url="https://example.com/jobs",
        parser="generic",
        fetcher_type="http",
        max_pages=10,
        delay_seconds=0,
        robots_txt_obey=False,
        proxy_url="",
        user_agent="test",
    )
    values.update(overrides)
    return Namespace(**values)


class ErrorPageTests(unittest.TestCase):
    def test_exact_junk_error_titles_from_live_data_are_rejected(self) -> None:
        titles = [
            "Not found",
            "404 Error: Page Not Found",
            "Page Not Found | Trimble Skip to content",
            "404 - Page not found - Talent International",
            "Error (404) - Pushpay",
            "Page not found – H2R Consulting",
            "Page not found | Alpha Recruitment",
        ]
        for title in titles:
            with self.subTest(title=title):
                self.assertIsNotNone(candidate_rejection_reason(job(title)))

    def test_fetched_page_title_error_is_rejected(self) -> None:
        candidate = job(
            "Senior Software Engineer",
            url="https://example.com/jobs/senior-software-engineer",
            location="Auckland",
            salary="$120k - $140k",
        )
        self.assertIsNone(candidate_rejection_reason(candidate))
        self.assertIsNotNone(
            candidate_rejection_reason(candidate, page_title="404 - Page not found")
        )

    def test_short_error_description_is_rejected(self) -> None:
        candidate = job(
            "Software Developer",
            url="https://example.com/jobs/developer",
            description="404 Error: Page Not Found",
        )
        self.assertIsNotNone(candidate_rejection_reason(candidate))

    def test_long_page_not_found_phrase_does_not_reject_real_advert(self) -> None:
        # "not found" can legitimately appear deep in a long advert; only short
        # descriptions are trusted for error detection.
        candidate = job(
            "Software Developer",
            url="https://example.com/jobs/developer",
            location="Wellington",
            salary="$100k - $120k",
            description=("Build great products. " * 60) + "If the API resource is not found, log it.",
        )
        self.assertIsNone(candidate_rejection_reason(candidate))


class ArticleTests(unittest.TestCase):
    def test_article_url_paths_are_rejected(self) -> None:
        urls = [
            "https://www.trademe.co.nz/c/jobs/article/how-to-become-a-civil-structural-engineer-nz",
            "https://example.com/advice/interview-tips",
            "https://example.com/blog/engineering-at-acme",
            "https://example.com/guides/salary-guide",
        ]
        for url in urls:
            with self.subTest(url=url):
                self.assertIsNotNone(candidate_rejection_reason(job("How to get hired", url=url)))

    def test_article_title_without_job_signals_is_rejected(self) -> None:
        self.assertIsNotNone(
            candidate_rejection_reason(
                job(
                    "How to become a civil or structural engineer in New Zealand",
                    url="https://example.com/careers/how-to-become-an-engineer",
                )
            )
        )

    def test_article_style_title_with_real_signals_is_kept(self) -> None:
        candidate = job(
            "Why you will love this Senior Data Engineer role",
            url="https://example.com/jobs/senior-data-engineer",
            location="Auckland",
            salary="$130k - $150k",
            description="Permanent full-time role.",
        )
        self.assertIsNone(candidate_rejection_reason(candidate))


class HubLandingTests(unittest.TestCase):
    def test_live_landing_titles_are_rejected(self) -> None:
        titles = [
            "Recruitment and Executive Search | Tribe Group Find a Job I'm Hiring Executive S",
            "Summer of Tech - empowering New Zealand to hire the next generation of tech talent",
            "People and careers | About Spark | Spark NZ",
        ]
        for title in titles:
            with self.subTest(title=title):
                self.assertIsNotNone(candidate_rejection_reason(job(title)))

    def test_hub_url_equal_to_crawl_url_is_rejected(self) -> None:
        candidate = job(
            "Join our team at Example",
            url="https://example.com/careers/",
        )
        self.assertIsNotNone(
            candidate_rejection_reason(candidate, crawl_url="https://example.com/careers/")
        )

    def test_title_equal_to_source_name_is_rejected(self) -> None:
        self.assertIsNotNone(candidate_rejection_reason(job("Fonterra", source="Fonterra")))

    def test_strong_hub_title_is_rejected_even_with_a_misparsed_salary(self) -> None:
        candidate = job(
            "4 jobs found for Consultant",
            url="https://www.randstad.co.nz/jobs/q-consultant/",
            salary="$60k - $70k",
        )
        self.assertIsNotNone(candidate_rejection_reason(candidate))

    def test_employer_profile_description_is_rejected(self) -> None:
        candidate = job(
            "Readygrad",
            url="https://nz.gradconnection.com/employers/readygrad/",
            description="Readygrad 10 opportunities New Zealand Less than 100 employees Follow Website",
        )
        self.assertIsNotNone(candidate_rejection_reason(candidate))

    def test_careers_advisor_is_not_treated_as_a_hub_title(self) -> None:
        candidate = job(
            "Careers Advisor",
            url="https://example.com/jobs/careers-advisor-123",
            location="Hamilton",
            salary="$70k - $80k",
        )
        self.assertIsNone(candidate_rejection_reason(candidate))


class GoodJobTests(unittest.TestCase):
    def test_real_titles_survive(self) -> None:
        good = [
            job("Senior Data Engineer", "https://example.com/jobs/1", location="Auckland", salary="$130k - $150k"),
            job("Senior Software Engineer", "https://example.com/jobs/2", location="Wellington", salary="$120k - $140k"),
            job("Workday Reporting Specialist", "https://example.com/jobs/3", location="Auckland", salary="$90k - $110k"),
            job("IT Help Desk Technician", "https://example.com/jobs/4", location="Christchurch", salary="$55k - $65k"),
            job(
                "Junior React Developer",
                "https://example.com/jobs/5",
                location="Wellington",
                salary="$65k - $80k",
                description="Full-time hybrid role using React and TypeScript.",
            ),
            # Real job missing salary and posted date still saves.
            job("Senior .NET Engineer", "https://example.com/jobs/6", location="Remote"),
        ]
        for candidate in good:
            with self.subTest(title=candidate.title):
                self.assertTrue(is_acceptable_candidate(candidate))
                self.assertIsNone(candidate_rejection_reason(candidate))


class PageTitleHelperTests(unittest.TestCase):
    def test_page_title_extraction(self) -> None:
        self.assertEqual(
            "Senior Engineer | Acme",
            page_title_from_html("<html><head><title>Senior Engineer | Acme</title></head></html>"),
        )
        self.assertIsNone(page_title_from_html("<html></html>"))
        self.assertIsNone(page_title_from_html(None))


class LocationExtractionTests(unittest.TestCase):
    def test_labelled_location_is_extracted(self) -> None:
        self.assertEqual("Auckland", extract_location("Location: Auckland Salary: $100k"))

    def test_seek_job_in_phrasing_is_extracted(self) -> None:
        self.assertEqual(
            "Christchurch Central, Canterbury",
            extract_location("Service Desk Support Analyst Job in Christchurch Central, Canterbury - SEEK"),
        )

    def test_based_in_phrasing_is_extracted(self) -> None:
        self.assertEqual("Wellington CBD", extract_location("Based in Wellington CBD"))

    def test_missing_location_returns_none(self) -> None:
        self.assertIsNone(extract_location("Senior Data Engineer with Python and SQL"))


class LocationSanitisationTests(unittest.TestCase):
    """Round 2 regression: location fields must not absorb description text."""

    SEEK_LOCATION = (
        "Nelson, Nelson Region, New Zealand We also welcome applications from "
        "qualified candidates based in Christchurch, Canterbury Region, New Zealand "
        "🧠 Neurodiversity"
    )
    COMSPEK_LOCATION = (
        "Wellington Job type Permanent Specialisation Team Leaders Reference "
        "BH-2844 Job description Are you passionate about building world-class "
        "engineering platforms"
    )

    #: Exact live strings from Round 2 follow-up (2026-10-12).
    CONTAMINATION = json.loads(
        (FIXTURES / "location_contamination.json").read_text()
    )

    def test_seek_location_stops_before_the_trailing_ad_sentence(self) -> None:
        self.assertEqual(
            "Nelson, Nelson Region, New Zealand", sanitise_location(self.SEEK_LOCATION)
        )

    def test_comspek_location_stops_before_the_detail_block(self) -> None:
        self.assertEqual("Wellington", sanitise_location(self.COMSPEK_LOCATION))

    def test_gallagher_fragment_is_not_a_location(self) -> None:
        for key in ("gallagher", "gallagher_alt"):
            with self.subTest(key=key):
                self.assertIsNone(sanitise_location(self.CONTAMINATION[key]))

    def test_talent_vault_location_stops_before_the_industry_field(self) -> None:
        self.assertEqual(
            "Auckland", sanitise_location(self.CONTAMINATION["talent_vault"])
        )

    def test_extract_location_does_not_match_based_inside_a_word(self) -> None:
        text = (
            "We Offer Performance-based incentives Career growth opportunities as "
            "eShepherd continues to scale globally"
        )
        self.assertIsNone(extract_location(text))

    def test_nz_government_multi_region_keeps_every_region_and_caps_length(self) -> None:
        result = sanitise_location(self.CONTAMINATION["nz_government_multi_region"])

        self.assertIsNotNone(result)
        self.assertLessEqual(len(result), 60)
        self.assertTrue(result.startswith("Wellington - Hutt Valley"))
        self.assertIn("Kapiti", result)
        self.assertIn("Masterton", result)

    def test_extract_location_does_not_absorb_a_single_line_dump(self) -> None:
        text = (
            "Location: Nelson, Nelson Region, New Zealand We also welcome applications "
            "from qualified candidates based in Christchurch, Canterbury Region, New Zealand"
        )
        self.assertEqual("Nelson, Nelson Region, New Zealand", extract_location(text))

    def test_long_multi_region_list_is_capped(self) -> None:
        regions = (
            "Wellington - Hutt Valley;Wellington - Kapiti;Wellington - Masterton;"
            "Wellington - Porirua;Wellington - Tawa;Wellington - Upper Hutt;Wellington - Wellington"
        )
        result = sanitise_location(regions)
        self.assertIsNotNone(result)
        self.assertLessEqual(len(result), 60)
        self.assertTrue(result.startswith("Wellington - Hutt Valley"))

    def test_clean_locations_are_untouched(self) -> None:
        for value in ("Auckland", "Christchurch, Canterbury, New Zealand", "Wellington CBD", "Remote"):
            with self.subTest(value=value):
                self.assertEqual(value, sanitise_location(value))

    def test_comspek_li_markup_extracts_only_the_location(self) -> None:
        fixture = (FIXTURES / "comspek_detail_li.html").read_text()
        job = parse_comspek_detail(
            "Comspek International",
            "https://www.comspek.co.nz/job/network-security-engineer-684437",
            fixture,
            "",
            "Network Security Engineer",
        )
        self.assertEqual("Wellington", job.location)
        self.assertEqual("Network Security Engineer", job.title)


class LinkedInPathTests(unittest.TestCase):
    """Round 2 regression: the helper path had a doubled ``adapters/`` segment."""

    def test_linkedin_script_path_is_not_doubled(self) -> None:
        _venv, script = linkedin_worker_paths()
        self.assertTrue(
            script.endswith(os.path.join("adapters", "linkedin", "linkedin_crawler.py")),
            script,
        )
        self.assertNotIn(os.path.join("adapters", "adapters"), script)
        self.assertTrue(os.path.exists(script), f"missing LinkedIn helper: {script}")


class FetcherMappingTests(unittest.TestCase):
    """The resolved per-site fetcher must win over the global --fetcher-type."""

    def test_scrapling_backend_uses_the_resolved_fetcher(self) -> None:
        args = base_args(fetcher_type="http", proxy_url="")
        seen: list[str | None] = []

        def fake_scrapling(_args, _url, fetcher_type=None):
            seen.append(fetcher_type)
            return "<html></html>", "text"

        with patch(
            "adapters.scrapling.scrapling_worker.fetch_with_scrapling", fake_scrapling
        ):
            fetch = build_fetcher(args, "dynamic")
            fetch("https://example.com/jobs")

        self.assertEqual(["dynamic"], seen)


class CrawlIntegrationTests(unittest.TestCase):
    def test_crawl_drops_an_error_page_that_still_looks_like_an_it_title(self) -> None:
        html = (
            "<html><head><title>Software Engineer - Page Not Found</title></head>"
            "<body><h1>Software Engineer - Page Not Found</h1></body></html>"
        )

        def fetch(url):
            return html, "Software Engineer - Page Not Found"

        result = crawl(
            base_args(source="Acme", url="https://acme.example/jobs/software-engineer-page-not-found"),
            fetch=fetch,
        )

        self.assertEqual([], result["jobs"])

    def test_crawl_keeps_a_real_job_detail(self) -> None:
        listing_html = (
            '<html><body><a href="/jobs/senior-data-engineer">Senior Data Engineer</a></body></html>'
        )
        detail_html = (
            "<html><head><title>Senior Data Engineer | Acme</title></head><body>"
            "<h1>Senior Data Engineer</h1><p>Location: Auckland</p>"
            "<p>Salary: $130k - $150k</p><p>Full-time role using Python and SQL.</p>"
            "</body></html>"
        )

        def fetch(url):
            if url.rstrip("/").endswith("/jobs"):
                return listing_html, "Senior Data Engineer"
            return (
                detail_html,
                "Senior Data Engineer Location: Auckland Salary: $130k - $150k Full-time role using Python and SQL.",
            )

        result = crawl(
            base_args(source="Acme", url="https://acme.example/jobs"),
            fetch=fetch,
        )

        self.assertEqual(1, len(result["jobs"]))
        self.assertEqual("Senior Data Engineer", result["jobs"][0]["title"])


if __name__ == "__main__":
    unittest.main()
