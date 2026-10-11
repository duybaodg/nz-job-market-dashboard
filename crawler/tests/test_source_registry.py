"""Tests for the source registry, sitemap discovery and registry-driven routing."""
import sys
import unittest
from argparse import Namespace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from adapters.scrapling.scrapling_worker import (  # noqa: E402
    crawl,
    discover_sitemap_job_urls,
    is_acceptable_job,
    is_concrete_job_url,
    is_job_link,
    is_plausible_job_title,
    looks_like_it_job,
    normalize_job_url,
    parse_pageup_detail,
    parse_rss_jobs,
    parse_seek_detail,
    parse_seek_listing,
    parse_structured_jobs,
    relative_days_ago,
    sanitize_href,
    pageup_date,
    structured_location,
    title_from_url,
)
from adapters.scrapling.site_registry import load_registry, lookup_source  # noqa: E402


def base_args(**overrides):
    values = dict(
        source="Generic",
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


class SourceRegistryTests(unittest.TestCase):
    def test_registry_covers_the_full_nz_site_list(self) -> None:
        registry = load_registry()

        self.assertGreaterEqual(len(registry), 60)
        # Every entry must carry a crawl strategy so the worker can route it.
        self.assertTrue(all(site.get("strategy") for site in registry.values()))

    def test_lookup_is_case_insensitive_and_carries_platform_credentials(self) -> None:
        jade = lookup_source("jade software")

        self.assertIsNotNone(jade)
        self.assertEqual("bamboohr", jade["platform"])
        self.assertEqual("jadeworld", jade["token"])

    def test_title_from_url_produces_readable_fallback(self) -> None:
        self.assertEqual(
            "Senior Software Engineer",
            title_from_url("https://example.com/careers/jobs/senior-software-engineer-12345"),
        )

    def test_discover_sitemap_follows_index_and_filters_job_urls(self) -> None:
        pages = {
            "https://example.com/sitemap.xml": (
                "<sitemapindex><sitemap><loc>https://example.com/jobs-sitemap.xml</loc></sitemap></sitemapindex>",
                "",
            ),
            "https://example.com/jobs-sitemap.xml": (
                "<urlset>"
                "<url><loc>https://example.com/careers/jobs/data-engineer-1</loc></url>"
                "<url><loc>https://example.com/about-us</loc></url>"
                "</urlset>",
                "",
            ),
        }

        def fetch(url):
            return pages.get(url, ("", ""))

        found = discover_sitemap_job_urls("https://example.com/careers/", fetch)

        self.assertEqual(["https://example.com/careers/jobs/data-engineer-1"], found)

    def test_crawl_routes_api_platform_from_registry(self) -> None:
        payload = {
            "result": [
                {
                    "id": "342",
                    "jobOpeningName": "Software Developer",
                    "employmentStatusLabel": "Full Time",
                    "location": {"city": "Christchurch", "state": "Canterbury"},
                }
            ]
        }
        calls = []

        def api_fetch(url, method="GET", body=None):
            calls.append(url)
            return payload

        result = crawl(base_args(source="Jade Software", parser="bamboohr"), api_fetch=api_fetch)

        self.assertEqual(1, len(result["jobs"]))
        self.assertEqual("Software Developer", result["jobs"][0]["title"])
        self.assertEqual("Jade Software", result["jobs"][0]["company"])
        self.assertTrue(calls[0].startswith("https://jadeworld.bamboohr.com/"))
        self.assertTrue(result["isComplete"])

    def test_build_fetcher_prefers_browser_when_scrapling_missing(self) -> None:
        import adapters.scrapling.scrapling_worker as worker

        calls: list[tuple[str, str]] = []

        def boom(args, url):
            raise RuntimeError("Scrapling is not installed. Run `python -m pip install -e crawler`.")

        def browser(args, url):
            calls.append(("browser", url))
            return "<html>rendered</html>", "rendered"

        original_scrapling = worker.fetch_with_scrapling
        original_browser = worker.fetch_with_browser
        original_lookup = worker.find_browser_binary
        worker.fetch_with_scrapling = boom
        worker.fetch_with_browser = browser
        worker.find_browser_binary = lambda: "/usr/bin/chromium"
        try:
            fetch = worker.build_fetcher(base_args(), "dynamic")
            fetch("https://example.com/jobs/1")
            fetch("https://example.com/jobs/2")
        finally:
            worker.fetch_with_scrapling = original_scrapling
            worker.fetch_with_browser = original_browser
            worker.find_browser_binary = original_lookup

        self.assertEqual(
            [("browser", "https://example.com/jobs/1"), ("browser", "https://example.com/jobs/2")], calls
        )

    def test_crawl_uses_sitemap_discovery_for_dynamic_career_sites(self) -> None:
        detail_url = "https://www.example.co.nz/careers/jobs/senior-software-engineer-123"
        pages = {
            "https://www.example.co.nz/careers/": (
                "<html><body><div id=\"root\"></div></body></html>",
                "",
            ),
            "https://www.example.co.nz/sitemap.xml": (
                f"<urlset><url><loc>{detail_url}</loc></url></urlset>",
                "",
            ),
            detail_url: (
                "<html><body><h1>Senior Software Engineer</h1>"
                "<p>We build cloud platforms with Azure and Kubernetes.</p></body></html>",
                "Senior Software Engineer We build cloud platforms with Azure and Kubernetes.",
            ),
        }

        def fetch(url):
            if url in pages:
                return pages[url]
            raise RuntimeError(f"unexpected fetch: {url}")

        result = crawl(
            base_args(source="Example Sitemap Co", url="https://www.example.co.nz/careers/", parser="jsonld"),
            fetch=fetch,
        )

        self.assertEqual(1, len(result["jobs"]))
        self.assertEqual("Senior Software Engineer", result["jobs"][0]["title"])
        self.assertEqual(detail_url, result["jobs"][0]["url"])

    def test_build_fetcher_falls_back_to_http_when_scrapling_missing(self) -> None:
        import adapters.scrapling.scrapling_worker as worker

        calls: list[tuple[str, str]] = []

        def boom(args, url):
            calls.append(("scrapling", url))
            raise RuntimeError("Scrapling is not installed. Run `python -m pip install -e crawler`.")

        def http(args, url):
            calls.append(("http", url))
            return "<html></html>", "text"

        original_scrapling = worker.fetch_with_scrapling
        original_http = worker.fetch_with_http
        original_browser_lookup = worker.find_browser_binary
        worker.fetch_with_scrapling = boom
        worker.fetch_with_http = http
        worker.find_browser_binary = lambda: None  # force the HTTP fallback
        try:
            fetch = worker.build_fetcher(base_args(), "dynamic")
            fetch("https://example.com/1")
            fetch("https://example.com/2")
        finally:
            worker.fetch_with_scrapling = original_scrapling
            worker.fetch_with_http = original_http
            worker.find_browser_binary = original_browser_lookup

        self.assertEqual(
            [
                ("scrapling", "https://example.com/1"),
                ("http", "https://example.com/1"),
                ("http", "https://example.com/2"),
            ],
            calls,
        )

SEEK_CARD_HTML = """
<html><body>
<article data-automation="normalJob">
  <a href="/job/95156028?type=standard&amp;ref=search-standalone">View</a>
  <span data-automation="jobTitle">Senior Software Engineer</span>
  <span data-automation="jobCompany">Acme Ltd</span>
  <span data-automation="jobLocation">Auckland</span>
  <span data-automation="jobListingDate">Listed six days ago</span>
  <span data-automation="jobShortDescription">Build things with Azure.</span>
  <span data-automation="jobClassification">Information &amp; Communication Technology</span>
  <span data-automation="jobSubClassification">Engineering - Software</span>
</article>
</body></html>
"""


class SeekAndTradeMeTests(unittest.TestCase):
    def test_relative_days_ago_handles_words_and_digits(self) -> None:
        self.assertIsNotNone(relative_days_ago("Listed six days ago"))
        self.assertIsNotNone(relative_days_ago("18d ago"))
        self.assertIsNotNone(relative_days_ago("Today"))
        self.assertIsNone(relative_days_ago("whenever"))

    def test_seek_listing_parser_extracts_cards(self) -> None:
        jobs = parse_seek_listing(
            "SEEK NZ", "https://nz.seek.com/information-technology-jobs", SEEK_CARD_HTML, ""
        )

        self.assertEqual(1, len(jobs))
        job = jobs[0]
        self.assertEqual("Senior Software Engineer", job.title)
        self.assertEqual("Acme Ltd", job.company)
        self.assertEqual("Auckland", job.location)
        self.assertEqual("95156028", job.sourceJobId)
        self.assertEqual("https://nz.seek.com/job/95156028", job.url)
        self.assertIn("Engineering - Software", job.descriptionText or "")
        self.assertIsNotNone(job.postedDate)

    def test_seek_detail_parser_uses_sk_dl_payload(self) -> None:
        html = """
        <html><body><h1>IT Help Desk Technician</h1>
        <script>window.SK_DL = {"jobTitle":"IT Help Desk Technician","advertiserName":"Aioi NZ",
          "jobArea":"Ellerslie, Auckland","jobWorkTypeIds":"242","jobPostedTime":"18d ago"};</script>
        <script type="application/json">{"content":"<p>Support the team with <b>Windows</b>.</p>"}</script>
        </body></html>
        """
        job = parse_seek_detail("SEEK NZ", "https://nz.seek.com/job/94823235", html, "", "ignored")

        self.assertEqual("IT Help Desk Technician", job.title)
        self.assertEqual("Aioi NZ", job.company)
        self.assertEqual("Ellerslie, Auckland", job.location)
        self.assertIn("Windows", job.descriptionText or "")
        self.assertTrue((job.descriptionText or "").startswith("Employment type: Full-time"))
        self.assertEqual("94823235", job.sourceJobId)

    def test_job_link_rules_for_seek_and_trade_me(self) -> None:
        self.assertTrue(is_job_link("seek", "anything", "https://nz.seek.com/job/95156028"))
        self.assertFalse(is_job_link("seek", "anything", "https://nz.seek.com/information-technology-jobs"))
        self.assertTrue(
            is_job_link("trade-me", "anything", "https://www.trademe.co.nz/a/jobs/it/listing/6160498813")
        )
        self.assertFalse(
            is_job_link("trade-me", "anything", "https://www.trademe.co.nz/a/jobs/it/project-management")
        )

    def test_trade_me_urls_drop_tracking_queries(self) -> None:
        self.assertEqual(
            "https://www.trademe.co.nz/a/jobs/it/listing/6160498813",
            normalize_job_url(
                "trade-me", "https://www.trademe.co.nz/a/jobs/it/listing/6160498813?rsqid=abc#frag"
            ),
        )
        self.assertEqual(
            "https://nz.seek.com/job/1?x=1", normalize_job_url("seek", "https://nz.seek.com/job/1?x=1")
        )

    def test_crawl_seek_reads_only_listing_pages(self) -> None:
        fetched: list[str] = []

        def fetch(url):
            fetched.append(url)
            return SEEK_CARD_HTML, "cards"

        args = base_args(source="SEEK NZ", url="https://nz.seek.com/information-technology-jobs", parser="seek")
        args.max_pages = 3
        result = crawl(args, fetch=fetch, sleeper=lambda _: None)

        self.assertEqual(1, len(result["jobs"]))
        self.assertTrue(all("/job/" not in url or url == fetched[0] for url in fetched))
        self.assertTrue(all("information-technology-jobs" in url for url in fetched))

class JobQualityFilterTests(unittest.TestCase):
    def test_hub_page_titles_are_rejected(self) -> None:
        for title in (
            "Careers - BNZ",
            "3 jobs found for engineering",
            "Home | New Cops | New Zealand Police",
            "Working here at One NZ - Careers",
            "Jobspace - Launch Your New Career",
            "Register as a job seeker",
            "Program your path",
            "Job search | Find a job",
        ):
            self.assertFalse(is_plausible_job_title(title, "Some Source"), title)

    def test_source_name_alone_is_not_a_job(self) -> None:
        self.assertFalse(is_plausible_job_title("Fonterra", "Fonterra"))
        self.assertTrue(is_plausible_job_title("Fonterra", "Other Source"))

    def test_real_titles_survive(self) -> None:
        for title in ("Senior Software Engineer", "Workday Reporting Specialist", "Data Analyst"):
            self.assertTrue(is_plausible_job_title(title, "Source"))

    def test_enterprise_technology_roles_are_kept(self) -> None:
        self.assertTrue(looks_like_it_job("Workday Reporting Specialist", ""))
        self.assertTrue(looks_like_it_job("Salesforce Developer", ""))
        self.assertTrue(looks_like_it_job("ServiceNow Consultant", ""))

    def test_non_technology_roles_are_dropped(self) -> None:
        self.assertFalse(looks_like_it_job("Regional Marketing Manager", "Security guard"))
        self.assertFalse(looks_like_it_job("Registered Nurse", "Patient care"))
        # "sas" must not match as a substring of "sales".
        self.assertFalse(looks_like_it_job("Sales Representative", ""))

    def test_engineer_titles_need_real_technology_context(self) -> None:
        # A bare "engineer" must not qualify civil/mechanical roles.
        for title in ("Project Site Engineer", "Civil Engineer", "Mechanical Engineer", "Quantity Surveyor"):
            self.assertFalse(looks_like_it_job(title, ""), title)
        for title in (
            "Systems Engineer",
            "Network Engineer",
            "Senior .NET Engineer",
            "Senior C# Developer",
            "Solution Architect",
            "Technical Business Analyst – PEGA",
        ):
            self.assertTrue(looks_like_it_job(title, ""), title)

    def test_hays_job_link_rule(self) -> None:
        self.assertTrue(
            is_job_link(
                "hays",
                "Database Reliability Engineer",
                "https://www.hays.net.nz/job-detail/database-reliability-engineer-wellington_3016226",
            )
        )
        self.assertFalse(
            is_job_link("hays", "Technology", "https://www.hays.net.nz/job-search?q=technology")
        )

    def test_accept_requires_both_quality_and_it_match(self) -> None:
        from adapters.scrapling.scrapling_worker import RawJobInput

        good = RawJobInput("S", None, "Software Engineer", "Acme", "Auckland", None, None, "https://x/1", None, None)
        hub = RawJobInput("S", None, "Careers - Acme", "Acme", "Auckland", None, None, "https://x/2", None, None)
        non_it = RawJobInput("S", None, "Registered Nurse", "Hospital", "Auckland", None, None, "https://x/3", None, None)

        self.assertTrue(is_acceptable_job(good))
        self.assertFalse(is_acceptable_job(hub))
        self.assertFalse(is_acceptable_job(non_it))

    def test_crawl_does_not_store_a_generic_hub_page_as_a_job(self) -> None:
        hub_html = "<html><body><h1>Careers - Acme</h1><p>Join our team</p></body></html>"

        def fetch(url):
            return hub_html, "Careers - Acme Join our team"

        result = crawl(base_args(source="Acme", url="https://acme.example/careers/"), fetch=fetch)

        self.assertEqual([], result["jobs"])

    def test_concrete_job_url_distinguishes_postings_from_categories(self) -> None:
        self.assertTrue(is_concrete_job_url("https://x.co/jobs/123456"))
        self.assertTrue(is_concrete_job_url("https://x.co/careers/jobs/senior-software-engineer"))
        self.assertFalse(is_concrete_job_url("https://x.co/jobs/category/construction"))
        self.assertFalse(is_concrete_job_url("https://x.co/jobs/"))
        self.assertFalse(is_concrete_job_url("https://x.co/blog/5-ways-to-do-your-job"))

    def test_sanitize_href_drops_malformed_links(self) -> None:
        self.assertEqual("/jobs/1", sanitize_href('"/jobs/1"'))
        self.assertIsNone(sanitize_href('/jobs/"/analyst-jobs'))
        self.assertIsNone(sanitize_href("   "))
        self.assertIsNone(sanitize_href(None))

    def test_parse_structured_jobs_returns_every_posting(self) -> None:
        html = """
        <script type="application/ld+json">
        {"@graph":[
          {"@type":"JobPosting","title":"Developer","url":"https://x/1"},
          {"@type":"JobPosting","title":"Analyst","url":"https://x/2"},
          {"@type":"Organization","name":"Acme"}
        ]}
        </script>
        <script type="application/ld+json">
        {"@type":"JobPosting","title":"Engineer","url":"https://x/3"}
        </script>
        """
        postings = parse_structured_jobs(html)

        self.assertEqual(["Developer", "Analyst", "Engineer"], [p["title"] for p in postings])

    def test_structured_location_handles_country_objects(self) -> None:
        location = structured_location(
            {
                "jobLocation": {
                    "address": {
                        "addressLocality": "Auckland",
                        "addressRegion": "Auckland",
                        "addressCountry": {"@type": "Country", "name": "NZ"},
                    }
                }
            }
        )

        self.assertEqual("Auckland, NZ", location)

    def test_pageup_parser_reads_schema_microdata(self) -> None:
        html = """
        <html><body>
        <div itemscope itemtype="http://schema.org/JobPosting">
          <span itemprop="jobLocation" itemscope itemtype="http://schema.org/Place">
            <span itemprop="address" itemscope itemtype="http://schema.org/PostalAddress">
              <meta itemprop="streetAddress" content="Auckland, NZ">
            </span>
          </span>
          <meta itemprop="datePosted" content="Thu Oct 01 00:00:00 UTC 2026">
          <meta itemprop="validThrough" content="Sun Oct 11 11:00:00 UTC 2026">
          <meta itemprop="hiringOrganization" content="One New Zealand">
          <h1>Title: Core Network DevOps Engineer</h1>
          <span itemprop="description"><span class="jobdescription">
            Build and run core network platforms with AWS and Kubernetes at scale.
          </span></span>
        </div>
        </body></html>
        """
        job = parse_pageup_detail(
            "One NZ",
            "https://careers.one.nz/job/Auckland-Core-Network-DevOps-Engineer/1368245966/",
            html,
            "Core Network DevOps Engineer Auckland NZ",
            "ignored",
        )

        self.assertEqual("Core Network DevOps Engineer", job.title)
        self.assertEqual("One New Zealand", job.company)
        self.assertEqual("Auckland, NZ", job.location)
        self.assertEqual("1368245966", job.sourceJobId)
        self.assertEqual("2026-10-01T00:00:00Z", job.postedDate)
        self.assertEqual("2026-10-11T00:00:00Z", job.closingDate)
        self.assertIn("Kubernetes", job.descriptionText or "")

    def test_rss_job_feed_parser(self) -> None:
        xml = """<?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"><channel>
          <item>
            <title><![CDATA[Cyber Security Analyst (Senior)]]></title>
            <description><![CDATA[]]></description>
            <guid isPermaLink="true">https://careers.example.co.nz/careers/JobDetail/Cyber-Security-Analyst/65282</guid>
            <link>https://careers.example.co.nz/careers/JobDetail/Cyber-Security-Analyst/65282</link>
            <pubDate>Fri, 07 Aug 2026 00:00:00 +0000</pubDate>
          </item>
          <item>
            <title>Customer Experience Agent</title>
            <link>https://careers.example.co.nz/careers/JobDetail/Customer-Experience-Agent/66335</link>
            <pubDate>Mon, 10 Aug 2026 00:00:00 +0000</pubDate>
          </item>
        </channel></rss>"""
        jobs = parse_rss_jobs("Spark NZ", xml)

        self.assertEqual(2, len(jobs))
        self.assertEqual("Cyber Security Analyst (Senior)", jobs[0].title)
        self.assertEqual("65282", jobs[0].sourceJobId)
        self.assertEqual("2026-08-07T00:00:00Z", jobs[0].postedDate)

        kept = [job for job in jobs if looks_like_it_job(job.title, "")]
        self.assertEqual(["Cyber Security Analyst (Senior)"], [job.title for job in kept])

    def test_pageup_date_parsing(self) -> None:
        self.assertEqual("2026-10-01T00:00:00Z", pageup_date("Thu Oct 01 00:00:00 UTC 2026"))
        self.assertEqual("2026-01-31T00:00:00Z", pageup_date("Sat Jan 31 00:00:00 UTC 2026"))
        self.assertIsNone(pageup_date(None))

    def test_sanitize_and_decompress_helpers(self) -> None:
        from adapters.scrapling.scrapling_worker import _decompress, _sanitize

        self.assertEqual("HTTP 404 for x: gone", _sanitize("HTTP 404 for x: \x00\x01gone"))
        self.assertEqual(b"plain", _decompress(b"plain", "identity"))
        self.assertEqual(b"plain", _decompress(b"plain", ""))

    def test_clean_text_removes_literal_escape_sequences(self) -> None:
        from adapters.scrapling.scrapling_worker import clean_text

        self.assertEqual("Senior Network Engineer", clean_text("\\r\\n \\n Senior Network Engineer\\n \\r\\n"))

    def test_probable_job_detail_guards_against_blog_pages(self) -> None:
        from adapters.scrapling.scrapling_worker import is_probable_job_detail

        self.assertTrue(is_probable_job_detail("https://x.co/job/senior-engineer", "Whatever"))
        self.assertTrue(is_probable_job_detail("https://x.co/blog/post", "Senior Software Engineer"))
        self.assertFalse(is_probable_job_detail("https://x.co/blog-and-news/hvnl-changes", "What are the latest HVNL changes?"))

    def test_crawl_rejects_blog_page_matched_by_link_text(self) -> None:
        listing = '<a href="/blog-and-news/hvnl-changes">Analyst update</a>'
        blog = "<html><body><h1>What are the latest HVNL changes?</h1><p>Cloud platform news</p></body></html>"

        def fetch(url):
            if url.endswith("/careers/"):
                return listing, "Analyst update"
            return blog, "What are the latest HVNL changes? Cloud platform news"

        result = crawl(base_args(source="Example Blog Co", url="https://www.example.co.nz/careers/"), fetch=fetch)

        self.assertEqual([], result["jobs"])


if __name__ == "__main__":
    unittest.main()
