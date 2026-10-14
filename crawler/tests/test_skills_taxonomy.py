import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from skills_taxonomy import SkillExtractor, extract_skills  # noqa: E402


FIXTURES = Path(__file__).parent / "fixtures" / "skills"


class SkillExtractorFixtureTests(unittest.TestCase):
    """The live no-skill jobs named in the Round 7 plan now extract skills."""

    FIXTURE_EXPECTATIONS = {
        "westpac-data-engineer.txt": {"SQL", "Python", "ETL", "Snowflake", "Oracle", "Apache Kafka"},
        "eroad-security-analyst.txt": {"Cybersecurity", "AWS", "Azure", "GCP", "Python", "Linux"},
        "digital-workspace-engineer.txt": {"ITIL", "IT Service Management", "Technical Support", "Problem Solving"},
        "gallagher-product-manager-cloud.txt": {
            "Cloud Computing",
            "SaaS",
            "Product Management",
            "Stakeholder Management",
            "Analytics",
        },
        "beyond-senior-data-engineer.txt": {
            "WhereScape RED",
            "Databricks",
            "Data Warehouse",
            "Business Intelligence",
            "Data Platform",
        },
    }

    def setUp(self) -> None:
        self.extractor = SkillExtractor()

    def test_each_live_no_skill_fixture_yields_five_or_more_relevant_skills(self) -> None:
        for name, expected in self.FIXTURE_EXPECTATIONS.items():
            with self.subTest(fixture=name):
                text = (FIXTURES / name).read_text(encoding="utf-8")
                names = [skill["name"] for skill in self.extractor.extract(text)]

                self.assertGreaterEqual(len(names), 5, f"{name} is a technical role")
                self.assertTrue(expected.issubset(set(names)), f"missing {expected - set(names)}")
                self.assertEqual(len(names), len(set(names)), "canonical skills must be unique")

    def test_result_is_capped(self) -> None:
        text = (
            "C# Python Java JavaScript TypeScript Go Rust Kotlin Swift PHP Ruby Scala R MATLAB SQL T-SQL PL/SQL "
            "React Angular Vue Next.js Node.js .NET ASP.NET Blazor Svelte Redux Tailwind CSS HTML CSS Bootstrap "
            "AWS Azure GCP Kubernetes Docker Terraform Helm Serverless OpenShift Cloudflare VMware Citrix Linux "
            "SQL Server PostgreSQL MySQL Oracle MongoDB Redis Elasticsearch DynamoDB Cassandra Snowflake Databricks "
            "Power BI Tableau Looker Qlik dbt Airflow Kafka Hadoop SSIS SSRS SSAS Informatica Talend Alteryx "
            "Jenkins GitHub Actions GitLab CI Azure DevOps Ansible Chef Puppet Grafana Prometheus Datadog ELK Nginx "
            "Selenium Cypress Playwright Postman JMeter xUnit NUnit MSTest Jest JUnit pytest Mocha Cucumber SoapUI "
            "Splunk CrowdStrike Okta CyberArk Nessus Qualys Tenable Wireshark PKI IAM SIEM NIST ISO 27001 "
            "Dynamics 365 SAP Xero MYOB Salesforce SharePoint Power Apps Power Automate MuleSoft Boomi REST API SOAP"
        )
        skills = self.extractor.extract(text)

        self.assertEqual(len(skills), self.extractor.max_skills_per_job)
        self.assertEqual(len(skills), len({skill["name"] for skill in skills}))


class SkillFalsePositiveTests(unittest.TestCase):
    def test_r_in_r_and_d_is_not_the_r_language(self) -> None:
        names = [skill["name"] for skill in extract_skills("We invest heavily in R&D and research and development.")]
        self.assertNotIn("R", names)

    def test_r_matches_in_a_statistics_context(self) -> None:
        names = [skill["name"] for skill in extract_skills("Data scientist: R, Python and SQL for modelling.")]
        self.assertIn("R", names)

    def test_go_in_go_to_market_is_not_the_go_language(self) -> None:
        names = [skill["name"] for skill in extract_skills("We need a go to market strategy for the launch.")]
        self.assertNotIn("Go", names)

    def test_go_matches_in_a_development_context(self) -> None:
        names = [skill["name"] for skill in extract_skills("Backend engineer with Go and Kubernetes.")]
        self.assertIn("Go", names)

    def test_angular_design_marketing_copy_is_not_the_framework(self) -> None:
        names = [skill["name"] for skill in extract_skills("The angular design aesthetic shapes the campaign.")]
        self.assertNotIn("Angular", names)

    def test_ordinary_english_is_not_mistaken_for_skills(self) -> None:
        names = [
            skill["name"]
            for skill in extract_skills(
                "The rest of the team does less than expected and meets each spring to plan next steps."
            )
        ]
        self.assertNotIn("REST API", names)
        self.assertNotIn("CSS", names)  # "less" is not the LESS preprocessor
        self.assertNotIn("Spring", names)
        self.assertNotIn("Next.js", names)

    def test_maui_place_name_is_not_the_dotnet_maui_framework(self) -> None:
        names = [skill["name"] for skill in extract_skills("Based in Te Matau a Maui Hawke's Bay.")]
        self.assertNotIn(".NET MAUI", names)


class SkillNormalisationTests(unittest.TestCase):
    def test_aliases_normalise_to_canonical_names(self) -> None:
        skills = extract_skills("Postgres, K8s, JS, TS, .NET, PowerBI and MSSQL.")
        by_name = {skill["name"]: skill["type"] for skill in skills}

        self.assertIn("PostgreSQL", by_name)
        self.assertIn("Kubernetes", by_name)
        self.assertIn("JavaScript", by_name)
        self.assertIn("TypeScript", by_name)
        self.assertIn(".NET", by_name)
        self.assertIn("Power BI", by_name)
        self.assertIn("SQL Server", by_name)

    def test_alias_variants_do_not_create_duplicates(self) -> None:
        skills = extract_skills("Postgres, PostgreSQL and PostgresQL plus K8s, Kubernetes and K8S.")
        names = [skill["name"] for skill in skills]

        self.assertEqual(names.count("PostgreSQL"), 1)
        self.assertEqual(names.count("Kubernetes"), 1)

    def test_types_come_from_the_shared_categories(self) -> None:
        skills = {skill["name"]: skill["type"] for skill in extract_skills(
            "Python, React, Azure, PostgreSQL, Power BI, Jenkins, Selenium, Splunk, SAP, Agile"
        )}

        self.assertEqual("ProgrammingLanguage", skills["Python"])
        self.assertEqual("Framework", skills["React"])
        self.assertEqual("Cloud", skills["Azure"])
        self.assertEqual("Database", skills["PostgreSQL"])
        self.assertEqual("DataTool", skills["Power BI"])
        self.assertEqual("DevOps", skills["Jenkins"])
        self.assertEqual("Testing", skills["Selenium"])
        self.assertEqual("Security", skills["Splunk"])
        self.assertEqual("Integration", skills["SAP"])
        self.assertEqual("Keyword", skills["Agile"])


class WorkerSkillAttachmentTests(unittest.TestCase):
    def test_attach_skills_enriches_every_job_in_the_cli_result(self) -> None:
        from adapters.scrapling.scrapling_worker import attach_skills

        result = {
            "rawPages": [],
            "jobs": [
                {
                    "title": "Senior Data Engineer",
                    "descriptionText": "Python, Azure and Snowflake on Kubernetes.",
                },
                {"title": "Marketing Manager", "descriptionText": "Own the go to market plan."},
            ],
        }

        enriched = attach_skills(result)
        names = {skill["name"] for skill in enriched["jobs"][0]["skills"]}

        self.assertTrue({"Python", "Azure", "Snowflake", "Kubernetes"}.issubset(names))
        # A go-to-market role must not pick up the Go language.
        self.assertNotIn("Go", {skill["name"] for skill in enriched["jobs"][1]["skills"]})


if __name__ == "__main__":
    unittest.main()
