using Application.Jobs;
using FluentAssertions;

namespace Application.Tests;

public sealed class RoleFamilyClassifierTests
{
    [Theory]
    [InlineData("Senior Software Engineer", "Software Engineering")]
    [InlineData("Full Stack Developer", "Software Engineering")]
    [InlineData("Data Engineer", "Data & Analytics")]
    [InlineData("Business Intelligence Analyst", "Data & Analytics")]
    [InlineData("Site Reliability Engineer", "DevOps & SRE")]
    [InlineData("Test Analyst", "Testing & QA")]
    [InlineData("Network Engineer", "Networking")]
    [InlineData("Solutions Architect", "Architecture")]
    [InlineData("Product Manager", "Product & Project")]
    [InlineData("Business Analyst", "Business Analysis")]
    [InlineData("Head of Engineering", "Management & Leadership")]
    [InlineData("Cyber Security Analyst", "Security")]
    [InlineData("Service Desk Analyst", "Support & IT Operations")]
    [InlineData("UX Designer", "Design & UX")]
    public void Classifies_known_titles(string title, string expected)
    {
        var result = RoleFamilyClassifier.Classify(title);

        result.Family.Should().Be(expected);
        result.Inferred.Should().BeTrue("the family is derived from the title, not source data");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Warehouse Team Member")]
    public void Falls_back_to_unknown(string title)
    {
        RoleFamilyClassifier.Classify(title).Family.Should().Be(RoleFamilyClassifier.UnknownFamily);
    }

    [Fact]
    public void Every_family_is_reachable_and_listed_once()
    {
        RoleFamilyClassifier.Families.Should().Contain(RoleFamilyClassifier.UnknownFamily);
        RoleFamilyClassifier.Families.Should().OnlyHaveUniqueItems();
    }
}
