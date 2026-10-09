import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from argparse import Namespace

from adapters.scrapling.scrapling_worker import (
    crawl,
    extract_links,
    is_job_link,
    parse_airnz_detail,
    parse_airnz_listing,
    parse_beyond_recruitment_detail,
    parse_beyond_recruitment_listing,
    parse_comspek_detail,
    parse_comspek_listing,
    parse_consult_recruitment_detail,
    parse_consult_recruitment_listing,
    parse_digital_garage_detail,
    parse_digital_garage_listing,
    parse_halter_ashby,
    parse_job_detail,
    parse_jobs,
    parse_jobseek_detail,
    parse_jobseek_listing,
    parse_salt_detail,
    parse_salt_listing,
)


FIXTURES = Path(__file__).parent / "fixtures"


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

    def test_crawl_follows_next_page_then_fetches_job_details_with_delay(self) -> None:
        pages = {
            "https://example.com/jobs": (
                '<a href="/jobs/1">Senior .NET Engineer</a><a rel="next" href="/jobs?page=2">Next</a>',
                "Jobs",
            ),
            "https://example.com/jobs?page=2": (
                '<a href="/jobs/2">Graduate Cloud Engineer</a>',
                "Jobs",
            ),
            "https://example.com/jobs/1": (
                "<h1>Senior .NET Engineer</h1>",
                "Company: Harbour Systems\nLocation: Auckland\n$125k - $150k",
            ),
            "https://example.com/jobs/2": (
                "<h1>Graduate Cloud Engineer</h1>",
                "Company: Cloud Kiwi\nLocation: Hamilton\nRemote Azure role",
            ),
        }
        delays: list[float] = []
        args = Namespace(
            source="Example Jobs",
            url="https://example.com/jobs",
            parser="generic",
            fetcher_type="fetcher",
            max_pages=4,
            delay_seconds=2,
            robots_txt_obey=False,
            proxy_url="",
            user_agent="test",
        )

        result = crawl(args, fetch=pages.__getitem__, sleeper=delays.append)

        self.assertEqual(4, len(result["rawPages"]))
        self.assertEqual(["Senior .NET Engineer", "Graduate Cloud Engineer"], [job["title"] for job in result["jobs"]])
        self.assertEqual([2, 2, 2], delays)
        self.assertTrue(all(job["source"] == "Example Jobs" for job in result["jobs"]))
        self.assertTrue(result["isComplete"])

    def test_govt_nz_parser_uses_jobposting_json(self) -> None:
        html = """
        <script type="application/ld+json">
        {"@type":"JobPosting","title":"Scrum Master","datePosted":"2026-08-21",
         "validThrough":"2026-08-26","description":"Azure delivery role",
         "employmentType":"FULL_TIME",
         "jobLocation":{"address":{"addressLocality":"Wellington Central","addressRegion":"Wellington","addressCountry":"NZ"}},
         "hiringOrganization":{"name":"The Treasury"},
         "identifier":{"value":"45641"}}
        </script>
        """

        job = parse_job_detail("NZ Government Jobs", "https://jobs.govt.nz/jobs/45641", html, "", "ignored")

        self.assertEqual("Scrum Master", job.title)
        self.assertEqual("The Treasury", job.company)
        self.assertEqual("Wellington Central, Wellington, NZ", job.location)
        self.assertEqual("45641", job.sourceJobId)
        self.assertEqual("2026-08-26T00:00:00Z", job.closingDate)

    def test_absolute_it_parser_uses_job_url_and_detail_fields(self) -> None:
        listing = (FIXTURES / "absolute_it_listing.html").read_text()
        detail = (FIXTURES / "absolute_it_detail.html").read_text()
        self.assertTrue(is_job_link("absolute-it", "Read more", "https://absoluteit.co.nz/it-job/bh-145517-x/"))

        job = parse_job_detail(
            "Absolute IT", "https://absoluteit.co.nz/it-job/bh-145517-x/", detail, "", "Read more", "absolute-it"
        )

        self.assertIn("/it-job/", listing)
        self.assertEqual(("Technical Lead", "Christchurch", "BH-145517"), (job.title, job.location, job.sourceJobId))
        self.assertEqual("2026-08-19T00:00:00Z", job.postedDate)
        self.assertEqual("$130,000 - $150,000", job.salaryText)

    def test_talent_army_parser_filters_and_extracts_detail_fields(self) -> None:
        listing = (FIXTURES / "talent_army_listing.html").read_text()
        detail = (FIXTURES / "talent_army_detail.html").read_text()
        links = [
            link
            for title, link in extract_links(listing, "https://talent.army/job-board")
            if is_job_link("talent-army", title, link)
        ]

        job = parse_job_detail(
            "Talent Army", "https://talent.army/job-ads/1986403", detail, "", "ignored", "talent-army"
        )

        self.assertEqual(["https://talent.army/job-ads/1986403"], links)
        self.assertEqual(("Principal Software Developer", "Wellington", "1986403"), (job.title, job.location, job.sourceJobId))
        self.assertEqual("2026-08-19T00:00:00Z", job.postedDate)
        self.assertTrue(job.descriptionText.startswith("Permanent."))

    def test_halter_ashby_parser_keeps_only_nz_technology_jobs(self) -> None:
        payload = (FIXTURES / "halter_ashby.json").read_text()

        jobs = parse_halter_ashby("Halter", payload)
        result = crawl(
            Namespace(
                source="Halter",
                url="https://api.ashbyhq.com/posting-api/job-board/halter",
                parser="halter-ashby",
                fetcher_type="fetcher",
                max_pages=1,
                delay_seconds=2,
                robots_txt_obey=True,
                proxy_url="",
                user_agent="test",
            ),
            fetch=lambda _: (payload, payload),
        )

        self.assertEqual(1, len(jobs))
        self.assertEqual(("nz-tech-1", "Auckland", "Halter"), (jobs[0].sourceJobId, jobs[0].location, jobs[0].company))
        self.assertEqual("2026-08-20T00:00:00Z", jobs[0].postedDate)
        self.assertEqual("$130,000 - $160,000", jobs[0].salaryText)
        self.assertTrue(result["isComplete"])
        self.assertEqual(payload, result["rawPages"][0]["rawJson"])

    def test_beyond_recruitment_listing_extracts_title_location_and_urls(self) -> None:
        listing = (FIXTURES / "beyond_recruitment_listing.html").read_text()

        jobs = parse_beyond_recruitment_listing(
            "Beyond Recruitment", "https://www.beyondrecruitment.co.nz/jobs/it-transformation-and-digital", listing, ""
        )

        self.assertEqual(3, len(jobs))
        self.assertEqual(("Senior Data Engineer", "Auckland"), (jobs[0].title, jobs[0].location))
        self.assertEqual("https://www.beyondrecruitment.co.nz/job/senior-data-engineer-5983983", jobs[0].url)
        self.assertEqual(("Solutions Architect", "Wellington"), (jobs[1].title, jobs[1].location))
        self.assertEqual(("Lead Java Developer", "Christchurch"), (jobs[2].title, jobs[2].location))

    def test_beyond_recruitment_detail_parser_extracts_all_fields(self) -> None:
        detail = (FIXTURES / "beyond_recruitment_detail.html").read_text()

        job = parse_beyond_recruitment_detail(
            "Beyond Recruitment",
            "https://www.beyondrecruitment.co.nz/job/solutions-architect-5979056",
            detail,
            "",
            "ignored",
        )

        self.assertEqual("Solutions Architect", job.title)
        self.assertEqual("Beyond Recruitment", job.company)
        self.assertEqual("Wellington", job.location)
        self.assertEqual("133087", job.sourceJobId)
        self.assertEqual("2026-07-29T00:00:00Z", job.postedDate)
        self.assertIn("Solution Architects", job.descriptionText or "")

    def test_beyond_recruitment_job_link_filter(self) -> None:
        self.assertTrue(
            is_job_link("beyond-recruitment", "Senior Data Engineer", "https://www.beyondrecruitment.co.nz/job/senior-data-engineer-5983983")
        )
        self.assertFalse(
            is_job_link("beyond-recruitment", "Save", "https://www.beyondrecruitment.co.nz/job/senior-data-engineer-5983983/save_job")
        )
        self.assertFalse(
            is_job_link("beyond-recruitment", "Apply", "https://www.beyondrecruitment.co.nz/job/senior-data-engineer-5983983/apply")
        )

    def test_beyond_recruitment_crawl_from_listing_to_details(self) -> None:
        listing = (FIXTURES / "beyond_recruitment_listing.html").read_text()
        detail = (FIXTURES / "beyond_recruitment_detail.html").read_text()
        pages = {
            "https://www.beyondrecruitment.co.nz/jobs/it-transformation-and-digital": (listing, listing),
            "https://www.beyondrecruitment.co.nz/job/senior-data-engineer-5983983": (detail, detail),
            "https://www.beyondrecruitment.co.nz/job/solutions-architect-5979056": (detail, detail),
            "https://www.beyondrecruitment.co.nz/job/lead-java-developer-5977776": (detail, detail),
        }
        args = Namespace(
            source="Beyond Recruitment",
            url="https://www.beyondrecruitment.co.nz/jobs/it-transformation-and-digital",
            parser="beyond-recruitment",
            fetcher_type="fetcher",
            max_pages=5,
            delay_seconds=1,
            robots_txt_obey=False,
            proxy_url="",
            user_agent="test",
        )

        result = crawl(args, fetch=pages.__getitem__, sleeper=lambda _: None)

        self.assertTrue(result["isComplete"])
        self.assertGreaterEqual(len(result["jobs"]), 3)
        self.assertEqual("Beyond Recruitment", result["jobs"][0]["source"])

    def test_beyond_recruitment_detail_via_parse_job_detail(self) -> None:
        detail = (FIXTURES / "beyond_recruitment_detail.html").read_text()

        job = parse_job_detail(
            "Beyond Recruitment",
            "https://www.beyondrecruitment.co.nz/job/solutions-architect-5979056",
            detail,
            "",
            "ignored",
            "beyond-recruitment",
        )

        self.assertEqual("Solutions Architect", job.title)
        self.assertEqual("Wellington", job.location)

    def test_consult_recruitment_listing_extracts_it_jobs(self) -> None:
        listing = (FIXTURES / "consult_recruitment_listing.html").read_text()

        jobs = parse_consult_recruitment_listing(
            "Consult Recruitment", "https://www.consult.co.nz/jobs/", listing, ""
        )

        self.assertGreaterEqual(len(jobs), 6)
        titles = [j.title for j in jobs]
        self.assertIn("Project Manager", titles[0] if titles else "")
        self.assertIn("Security Architect", titles)
        # Verify data attributes mapped
        it_jobs = [j for j in jobs if "Security Architect" in j.title]
        self.assertEqual(1, len(it_jobs))
        self.assertIsNotNone(it_jobs[0].url)
        self.assertIn("/job/", it_jobs[0].url)

    def test_consult_recruitment_detail_parser_extracts_fields(self) -> None:
        detail = (FIXTURES / "consult_recruitment_detail.html").read_text()

        job = parse_consult_recruitment_detail(
            "Consult Recruitment",
            "https://www.consult.co.nz/job/security-architect/",
            detail,
            "",
            "ignored",
        )

        self.assertIn("Security Architect", job.title)
        self.assertIsNotNone(job.location)
        self.assertIsNotNone(job.descriptionText)
        self.assertIn("security", (job.descriptionText or "").lower())

    def test_consult_recruitment_job_link_filter(self) -> None:
        self.assertTrue(
            is_job_link("consult-recruitment", "See job", "https://www.consult.co.nz/job/security-architect/")
        )
        self.assertFalse(
            is_job_link("consult-recruitment", "Register", "https://www.consult.co.nz/jobs/register-candidate/")
        )

    def test_comspek_listing_extracts_job_cards(self) -> None:
        listing = (FIXTURES / "comspek_listing.html").read_text()

        jobs = parse_comspek_listing(
            "Comspek International", "https://www.comspek.co.nz/jobs", listing, ""
        )

        self.assertEqual(3, len(jobs))
        self.assertEqual(("Data Engineer", "Auckland"), (jobs[0].title, jobs[0].location))
        self.assertEqual(("Network Engineer", "Wellington"), (jobs[1].title, jobs[1].location))

    def test_comspek_detail_parser_extracts_fields(self) -> None:
        detail = (FIXTURES / "comspek_detail.html").read_text()

        job = parse_comspek_detail(
            "Comspek International",
            "https://www.comspek.co.nz/job/data-engineer-679327",
            detail,
            "",
            "ignored",
        )

        self.assertEqual("Data Engineer", job.title)
        self.assertIn("data", (job.descriptionText or "").lower())
        self.assertIn("679327", job.sourceJobId or "")

    def test_comspek_job_link_filter(self) -> None:
        self.assertTrue(
            is_job_link("comspek", "Data Engineer", "https://www.comspek.co.nz/job/data-engineer-679327")
        )
        self.assertFalse(
            is_job_link("comspek", "Save", "https://www.comspek.co.nz/job/data-engineer-679327/save_job")
        )

    def test_salt_listing_extracts_job_cards(self) -> None:
        listing = (FIXTURES / "salt_listing.html").read_text()

        jobs = parse_salt_listing(
            "Salt", "https://welovesalt.com/job-category/new-zealand/technology-new-zealand", listing, ""
        )

        self.assertGreaterEqual(len(jobs), 1)
        titles = [j.title for j in jobs]
        self.assertIn("Senior Data Engineer (Enterprise data platform)", titles)

    def test_salt_detail_parser_uses_schema_org(self) -> None:
        detail = (FIXTURES / "salt_detail.html").read_text()

        job = parse_salt_detail(
            "Salt",
            "https://welovesalt.com/jobs/stakeholder-engagement-specialist-714717",
            detail,
            "",
            "ignored",
        )

        self.assertIn("Stakeholder Engagement Specialist", job.title)
        self.assertIsNotNone(job.descriptionText)
        self.assertIn("New Zealand", (job.location or ""))

    def test_salt_job_link_filter(self) -> None:
        self.assertTrue(
            is_job_link("salt", "Read more", "https://welovesalt.com/jobs/stakeholder-engagement-specialist-714717")
        )
        self.assertFalse(
            is_job_link("salt", "Category", "https://welovesalt.com/job-category/new-zealand/")
        )

    def test_digital_garage_listing_extracts_jobs_from_links(self) -> None:
        listing = (FIXTURES / "digital_garage_listing.html").read_text()

        jobs = parse_digital_garage_listing(
            "Digital Garage", "https://digitalgarage.co.nz/jobs", listing, ""
        )

        self.assertGreaterEqual(len(jobs), 1)
        titles = [j.title for j in jobs]
        self.assertIn("Senior Net Developer", titles)
        # Verify source job ID is extracted from URL
        self.assertEqual("1112438", jobs[0].sourceJobId)

    def test_digital_garage_detail_parser_extracts_fields(self) -> None:
        detail = (FIXTURES / "digital_garage_detail.html").read_text()
        import re
        text = re.sub(r'<[^>]+>', ' ', detail)
        text = re.sub(r'\s+', ' ', text).strip()

        job = parse_digital_garage_detail(
            "Digital Garage",
            "https://digitalgarage.co.nz/jobs/1112438-senior-net-developer",
            detail,
            text,
            "ignored",
        )

        self.assertIn("Senior .net Developer", job.title)
        self.assertIsNotNone(job.descriptionText)
        self.assertIn("C#", job.descriptionText or "")
        self.assertEqual("1112438", job.sourceJobId)

    def test_digital_garage_job_link_filter(self) -> None:
        self.assertTrue(
            is_job_link("digital-garage", "Senior .net Developer",
                       "https://digitalgarage.co.nz/jobs/1112438-senior-net-developer")
        )
        self.assertFalse(
            is_job_link("digital-garage", "Find talent",
                       "https://digitalgarage.co.nz/find-talent")
        )

    def test_jobseek_listing_extracts_it_jobs(self) -> None:
        listing = (FIXTURES / "jobseek_listing.html").read_text()

        jobs = parse_jobseek_listing(
            "JobSeek", "https://jobseek.nz/Jobs", listing, ""
        )

        self.assertGreaterEqual(len(jobs), 4)
        titles = [j.title for j in jobs]
        self.assertIn("IT Project Manager", titles)
        self.assertIn("Senior Network Engineer", titles)

    def test_jobseek_detail_parser_extracts_fields(self) -> None:
        detail = (FIXTURES / "jobseek_detail.html").read_text()
        import re
        text = re.sub(r'<[^>]+>', ' ', detail)
        text = re.sub(r'\s+', ' ', text).strip()

        job = parse_jobseek_detail(
            "JobSeek",
            "https://jobseek.nz/jobs/it-project-manager-canterbury-1",
            detail,
            text,
            "ignored",
        )

        self.assertEqual("IT Project Manager", job.title)
        self.assertIsNotNone(job.location)
        self.assertIn("Canterbury", job.location or "")
        self.assertIsNotNone(job.descriptionText)

    def test_jobseek_job_link_filter(self) -> None:
        self.assertTrue(
            is_job_link("jobseek", "IT Project Manager",
                       "https://jobseek.nz/jobs/it-project-manager-canterbury-1")
        )
        self.assertFalse(
            is_job_link("jobseek", "Page 2",
                       "https://jobseek.nz/Jobs?page=2")
        )

    def test_airnz_listing_extracts_jobs(self) -> None:
        listing = (FIXTURES / "airnz_listing.html").read_text()

        jobs = parse_airnz_listing(
            "Air New Zealand", "https://careers.airnewzealand.co.nz/jobs", listing, ""
        )

        self.assertGreaterEqual(len(jobs), 3)
        titles = [j.title for j in jobs]
        self.assertIn("Senior AI Engineer", titles)
        self.assertIn("Marketing Data and Technology Specialist", titles)

    def test_airnz_detail_parser_uses_schema_org(self) -> None:
        detail = (FIXTURES / "airnz_detail.html").read_text()
        import re
        text = re.sub(r'<[^>]+>', ' ', detail)
        text = re.sub(r'\s+', ' ', text).strip()

        job = parse_airnz_detail(
            "Air New Zealand",
            "https://careers.airnewzealand.co.nz/job/senior-ai-engineer-in-auckland-nz-jid-830",
            detail,
            text,
            "ignored",
        )

        self.assertIn("Senior AI Engineer", job.title)
        self.assertEqual("Air New Zealand", job.company)
        self.assertIsNotNone(job.location)
        self.assertIn("Auckland", job.location or "")
        self.assertIn("Permanent", job.descriptionText or "")

    def test_airnz_job_link_filter(self) -> None:
        self.assertTrue(
            is_job_link("airnz", "Senior AI Engineer",
                       "https://careers.airnewzealand.co.nz/job/senior-ai-engineer-in-auckland-nz-jid-830")
        )
        self.assertFalse(
            is_job_link("airnz", "Browse Jobs",
                       "https://careers.airnewzealand.co.nz/jobs")
        )
