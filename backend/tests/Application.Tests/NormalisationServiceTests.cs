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
    // City names map to their region.
    [InlineData("Hamilton", "Waikato")]
    [InlineData("Dunedin", "Otago")]
    [InlineData("Queenstown", "Otago")]
    [InlineData("Tauranga", "Bay of Plenty")]
    [InlineData("Palmerston North", "Manawatū-Whanganui")]
    [InlineData("Napier", "Hawke's Bay")]
    [InlineData("New Plymouth", "Taranaki")]
    [InlineData("Whangārei", "Northland")]
    [InlineData("Gisborne", "Gisborne")]
    [InlineData("Nelson", "Nelson")]
    [InlineData("Blenheim", "Marlborough")]
    [InlineData("Greymouth", "West Coast")]
    [InlineData("Invercargill", "Southland")]
    [InlineData("Timaru", "Canterbury")]
    // City wins over a trailing country/remote marker.
    [InlineData("Auckland, New Zealand", "Auckland")]
    [InlineData("Remote - Wellington", "Wellington")]
    // Explicit remote / NZ-wide handling.
    [InlineData("Remote", "Remote")]
    [InlineData("Work from home", "Remote")]
    [InlineData("New Zealand", "New Zealand")]
    [InlineData("Nationwide", "New Zealand")]
    // Missing location defaults to the country-level convention.
    [InlineData(null, "New Zealand")]
    [InlineData("", "New Zealand")]
    // Present but unrecognisable stays distinct from a data hole.
    [InlineData("Somewhereville", "Other")]
    public void StandardiseRegion_maps_new_zealand_locations(string? location, string expected)
    {
        service.StandardiseRegion(location).Should().Be(expected);
    }

    [Theory]
    [InlineData("Service Desk Support Analyst Job in Christchurch Central, Canterbury - SEEK", "Canterbury")]
    [InlineData("Engineer - IT Service Operations Job in Porirua, Wellington - SEEK", "Wellington")]
    [InlineData("Senior Data Engineer - Bengaluru, Karnataka, India", "International")]
    [InlineData("This is a remote-first role", "Remote")]
    public void ResolveRegion_derives_a_region_from_context_when_location_is_missing(
        string context,
        string expected)
    {
        service.ResolveRegion(null, context).Should().Be(expected);
    }

    [Theory]
    [InlineData("Adelaide, Brisbane, Canberra", "International")]
    [InlineData("Sydney", "International")]
    [InlineData("Allen, TX", "International")]
    // Canonical Round 5 locations still resolve to their NZ region.
    [InlineData("Christchurch, Canterbury", "Canterbury")]
    [InlineData("Hamilton, Waikato", "Waikato")]
    [InlineData("Tauranga, Bay of Plenty", "Bay of Plenty")]
    [InlineData("Whangarei, Northland", "Northland")]
    [InlineData("Upper Hutt, Wellington", "Wellington")]
    [InlineData("Remote (NZ)", "Remote")]
    [InlineData("New Zealand", "New Zealand")]
    public void StandardiseRegion_handles_international_and_canonical_locations(
        string location,
        string expected)
    {
        service.StandardiseRegion(location).Should().Be(expected);
    }

    [Fact]
    public void ResolveRegion_defaults_missing_location_and_context_to_new_zealand()
    {
        service.ResolveRegion(null).Should().Be("New Zealand");
        service.ResolveRegion("   ").Should().Be("New Zealand");
        service.ResolveRegion(null, "no recognisable place here").Should().Be("New Zealand");
    }

    [Fact]
    public void ResolveRegion_prefers_an_explicit_location_over_context()
    {
        service.ResolveRegion("Hamilton", "This advert mentions Auckland a lot").Should().Be("Waikato");
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
