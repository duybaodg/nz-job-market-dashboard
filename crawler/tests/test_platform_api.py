"""Offline tests for the public ATS JSON API mappers.

Every mapper is pure: it receives a ``fetch`` callable, so tests hand it a
recorded payload and assert the normalised ``RawJobInput``-shaped dict.
"""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from adapters.scrapling.platform_api import (  # noqa: E402
    adzuna_jobs,
    oracle_jobs,
    salesforce_jobs,
    tribe_jobs,
    ashby_jobs,
    bamboohr_jobs,
    greenhouse_jobs,
    iso_date,
    lever_jobs,
    relative_date,
    smartrecruiters_jobs,
    workable_jobs,
    workday_jobs,
)


def fetcher(payload):
    def _fetch(url, method="GET", body=None):
        _fetch.calls.append((url, method, body))
        return payload

    _fetch.calls = []
    return _fetch


class PlatformApiTests(unittest.TestCase):
    def test_iso_date_normalises_multiple_shapes(self) -> None:
        self.assertEqual("2026-08-20T00:00:00Z", iso_date("2026-08-20T10:11:12.000+00:00"))
        self.assertEqual("2026-08-20T00:00:00Z", iso_date("2026-08-20"))
        self.assertIsNone(iso_date(None))

    def test_relative_date_parses_workday_text(self) -> None:
        self.assertTrue(relative_date("Posted Today").startswith("20"))
        self.assertIsNotNone(relative_date("Posted 2 Days Ago"))
        self.assertIsNotNone(relative_date("Posted Yesterday"))

    def test_ashby_mapper_normalises_and_filters(self) -> None:
        payload = {
            "jobs": [
                {
                    "id": "abc",
                    "title": "Senior Software Engineer",
                    "department": "Engineering",
                    "employmentType": "FullTime",
                    "workplaceType": "Hybrid",
                    "isListed": True,
                    "descriptionPlain": "Build things",
                    "jobUrl": "https://jobs.ashbyhq.com/acme/abc",
                    "publishedAt": "2026-08-20T00:00:00.000+00:00",
                    "address": {"postalAddress": {"addressLocality": "Auckland", "addressCountry": "New Zealand"}},
                    "compensation": {"scrapeableCompensationSalarySummary": "$130k - $160k"},
                },
                {
                    "id": "skip-country",
                    "title": "Software Engineer",
                    "isListed": True,
                    "jobUrl": "https://jobs.ashbyhq.com/acme/skip",
                    "address": {"postalAddress": {"addressCountry": "Australia"}},
                },
            ]
        }
        fetch = fetcher(payload)

        jobs = ashby_jobs(fetch, source="Acme", token="acme", country="new zealand")

        self.assertEqual(1, len(jobs))
        self.assertEqual("Senior Software Engineer", jobs[0]["title"])
        self.assertEqual("Auckland", jobs[0]["location"])
        self.assertIn("Employment type: Full-time", jobs[0]["descriptionText"])
        self.assertEqual("2026-08-20T00:00:00Z", jobs[0]["postedDate"])

    def test_greenhouse_mapper(self) -> None:
        payload = {
            "jobs": [
                {
                    "id": 123,
                    "title": "Platform Engineer",
                    "absolute_url": "https://boards.greenhouse.io/acme/jobs/123",
                    "location": {"name": "Wellington, NZ"},
                    "content": "<p>Manage <strong>Kubernetes</strong></p>",
                    "first_published": "2026-08-18T00:00:00Z",
                }
            ]
        }
        jobs = greenhouse_jobs(fetcher(payload), source="Acme", token="acme")

        self.assertEqual(1, len(jobs))
        self.assertEqual("Wellington, NZ", jobs[0]["location"])
        self.assertEqual("Manage Kubernetes", jobs[0]["descriptionText"])
        self.assertEqual("123", jobs[0]["sourceJobId"])

    def test_lever_mapper(self) -> None:
        payload = [
            {
                "id": "lev-1",
                "text": "Data Engineer",
                "hostedUrl": "https://jobs.lever.co/acme/lev-1",
                "descriptionPlain": "Airflow and dbt",
                "createdAt": 1755000000000,
                "categories": {"location": "Remote, NZ"},
                "salaryRange": {"min": 110000, "max": 140000, "currency": "NZD"},
            }
        ]
        jobs = lever_jobs(fetcher(payload), source="Acme", token="acme")

        self.assertEqual("Remote, NZ", jobs[0]["location"])
        self.assertEqual("$110000 - $140000 NZD", jobs[0]["salaryText"])
        self.assertIsNotNone(jobs[0]["postedDate"])

    def test_workable_mapper(self) -> None:
        payload = {
            "name": "Acme",
            "jobs": [
                {
                    "shortcode": "AB12",
                    "title": "QA Engineer",
                    "url": "https://apply.workable.com/acme/j/AB12",
                    "description": "<p>Test all the things</p>",
                    "requirements": "<p>Playwright</p>",
                    "location": {"city": "Christchurch", "region": "Canterbury", "country": "New Zealand"},
                    "created_at": "2026-08-19",
                }
            ],
        }
        jobs = workable_jobs(fetcher(payload), source="Acme", token="acme")

        self.assertEqual("Acme", jobs[0]["company"])
        self.assertEqual("Christchurch, Canterbury, New Zealand", jobs[0]["location"])
        self.assertIn("Playwright", jobs[0]["descriptionText"])

    def test_smartrecruiters_mapper(self) -> None:
        payload = {
            "content": [
                {
                    "id": "sr-1",
                    "name": "Cloud Architect",
                    "company": {"name": "Acme"},
                    "location": {"city": "Auckland", "country": "nz"},
                    "releasedDate": "2026-08-15T00:00:00.000Z",
                    "ref": "https://api.smartrecruiters.com/v1/companies/acme/postings/sr-1",
                }
            ]
        }
        jobs = smartrecruiters_jobs(fetcher(payload), source="Acme", token="acme")

        self.assertEqual("Cloud Architect", jobs[0]["title"])
        # The list API returns an API URL in `ref`; the public posting URL is canonical.
        self.assertEqual("https://jobs.smartrecruiters.com/acme/sr-1", jobs[0]["url"])

    def test_bamboohr_mapper(self) -> None:
        payload = {
            "result": [
                {
                    "id": "342",
                    "jobOpeningName": "Expressions of Interest",
                    "employmentStatusLabel": "Full Time",
                    "location": {"city": "Christchurch", "state": "Canterbury"},
                }
            ]
        }
        jobs = bamboohr_jobs(fetcher(payload), source="Jade Software", token="jadeworld", company="Jade Software")

        self.assertEqual("Jade Software", jobs[0]["company"])
        self.assertEqual("Christchurch, Canterbury", jobs[0]["location"])
        self.assertEqual("https://jadeworld.bamboohr.com/careers/342", jobs[0]["url"])

    def test_workday_paginates_and_builds_urls(self) -> None:
        pages = [
            {
                "total": 30,
                "jobPostings": [
                    {
                        "title": "Ranger",
                        "externalPath": "/job/Wellington/Ranger_JR1",
                        "locationsText": "Wellington",
                        "postedOn": "Posted Yesterday",
                        "bulletFields": ["Permanent", "JR1"],
                    }
                ],
            },
            {
                "total": 30,
                "jobPostings": [
                    {
                        "title": "Advisor",
                        "externalPath": "/job/Auckland/Advisor_JR2",
                        "locationsText": "Auckland",
                        "postedOn": "Posted 3 Days Ago",
                        "bulletFields": ["Fixed Term", "JR2"],
                    }
                ],
            },
        ]
        calls = []

        def fetch(url, method="GET", body=None):
            calls.append((url, method, body))
            return pages[min(len(calls) - 1, len(pages) - 1)]

        jobs = workday_jobs(
            fetch,
            source="DOC Careers",
            host="doc.wd105.myworkdayjobs.com",
            tenant="doc",
            site="DOC",
            company="Department of Conservation",
            limit=40,
        )

        self.assertEqual(2, len(jobs))
        self.assertEqual("POST", calls[0][1])
        self.assertEqual("JR1", jobs[0]["sourceJobId"])
        self.assertIn("/en-US/DOC/job/Wellington/Ranger_JR1", jobs[0]["url"])
        self.assertIsNotNone(jobs[0]["postedDate"])

    def test_adzuna_mapper_formats_salary(self) -> None:
        payload = {
            "results": [
                {
                    "id": "ad-1",
                    "title": "DevOps Engineer",
                    "company": {"display_name": "Acme"},
                    "location": {"display_name": "Auckland, New Zealand"},
                    "description": "Terraform and AWS",
                    "redirect_url": "https://www.adzuna.co.nz/jobs/ad-1",
                    "created": "2026-08-19T01:00:00Z",
                    "salary_min": 100000,
                    "salary_max": 130000,
                }
            ]
        }
        jobs = adzuna_jobs(fetcher(payload), source="Adzuna NZ", app_id="id", app_key="key", pages=1)

        self.assertEqual("Acme", jobs[0]["company"])
        self.assertEqual("$100,000 - $130,000", jobs[0]["salaryText"])


    def test_salesforce_apex_mapper_reads_open_vacancies(self) -> None:
        payload = {
            "returnValue": [
                {
                    "Id": "a0RVR00000FVxb32AD",
                    "Name": "Senior Engineer, Networks NZDF-2609039",
                    "Title_Plain__c": "Senior Engineer, Networks",
                    "Vacancy_Category__c": "Engineering",
                    "New_Region__c": "Auckland",
                    "Region_Location__c": "Auckland - Auckland",
                    "Application_Close_Date__c": "2026-10-22",
                    "Work_Type__c": "Full Time",
                }
            ]
        }
        jobs = salesforce_jobs(
            fetcher(payload),
            source="NZ Defence Careers",
            api_url="https://defencecareers.my.site.com/civilianportal/webruntime/api/apex/execute",
            portal_url="https://defencecareers.my.site.com/civilianportal/",
            company="New Zealand Defence Force",
        )

        self.assertEqual(1, len(jobs))
        self.assertEqual("a0RVR00000FVxb32AD", jobs[0]["sourceJobId"])
        self.assertEqual("Senior Engineer, Networks", jobs[0]["title"])
        self.assertEqual("Auckland - Auckland", jobs[0]["location"])
        self.assertEqual("2026-10-22T00:00:00Z", jobs[0]["closingDate"])
        self.assertIn("Full Time", jobs[0]["descriptionText"])

    def test_oracle_mapper_reads_requisition_list(self) -> None:
        payload = {
            "items": [
                {
                    "TotalJobsCount": 2,
                    "requisitionList": [
                        {
                            "Id": "3375",
                            "Title": "Intelligence & Insight Specialist",
                            "PostedDate": "2026-10-08",
                            "PrimaryLocationCountry": "NZ",
                            "PrimaryLocation": "New Zealand",
                        },
                        {
                            "Id": "3376",
                            "Title": "Software Developer",
                            "PostedDate": "2026-10-01",
                            "PrimaryLocationCountry": "AU",
                            "PrimaryLocation": "Sydney",
                        },
                    ],
                }
            ]
        }
        jobs = oracle_jobs(
            fetcher(payload),
            source="Inland Revenue Careers",
            host="ekfu.fa.ap1.oraclecloud.com",
            site="CX_1001",
            company="Inland Revenue",
            country_code="NZ",
        )

        self.assertEqual(1, len(jobs))
        self.assertEqual("3375", jobs[0]["sourceJobId"])
        self.assertEqual("New Zealand", jobs[0]["location"])
        self.assertEqual("2026-10-08T00:00:00Z", jobs[0]["postedDate"])
        self.assertIn("/sites/CX_1001/job/3375", jobs[0]["url"])

    def test_tribe_mapper_builds_detail_urls_and_salary(self) -> None:
        payload = {
            "count": 1,
            "jobs": [
                {
                    "jobId": 18689,
                    "objectID": "job-18689",
                    "title": "Site Reliability Engineer",
                    "brand": "Tribe Tech",
                    "location": "Auckland",
                    "postedAt": "2026-10-09T04:00:00.000Z",
                    "workType": "Permanent",
                    "classifications": ["Information Technology"],
                    "salary": 160000,
                    "salaryUnit": "per annum",
                    "body": ["About the role", "Build reliable platforms."],
                }
            ],
        }
        jobs = tribe_jobs(fetcher(payload), source="Tribe Recruitment")

        self.assertEqual(1, len(jobs))
        self.assertEqual("18689", jobs[0]["sourceJobId"])
        self.assertEqual("Tribe Tech", jobs[0]["company"])
        self.assertEqual("https://www.tribegroup.com/find-a-job/job-listings/18689", jobs[0]["url"])
        self.assertEqual("$160,000 per annum", jobs[0]["salaryText"])
        self.assertIn("Information Technology", jobs[0]["descriptionText"])
        self.assertEqual("2026-10-09T00:00:00Z", jobs[0]["postedDate"])


if __name__ == "__main__":
    unittest.main()
