using Application.Jobs;
using FluentAssertions;

namespace Application.Tests;

public sealed class SeniorityExtractorTests
{
    [Theory]
    [InlineData("Senior Software Engineer", "Senior")]
    [InlineData("Snr Developer", "Senior")]
    [InlineData("Technical Lead", "Lead")]
    [InlineData("Principal Engineer", "Lead")]
    [InlineData("Head of Engineering", "Lead")]
    [InlineData("Staff Software Engineer", "Lead")]
    [InlineData("Junior Developer", "Junior")]
    [InlineData("Firmware Internship 2026/2027", "Junior")]
    [InlineData("Readygrad - Data Analyst Internship Program 2026", "Junior")]
    [InlineData("Graduate Developer", "Junior")]
    [InlineData("Software Engineer", "Unknown")]
    public void Title_markers_classify_the_role(string title, string expected)
    {
        var result = SeniorityExtractor.Extract(title, null, null);

        result.Value.Should().Be(expected);
        if (expected != "Unknown")
        {
            result.Source.Should().Be("title");
        }
    }

    [Fact]
    public void A_graduate_programme_in_the_description_is_junior()
    {
        var result = SeniorityExtractor.Extract(
            "Technology Analyst", null,
            "Join our 2027 graduate programme and build your career.");

        result.Value.Should().Be("Junior");
        result.Source.Should().Be("description");
    }

    [Theory]
    [InlineData("You will have 1 year of experience.", "Junior")]
    [InlineData("We need 3 years of experience in a similar role.", "Intermediate")]
    [InlineData("At least 5 years of experience with .NET.", "Senior")]
    [InlineData("Minimum of 10 years of experience leading teams.", "Lead")]
    public void Years_of_experience_bands_apply_when_the_title_has_no_marker(string description, string expected)
    {
        var result = SeniorityExtractor.Extract("Software Engineer", null, description);

        result.Value.Should().Be(expected);
        result.Source.Should().Be("description");
    }

    [Fact]
    public void A_very_wide_experience_band_is_ignored_rather_than_inflated()
    {
        var result = SeniorityExtractor.Extract(
            "Software Engineer", null, "Skills & Experience 2-8+ years' experience in software engineering.");

        result.Value.Should().Be("Unknown");
    }

    [Fact]
    public void A_band_uses_its_lower_edge_as_the_minimum_requirement()
    {
        SeniorityExtractor.Extract("Analyst", null, "You will have 3-5 years of experience.")
            .Value.Should().Be("Intermediate");
        SeniorityExtractor.Extract("Engineer", null, "You will have 5-8 years of experience.")
            .Value.Should().Be("Senior");
    }

    [Fact]
    public void An_explicit_title_marker_beats_description_years()
    {
        var result = SeniorityExtractor.Extract(
            "Senior Software Engineer", null, "Minimum of 12 years of experience.");

        result.Value.Should().Be("Senior");
        result.Source.Should().Be("title");
    }

    [Fact]
    public void Company_name_never_influences_seniority()
    {
        // "Salt" is an agency, not a seniority cue; only the title is scanned.
        SeniorityExtractor.Extract("Developer at Salt", null, null).Value.Should().Be("Unknown");
    }

    [Fact]
    public void HasExplicitTitleMarker_distinguishes_real_values_from_the_default()
    {
        SeniorityExtractor.HasExplicitTitleMarker("Senior Developer").Should().BeTrue();
        SeniorityExtractor.HasExplicitTitleMarker("Intermediate Developer").Should().BeTrue();
        SeniorityExtractor.HasExplicitTitleMarker("Software Engineer").Should().BeFalse();
    }

    [Fact]
    public void An_explicit_intermediate_title_is_not_overridden_by_description_years()
    {
        var result = SeniorityExtractor.Extract(
            "Intermediate Software Engineer", null, "You will have 8 years of experience.");

        result.Value.Should().Be("Intermediate");
        result.Source.Should().Be("title");
    }

    [Fact]
    public void Company_history_years_are_not_a_candidate_seniority_signal()
    {
        // "over 28 years' experience servicing the Nelson region" describes the
        // business, not the applicant.
        var result = SeniorityExtractor.Extract(
            "Computer Technician", null,
            "Bell Computers is an established IT Service Provider with over 28 years' experience servicing the region.");

        result.Value.Should().Be("Unknown");
    }
}
