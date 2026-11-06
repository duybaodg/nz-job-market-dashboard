"""Unit tests for the Younity / JobAdder widget parser and crawl branch."""
import json
import sys
import unittest
from argparse import Namespace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from adapters.scrapling.scrapling_worker import crawl  # noqa: E402
from adapters.scrapling.parsers.younity import (  # noqa: E402
    decode_jobadder_jsonp,
    extract_jobadder_key,
    parse_younity_detail,
    parse_younity_list,
)

LIST_HTML = """
<div class="ja-job-list-container"> <div class="ja-job-list">
<div class="job">
<h2 class="title"> <a data-job-id="2008511" href="#" title="Data Engineer">Data Engineer</a> </h2>
<div class="meta"> <ul class="classifications">
<li data-id="20502"> ICT, Digital &amp; Technology </li>
<li data-id="20503"> Data Engineer </li>
<li data-id="20504"> Auckland </li>
<li data-id="20505"> Permanent </li>
</ul> </div>
<p class="summary">Build reliable data platforms that make a difference.</p>
<a class="view-details" data-job-id="2008511" href="#" title="More..">More..</a> </div>
<div class="job alt">
<h2 class="title"> <a data-job-id="2007973" href="#" title="Senior Infrastructure Engineer">Senior Infrastructure Engineer</a> </h2>
<div class="meta"> <ul class="classifications">
<li data-id="20502"> ICT, Digital &amp; Technology </li>
<li data-id="20503"> Network Engineer, NOC Engineer </li>
<li data-id="20504"> Tauranga </li>
<li data-id="20505"> Contract </li>
</ul> </div>
<p class="summary">Meaningful, hands-on infrastructure contract work.</p>
<a class="view-details" data-job-id="2007973" href="#" title="More..">More..</a> </div>
<div class="job">
<h2 class="title"> <a data-job-id="2004505" href="#" title="nCino Functional Consultant">nCino Functional Consultant</a> </h2>
<div class="meta"> <ul class="classifications">
<li data-id="20502"> ICT, Digital &amp; Technology </li>
<li data-id="20503"> Consultant - Functional </li>
<li data-id="20504"> Auckland </li>
<li data-id="20505"> Permanent </li>
</ul> </div>
<p class="summary">Banking platform functional consulting.</p>
<a class="view-details" data-job-id="2004505" href="#" title="More..">More..</a> </div>
</div> <div class="ja-pager-container"> <div class="ja-pager-summary">Page 1 of 1</div> </div> </div>
"""

DETAIL_HTML = """
<div class="ja-job-details"> <h2 class="title">Data Engineer</h2> <div class="meta">
<ul class="classifications">
<li data-id="20502"> ICT, Digital &amp; Technology </li>
<li data-id="20503"> Data Engineer </li>
<li data-id="20504"> Auckland </li>
<li data-id="20505"> Permanent </li>
</ul> </div> <ul class="bullet-points">
<li>Salary between $115k - $125k</li>
<li>Learning and Development Opportunities</li>
<li>Hybrid Working Environment - 2 Days in the office</li>
</ul> <div class="description">
<strong>The Opportunity</strong><br>Build Azure data pipelines with Synapse and PySpark.
</div> <div class="apply"> <a class="back-link" href="#" title="Real IT Jobs">Real IT Jobs</a> </div> </div>
"""

LANDING_HTML = """
<html><body>
<div id="ja-jobs-widget"></div>
<script id="jobadder-js-before">
var _jaJobsSettings = {"key":"test-key","jobListSettings":{"jobsPerPage":8}};
</script>
</body></html>
"""


def jsonp(html: str) -> str:
    return "cb(" + json.dumps(html) + ");"


class YounityParserTests(unittest.TestCase):
    def test_decode_jsonp_returns_the_html_fragment(self) -> None:
        self.assertEqual(LIST_HTML, decode_jobadder_jsonp(jsonp(LIST_HTML)))

    def test_decode_jsonp_rejects_non_jsonp_payloads(self) -> None:
        with self.assertRaises(ValueError):
            decode_jobadder_jsonp("<html>not jsonp</html>")

    def test_extract_jobadder_key_reads_the_public_widget_settings(self) -> None:
        self.assertEqual("test-key", extract_jobadder_key(LANDING_HTML))
        self.assertIsNone(extract_jobadder_key("<html><body>no widget</body></html>"))

    def test_parse_list_returns_cards_fields_and_page_count(self) -> None:
        cards, total_pages = parse_younity_list(LIST_HTML)

        self.assertEqual(1, total_pages)
        self.assertEqual(3, len(cards))
        self.assertEqual("2008511", cards[0]["jobId"])
        self.assertEqual("Data Engineer", cards[0]["title"])
        self.assertEqual("Auckland", cards[0]["location"])
        self.assertEqual("Permanent", cards[0]["employment"])
        self.assertEqual("Contract", cards[1]["employment"])
        self.assertEqual("nCino Functional Consultant", cards[2]["title"])

    def test_parse_detail_keeps_salary_location_and_description(self) -> None:
        job = parse_younity_detail(
            "Younity IT Jobs",
            "https://younity.co.nz/it-jobs/?ja-job=2008511",
            "2008511",
            "Data Engineer",
            "Auckland",
            "Permanent",
            DETAIL_HTML,
        )

        self.assertEqual("Data Engineer", job.title)
        self.assertEqual("Younity", job.company)
        self.assertEqual("Auckland", job.location)
        self.assertEqual("2008511", job.sourceJobId)
        self.assertIn("125k", job.salaryText or "")
        self.assertIn("Azure data pipelines", job.descriptionText or "")
        self.assertIsNone(job.postedDate)


class YounityCrawlTests(unittest.TestCase):
    def test_crawl_reads_the_landing_key_list_page_and_each_detail(self) -> None:
        calls: list[str] = []

        def fetch(url: str):
            calls.append(url)
            if url == "https://younity.co.nz/it-jobs/":
                return LANDING_HTML, ""
            if "RenderJobList" in url:
                return jsonp(LIST_HTML), LIST_HTML
            if "RenderJobDetails" in url:
                job_id = (
                    "2004505" if "2004505" in url
                    else "2007973" if "2007973" in url
                    else "2008511"
                )
                detail = DETAIL_HTML.replace("2008511", job_id)
                if job_id == "2007973":
                    detail = detail.replace("Data Engineer", "Senior Infrastructure Engineer")
                if job_id == "2004505":
                    detail = detail.replace("Data Engineer", "nCino Functional Consultant")
                return jsonp(detail), detail
            raise AssertionError(f"unexpected fetch: {url}")

        args = Namespace(
            source="Younity IT Jobs",
            url="https://younity.co.nz/it-jobs/",
            parser="younity",
            fetcher_type="http",
            max_pages=10,
            delay_seconds=0,
            robots_txt_obey=False,
            proxy_url="",
            user_agent="test",
        )

        result = crawl(args, fetch=fetch, sleeper=lambda _seconds: None)

        self.assertTrue(result["isComplete"])
        self.assertEqual(3, len(result["jobs"]))
        self.assertEqual(3, result["stats"]["candidates"])
        self.assertEqual(3, result["stats"]["kept"])
        self.assertTrue(any("RenderJobList" in url for url in calls))
        self.assertEqual(3, sum("RenderJobDetails" in url for url in calls))
        self.assertEqual(
            "https://younity.co.nz/it-jobs/?ja-job=2008511", result["jobs"][0]["url"]
        )
        # The board's own ICT classification keeps a platform-functional role
        # whose title alone carries no generic IT keyword.
        self.assertIn("nCino Functional Consultant", [job["title"] for job in result["jobs"]])


if __name__ == "__main__":
    unittest.main()
