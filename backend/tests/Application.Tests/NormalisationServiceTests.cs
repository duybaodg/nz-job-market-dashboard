using Application.Normalisation;
using FluentAssertions;

namespace Application.Tests;

public sealed class NormalisationServiceTests
{
    private readonly NormalisationService service = new();

    [Theory]
    [InlineData("Wellington CBD", "Wellington")]
    [InlineData("Christchurch, Canterbury", "Canterbury")]
    [InlineData("Remote - Auckland", "Auckland")]
    [InlineData(null, "Unknown")]
    public void StandardiseRegion_maps_common_new_zealand_locations(string? location, string expected)
    {
        service.StandardiseRegion(location).Should().Be(expected);
    }

    [Theory]
    [InlineData("Graduate Cloud Engineer", "Graduate")]
    [InlineData("Junior Software Developer", "Junior")]
    [InlineData("Senior .NET Engineer", "Senior")]
    [InlineData("Lead Data Engineer", "Lead")]
    [InlineData("Software Developer", "Intermediate")]
    public void StandardiseSeniority_maps_title_keywords(string title, string expected)
    {
        service.StandardiseSeniority(title).Should().Be(expected);
    }
}
