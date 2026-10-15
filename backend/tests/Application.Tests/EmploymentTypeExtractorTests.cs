using Application.Jobs;
using FluentAssertions;

namespace Application.Tests;

public sealed class EmploymentTypeExtractorTests
{
    [Theory]
    [InlineData("Employment type: Fixed-Term", "Fixed-term")]
    [InlineData("Employment Type: Part-Time", "Part-time")]
    [InlineData("Employment type: Contract", "Contract")]
    public void A_labelled_field_wins(string description, string expected)
    {
        var result = EmploymentTypeExtractor.Extract(null, description);

        result.Value.Should().Be(expected);
        result.Source.Should().Be("statement");
    }

    [Theory]
    [InlineData("This is a fixed-term contract for 12 months.", "Fixed-term")]
    [InlineData("We are hiring a permanent full-time engineer.", "Full-time")]
    [InlineData("Casual work available over summer.", "Casual")]
    [InlineData("A 6-month contract role with a day rate.", "Contract")]
    [InlineData("Permanent opportunity in Auckland.", "Permanent")]
    [InlineData("Our internship programme starts in November.", "Internship")]
    public void Description_wording_is_detected(string description, string expected)
    {
        EmploymentTypeExtractor.Extract(null, description).Value.Should().Be(expected);
    }

    [Fact]
    public void Nothing_stated_is_unknown()
    {
        EmploymentTypeExtractor.Extract("Great team.", "Apply now.").Value.Should().Be("Unknown");
    }
}
