import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from adapters.scrapling.scrapling_worker import parse_jobs


class ScraplingWorkerParserTests(unittest.TestCase):
    def test_parse_jobs_extracts_linked_job_cards(self) -> None:
        html = """
        <html><body>
          <a href="/jobs/1">Senior .NET Engineer</a>
          <a href="/privacy">Privacy Policy</a>
        </body></html>
        """
        text = "Company: Harbour Systems\nLocation: Auckland\nSalary: $125k - $150k"

        jobs = parse_jobs("Scrapling", "https://example.com/search", html, text)

        self.assertEqual(1, len(jobs))
        self.assertEqual("Senior .NET Engineer", jobs[0].title)
        self.assertEqual("https://example.com/jobs/1", jobs[0].url)
        self.assertEqual("Auckland", jobs[0].location)

    def test_parse_jobs_falls_back_to_single_page_job(self) -> None:
        html = "<html><body><h1>Graduate Cloud Engineer</h1></body></html>"
        text = "Company: Cloud Kiwi\nLocation: Hamilton\nFull-time remote Azure role"

        jobs = parse_jobs("Scrapling", "https://example.com/jobs/2", html, text)

        self.assertEqual(1, len(jobs))
        self.assertEqual("Graduate Cloud Engineer", jobs[0].title)
        self.assertEqual("Cloud Kiwi", jobs[0].company)
