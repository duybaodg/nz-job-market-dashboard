using Application.Jobs;
using FluentAssertions;

namespace Application.Tests;

public sealed class JobQualityGuardTests
{
    [Theory]
    [InlineData("Careers - BNZ", "BNZ")]
    [InlineData("3 jobs found for engineering", "Randstad NZ")]
    [InlineData("Home | New Cops | New Zealand Police", "NZ Police")]
    [InlineData("Working here at One NZ - Careers", "One NZ")]
    [InlineData("Jobspace - Launch Your New Career", "MyJobSpace")]
    [InlineData("Register as a job seeker", "Student Job Search")]
    [InlineData("745 Software Developer jobs in New Zealand", "SEEK NZ")]
    [InlineData("Your Software Engineering Recruitment Specialists | Potentia", "Potentia")]
    [InlineData("Fonterra", "Fonterra")]
    [InlineData("Technology and engineering careers", "ANZ NZ")]
    [InlineData("Developer Opportunities", "Talent Vault")]
    [InlineData("Graduate Jobs and Graduate Programmes (74 open now!)", "GradConnection NZ")]
    [InlineData("", "Any")]
    public void IsLowQualityTitle_flags_hub_and_landing_pages(string title, string source)
    {
        JobQualityGuard.IsLowQualityTitle(title, source).Should().BeTrue();
    }

    [Theory]
    [InlineData("Senior Software Engineer", "Acme")]
    [InlineData("Workday Reporting Specialist", "Salt NZ")]
    [InlineData("IT Help Desk Technician", "SEEK NZ")]
    [InlineData("Fonterra", "Some Other Source")]
    public void IsLowQualityTitle_keeps_real_jobs(string title, string source)
    {
        JobQualityGuard.IsLowQualityTitle(title, source).Should().BeFalse();
    }
}
