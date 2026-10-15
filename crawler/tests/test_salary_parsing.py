import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from adapters.scrapling.scrapling_worker import (
    extract_salary,
    structured_job_to_raw,
    structured_salary,
)


class SalaryParsingTests(unittest.TestCase):
    def test_extract_salary_skips_a_salary_filter_widget(self):
        # The ZEIL navigation filter exposed "$300,000+" as the top of a slider.
        text = "Salary Annual Wage $0 $300,000+ $0 $150+ Job Schedule Day Shift"

        self.assertIsNone(extract_salary(text))

    def test_extract_salary_still_reads_a_real_labelled_salary(self):
        text = "Salary: $120,000 Job type: Permanent Location: Auckland"

        self.assertEqual(extract_salary(text), "$120,000")

    def test_structured_salary_prefers_jsonld_base_salary_over_the_widget(self):
        source_data = {
            "baseSalary": {
                "@type": "MonetaryAmount",
                "currency": "NZD",
                "value": {"@type": "QuantitativeValue", "minValue": 55000, "maxValue": 70000, "unitText": "YEAR"},
            }
        }
        text = "Salary Annual Wage $0 $300,000+ RealNZ IT Support Engineer"

        self.assertEqual(structured_salary(source_data, text), "NZD 55000 - NZD 70000 per year")

    def test_structured_salary_formats_an_hourly_unit(self):
        source_data = {
            "baseSalary": {
                "currency": "NZD",
                "value": {"value": 85, "unitText": "HOUR"},
            }
        }

        self.assertEqual(structured_salary(source_data, ""), "NZD 85 per hour")

    def test_structured_job_reads_the_jsonld_salary_and_keeps_provenance(self):
        html = """
        <html><body>
        <script type="application/ld+json">
        {"@type":"JobPosting","title":"IT Support Engineer",
         "baseSalary":{"@type":"MonetaryAmount","currency":"NZD",
           "value":{"@type":"QuantitativeValue","minValue":55000,"maxValue":70000,"unitText":"YEAR"}},
         "datePosted":"2026-10-08T13:26:36+00:00"}
        </script>
        <span>Salary Annual Wage $0 $300,000+</span>
        </body></html>
        """

        from adapters.scrapling.scrapling_worker import parse_structured_job

        source_data = parse_structured_job(html)
        raw = structured_job_to_raw("ZEIL", "https://zeil.example/job/1", source_data, html, "fallback")

        self.assertEqual(raw.salaryText, "NZD 55000 - NZD 70000 per year")


if __name__ == "__main__":
    unittest.main()
