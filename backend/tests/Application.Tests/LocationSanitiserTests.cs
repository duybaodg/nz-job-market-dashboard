using Application.Normalisation;
using FluentAssertions;

namespace Application.Tests;

public sealed class LocationSanitiserTests
{
    // The exact live strings from docs/agent-plans/2026-10-12-crawl-rules-round2.md.
    private const string SeekContaminatedLocation =
        "Nelson, Nelson Region, New Zealand We also welcome applications from qualified "
        + "candidates based in Christchurch, Canterbury Region, New Zealand 🧠 Neurodiversity";

    private const string ComspekContaminatedLocation =
        "Wellington Job type Permanent Specialisation Team Leaders Reference BH-2844 "
        + "Job description Are you passionate about building world-class engineering platforms";

    // Newly-yielding sources observed live 2026-10-12 (Round 2 follow-up).
    private const string GallagherContaminatedLocation =
        "centives Career growth opportunities as eShepherd continues to scale globally";

    private const string GallagherContaminatedLocationAlt =
        "centives Career growth opportunities as we continue to scale globally Modern";

    private const string TalentVaultContaminatedLocation =
        "Auckland Industry: Information & Communication Technology Contact name: Steve";

    private const string NzGovernmentMultiRegionLocation =
        "Wellington - Hutt Valley;Wellington - Kapiti;Wellington - Masterton";

    [Fact]
    public void Sanitise_cuts_seek_location_at_the_trailing_ad_sentence()
    {
        var result = LocationSanitiser.Sanitise(SeekContaminatedLocation);

        result.Location.Should().Be("Nelson, Nelson Region, New Zealand");
        result.RemovedRemainder.Should().StartWith("We also welcome applications");
    }

    [Fact]
    public void Sanitise_cuts_comspek_location_before_the_detail_block()
    {
        var result = LocationSanitiser.Sanitise(ComspekContaminatedLocation);

        result.Location.Should().Be("Wellington");
        result.RemovedRemainder.Should().StartWith("Job type Permanent");
    }

    [Fact]
    public void Sanitise_rejects_a_gallagher_description_fragment_as_a_location()
    {
        var result = LocationSanitiser.Sanitise(GallagherContaminatedLocation);

        result.Location.Should().BeNull();
        result.RemovedRemainder.Should().Be(GallagherContaminatedLocation);
    }

    [Fact]
    public void Sanitise_rejects_the_alternate_gallagher_fragment()
    {
        var result = LocationSanitiser.Sanitise(GallagherContaminatedLocationAlt);

        result.Location.Should().BeNull();
        result.RemovedRemainder.Should().Be(GallagherContaminatedLocationAlt);
    }

    [Fact]
    public void Sanitise_cuts_talent_vault_location_before_the_industry_field()
    {
        var result = LocationSanitiser.Sanitise(TalentVaultContaminatedLocation);

        result.Location.Should().Be("Auckland");
        result.RemovedRemainder.Should().StartWith("Industry: Information & Communication Technology");
    }

    [Fact]
    public void Sanitise_keeps_nz_government_multi_region_and_resolves_wellington()
    {
        var result = LocationSanitiser.Sanitise(NzGovernmentMultiRegionLocation);
        var region = new NormalisationService().StandardiseRegion(result.Location);

        result.Location.Should().NotBeNull();
        result.Location!.Length.Should().BeLessThanOrEqualTo(LocationSanitiser.MaxLength);
        result.Location.Should().StartWith("Wellington - Hutt Valley");
        result.Location.Should().Contain("Kapiti").And.Contain("Masterton");
        region.Should().Be("Wellington");
    }

    [Fact]
    public void Sanitise_caps_a_long_multi_region_list()
    {
        const string governmentRegions =
            "Wellington - Hutt Valley;Wellington - Kapiti;Wellington - Masterton;"
            + "Wellington - Porirua;Wellington - Tawa;Wellington - Upper Hutt;Wellington - Wellington";

        var result = LocationSanitiser.Sanitise(governmentRegions);

        result.Location.Should().NotBeNull();
        result.Location!.Length.Should().BeLessThanOrEqualTo(LocationSanitiser.MaxLength);
        result.Location.Should().StartWith("Wellington - Hutt Valley");
    }

    [Theory]
    [InlineData("Auckland")]
    [InlineData("Christchurch, Canterbury, New Zealand")]
    [InlineData("Wellington CBD")]
    [InlineData("Remote")]
    public void Sanitise_leaves_clean_locations_untouched(string location)
    {
        var result = LocationSanitiser.Sanitise(location);

        result.Location.Should().Be(location);
        result.RemovedRemainder.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitise_returns_null_for_missing_locations(string? location)
    {
        var result = LocationSanitiser.Sanitise(location);

        result.Location.Should().BeNull();
        result.RemovedRemainder.Should().BeNull();
    }

    [Fact]
    public void Sanitise_limits_a_location_to_four_comma_segments()
    {
        var result = LocationSanitiser.Sanitise("A, B, C, D, E, F");

        result.Location.Should().Be("A, B, C, D");
        result.RemovedRemainder.Should().Be("E, F");
    }

    [Fact]
    public void PrependRemainder_preserves_text_once()
    {
        LocationSanitiser.PrependRemainder("Job type Permanent", "Great role")
            .Should().Be("Job type Permanent Great role");

        LocationSanitiser.PrependRemainder("Job type Permanent", "Job type Permanent Great role")
            .Should().Be("Job type Permanent Great role");

        LocationSanitiser.PrependRemainder(null, "Great role").Should().Be("Great role");
    }

    [Fact]
    public void Sanitise_is_idempotent()
    {
        var first = LocationSanitiser.Sanitise(ComspekContaminatedLocation);
        var second = LocationSanitiser.Sanitise(first.Location);

        second.Location.Should().Be(first.Location);
        second.RemovedRemainder.Should().BeNull();
    }
}
