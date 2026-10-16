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

    def test_extract_salary_ignores_a_phone_country_code_in_a_contact_string(self):
        # Round 11: Potentia's "Senior Software Engineer - iOS" page advertises
        # no salary, but the contact block renders "+64kayla@potentia.co.nz".
        # The bare "64k" was read as NZD 64,000/yr.
        text = (
            "Meet the Consultant Kayla Moore Senior Talent Manager "
            "View profile +64kayla@potentia.co.nz kayla@potentia.co.nz"
        )

        self.assertIsNone(extract_salary(text))

    def test_extract_salary_ignores_a_file_size(self):
        # A Word/PDF "Job Description (PDF, 198KB)" listing is not pay.
        text = "Digital Workspace Engineer.pdf (PDF, 198KB) Job Description File links:"

        self.assertIsNone(extract_salary(text))

    def test_extract_salary_ignores_a_decimal_k_rate(self):
        # "$87.62 k/yr" is not a salary; a real "$87,624 ... per year" later wins.
        text = "Starting at $87.62 k/yr Hamilton The salary will begin at $87,624 pro-rata, per year."

        self.assertEqual(extract_salary(text), "$87,624")

    def test_extract_salary_ignores_a_headline_quantity(self):
        # "a website with over 500k users" is not a salary.
        text = "Day-to-day management of a website with over 500k users Blend content."

        self.assertIsNone(extract_salary(text))

    def test_extract_salary_still_reads_a_bare_k_salary(self):
        self.assertEqual(extract_salary("Up to 150K plus generous bonus"), "150K")
        self.assertEqual(extract_salary("145K - $160K"), "145K - $160K")


if __name__ == "__main__":
    unittest.main()
