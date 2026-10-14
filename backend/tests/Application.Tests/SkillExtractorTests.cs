using Application.Jobs;
using FluentAssertions;

namespace Application.Tests;

/// <summary>
/// Round 7: the shared taxonomy must extract a broad mix of technical skills
/// from real (live) no-skill job descriptions while rejecting the classic
/// false positives. Fixtures under <c>crawler/tests/fixtures/skills</c> are the
/// stored descriptions of the live no-skill jobs named in the plan.
/// </summary>
public sealed class SkillExtractorTests
{
    private static readonly SkillExtractor Extractor = new(SkillsTaxonomy.Load(RepoFile("crawler", "skills_taxonomy.json")));

    public static TheoryData<string, string[]> Fixtures => new()
    {
        {
            "westpac-data-engineer.txt",
            new[] { "SQL", "Python", "ETL", "Snowflake", "Oracle", "Apache Kafka" }
        },
        {
            "eroad-security-analyst.txt",
            new[] { "Security", "Cybersecurity", "AWS", "Azure", "GCP", "Python" }
        },
        {
            "digital-workspace-engineer.txt",
            new[] { "ITIL", "IT Service Management", "Technical Support", "Problem Solving" }
        },
        {
            "gallagher-product-manager-cloud.txt",
            new[] { "Cloud Computing", "SaaS", "Product Management", "Stakeholder Management", "Analytics" }
        },
        {
            "beyond-senior-data-engineer.txt",
            new[] { "WhereScape RED", "Databricks", "Data Warehouse", "Business Intelligence", "Data Platform" }
        },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Live_no_skill_fixtures_now_yield_five_or_more_relevant_skills(string fileName, string[] expected)
    {
        var text = File.ReadAllText(RepoFile("crawler", "tests", "fixtures", "skills", fileName));

        var skills = Extractor.Extract(text);
        var names = skills.Select(skill => skill.Name).ToList();

        // The plan's acceptance bar: every previously skill-less job reads as
        // technology-relevant after extraction.
        names.Should().HaveCountGreaterThanOrEqualTo(5, $"'{fileName}' is a technical role");
        names.Should().Contain(expected);
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Extraction_is_capped_so_a_long_description_cannot_become_keyword_soup()
    {
        var text = string.Join(
            '.',
            "C# Python Java JavaScript TypeScript Go Rust Kotlin Swift PHP Ruby Scala R MATLAB SQL T-SQL PL/SQL VBA SAS COBOL",
            "React Angular Vue Next.js Node.js .NET ASP.NET Blazor Svelte Redux Tailwind CSS HTML CSS Bootstrap Spring Django Flask",
            "AWS Azure GCP Kubernetes Docker Terraform Helm Serverless OpenShift Cloudflare VMware Citrix Linux Unix",
            "SQL Server PostgreSQL MySQL Oracle MongoDB Redis Elasticsearch DynamoDB Cassandra SQLite Snowflake Databricks",
            "Power BI Tableau Looker Qlik dbt Airflow Spark Kafka Hadoop SSIS SSRS SSAS Informatica Talend Alteryx",
            "Jenkins GitHub Actions GitLab CI Azure DevOps Ansible Chef Puppet Grafana Prometheus Datadog ELK Nginx",
            "Selenium Cypress Playwright Postman JMeter xUnit NUnit MSTest Jest JUnit pytest Mocha Cucumber SoapUI",
            "Splunk CrowdStrike Okta CyberArk Nessus Qualys Tenable Wireshark PKI IAM SIEM Penetration Testing NIST ISO 27001",
            "Dynamics 365 SAP Xero MYOB Salesforce SharePoint Power Apps Power Automate MuleSoft Boomi REST API SOAP GraphQL");

        var skills = Extractor.Extract(text);

        skills.Should().HaveCount(Extractor.Taxonomy.MaxSkillsPerJob);
        skills.Select(skill => skill.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void R_in_R_and_D_is_not_the_R_language()
    {
        var names = Names("We invest heavily in R&D. Research and development drives our roadmap.");

        names.Should().NotContain("R");
        names.Should().NotContain("Ruby"); // 'R' must not bleed into neighbouring words either
    }

    [Fact]
    public void R_matches_when_it_is_clearly_the_statistical_language()
    {
        var names = Names("Data scientist required: R, Python and SQL for statistical modelling.");

        names.Should().Contain("R");
        names.Should().Contain("Python");
        names.Should().Contain("SQL");
    }

    [Theory]
    [InlineData("We need a go to market strategy for the product launch.")]
    [InlineData("Drive the go-to-market plan with our partners.")]
    public void Go_in_go_to_market_is_not_the_Go_language(string text)
    {
        Names(text).Should().NotContain("Go");
    }

    [Fact]
    public void Go_matches_in_a_development_context()
    {
        var names = Names("Backend engineer with Go, Kubernetes and Docker experience.");

        names.Should().Contain("Go");
        names.Should().Contain("Kubernetes");
    }

    [Fact]
    public void Angular_design_marketing_copy_is_not_the_Angular_framework()
    {
        Names("The building's angular design aesthetic gives the campaign its distinctive look.").Should().NotContain("Angular");
        Names("Front-end developer with Angular, TypeScript and RxJS.").Should().Contain("Angular");
    }

    [Fact]
    public void Ordinary_english_words_are_not_mistaken_for_skills()
    {
        var names = Names("The rest of the team does less than expected and meets each spring to plan the next steps.");

        names.Should().NotContain("REST API");
        names.Should().NotContain("CSS");    // "less" is not the LESS preprocessor
        names.Should().NotContain("Spring");
        names.Should().NotContain("Next.js");
    }

    [Fact]
    public void Maui_place_name_is_not_the_dotnet_maui_framework()
    {
        Names("Based in Te Matau a Maui Hawke's Bay, the role supports the region.").Should().NotContain(".NET MAUI");
    }

    [Theory]
    [InlineData("Postgres and K8s administration.", "PostgreSQL", "Kubernetes")]
    [InlineData("Strong JS and TS skills.", "JavaScript", "TypeScript")]
    [InlineData("5 years with .NET and C#.", ".NET", "C#")]
    [InlineData("PowerBI dashboards and MSSQL.", "Power BI", "SQL Server")]
    public void Aliases_normalise_to_the_canonical_skill_name(string text, params string[] expected)
    {
        Names(text).Should().Contain(expected);
    }

    [Fact]
    public void Alias_variants_do_not_create_duplicate_skill_rows()
    {
        var names = Names("Postgres, PostgreSQL and PostgresQL experience. K8s, Kubernetes and K8S.");

        names.Should().OnlyHaveUniqueItems();
        names.Count(name => name == "PostgreSQL").Should().Be(1);
        names.Count(name => name == "Kubernetes").Should().Be(1);
    }

    [Fact]
    public void Skill_types_come_from_the_shared_taxonomy_categories()
    {
        var skills = Extractor.Extract("Python, React, Azure, PostgreSQL, Power BI, Jenkins, Selenium, Splunk, SAP, Agile");

        skills.Should().Contain(skill => skill.Name == "Python" && skill.Type == "ProgrammingLanguage");
        skills.Should().Contain(skill => skill.Name == "React" && skill.Type == "Framework");
        skills.Should().Contain(skill => skill.Name == "Azure" && skill.Type == "Cloud");
        skills.Should().Contain(skill => skill.Name == "PostgreSQL" && skill.Type == "Database");
        skills.Should().Contain(skill => skill.Name == "Power BI" && skill.Type == "DataTool");
        skills.Should().Contain(skill => skill.Name == "Jenkins" && skill.Type == "DevOps");
        skills.Should().Contain(skill => skill.Name == "Selenium" && skill.Type == "Testing");
        skills.Should().Contain(skill => skill.Name == "Splunk" && skill.Type == "Security");
        skills.Should().Contain(skill => skill.Name == "SAP" && skill.Type == "Integration");
        skills.Should().Contain(skill => skill.Name == "Agile" && skill.Type == "Keyword");
    }

    [Fact]
    public void Taxonomy_file_is_shared_with_the_python_crawler()
    {
        var pythonModule = RepoFile("crawler", "skills_taxonomy.py");

        File.Exists(pythonModule).Should().BeTrue();
        File.ReadAllText(pythonModule).Should().Contain("skills_taxonomy.json");
        File.ReadAllText(RepoFile("crawler", "adapters", "scrapling", "scrapling_worker.py"))
            .Should().Contain("from skills_taxonomy import default_extractor");
    }

    [Fact]
    public void Python_skill_array_deserialises_into_the_dotnet_raw_job_contract()
    {
        const string json = """
            {
              "rawPages": [],
              "jobs": [{
                "source": "Test", "sourceJobId": "1", "title": "Data Engineer",
                "company": "Acme", "location": "Auckland", "salaryText": null,
                "descriptionText": "Python, Azure and SQL Server.",
                "url": "https://example.com/jobs/1", "postedDate": null, "closingDate": null,
                "skills": [
                  {"name": "Python", "type": "ProgrammingLanguage"},
                  {"name": "Azure", "type": "Cloud"}
                ]
              }],
              "isComplete": true
            }
            """;

        var result = System.Text.Json.JsonSerializer.Deserialize<Application.Crawling.SourceFetchResult>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result.Should().NotBeNull();
        var job = result!.Jobs.Single();
        job.Skills.Should().NotBeNull().And.HaveCount(2);
        job.Skills!.Select(skill => (skill.Name, skill.Type)).Should().Contain(("Azure", "Cloud"));

        // The .NET extractor canonicalises exactly what the Python worker sent.
        var normalised = Extractor.NormaliseProvided(job.Skills!);
        normalised.Should().Contain(skill => skill.Name == "Python" && skill.Type == "ProgrammingLanguage");
        normalised.Should().Contain(skill => skill.Name == "Azure" && skill.Type == "Cloud");
    }

    private static List<string> Names(string text) => Extractor.Extract(text).Select(skill => skill.Name).ToList();

    internal static string RepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "crawler", "skills_taxonomy.json")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
        }

        return Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
    }
}
