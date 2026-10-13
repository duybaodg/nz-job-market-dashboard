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

        result.Location.Should().Be("Nelson");
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
    public void Sanitise_folds_nz_government_multi_region_into_wellington()
    {
        var result = LocationSanitiser.Sanitise(NzGovernmentMultiRegionLocation);
        var region = new NormalisationService().StandardiseRegion(result.Location);

        result.Location.Should().Be("Wellington");
        region.Should().Be("Wellington");
    }

    [Fact]
    public void Sanitise_folds_a_long_multi_region_list_into_wellington()
    {
        const string governmentRegions =
            "Wellington - Hutt Valley;Wellington - Kapiti;Wellington - Masterton;"
            + "Wellington - Porirua;Wellington - Tawa;Wellington - Upper Hutt;Wellington - Wellington";

        var result = LocationSanitiser.Sanitise(governmentRegions);

        result.Location.Should().Be("Wellington");
        result.Location!.Length.Should().BeLessThanOrEqualTo(LocationSanitiser.MaxLength);
    }

    // Every distinct non-null location string that was live in production when
    // Round 5 started (GET /api/jobs?status=all, 2026-10-13), with the canonical
    // value the model must produce. This is the regression net for the round.
    [Theory]
    [InlineData("2 Locations", null, null)]
    [InlineData("Adelaide, Brisbane, Canberra", null, "International")]
    [InlineData("All New Zealand", "New Zealand", null)]
    [InlineData("Allen, TX; Colorado Springs, CO; Redmond, WA; Remote", null, "International")]
    [InlineData("Auckland", "Auckland", null)]
    [InlineData("Auckland (Albany HQ) (NZ)", "Auckland", null)]
    [InlineData("Auckland - Auckland", "Auckland", null)]
    [InlineData("Auckland CBD TEMPORARY Job", "Auckland", null)]
    [InlineData("Auckland Central", "Auckland", null)]
    [InlineData("Auckland City, Auckland, NZ", "Auckland", null)]
    [InlineData("Auckland, Auckland, nz", "Auckland", null)]
    [InlineData("Auckland, Auckland,NZ, NZ", "Auckland", null)]
    [InlineData("Auckland, NZ", "Auckland", null)]
    [InlineData("Auckland, New Zealand", "Auckland", null)]
    [InlineData("Canterbury", "Canterbury", null)]
    [InlineData("Canterbury - Christchurch;Wellington - Wellington", "Christchurch, Canterbury", null)]
    [InlineData("Christchurch", "Christchurch, Canterbury", null)]
    [InlineData("Christchurch CBD", "Christchurch, Canterbury", null)]
    [InlineData("Christchurch City, Canterbury, NZ", "Christchurch, Canterbury", null)]
    [InlineData("Christchurch, Canterbury", "Christchurch, Canterbury", null)]
    [InlineData("East Tamaki, New Zealand", "Auckland", null)]
    [InlineData("Flexible Location", null, null)]
    [InlineData("Hamilton", "Hamilton, Waikato", null)]
    [InlineData("Hamilton, New Zealand", "Hamilton, Waikato", null)]
    [InlineData("Hamilton, New Zealand, NZ", "Hamilton, Waikato", null)]
    [InlineData("Hamilton, Waikato Region, NZ", "Hamilton, Waikato", null)]
    [InlineData("Hawke's Bay", "Hawke's Bay", null)]
    [InlineData("Lower Hutt, Greater Wellington, NZ", "Wellington", null)]
    [InlineData("Manukau & East Auckland", "Auckland", null)]
    [InlineData("Manukau City, Auckland, NZ", "Auckland", null)]
    [InlineData("Matamata-Piako, Waikato, NZ", "Matamata, Waikato", null)]
    [InlineData("Melbourne, Australia, AU", null, "International")]
    [InlineData("NZ: Remote", "Remote (NZ)", null)]
    [InlineData("Nelson, Nelson Region, New Zealand", "Nelson", null)]
    [InlineData("New Zealand", "New Zealand", null)]
    [InlineData("New Zealand - Christchurch", "Christchurch, Canterbury", null)]
    [InlineData("New Zealand’s legal and political heart", null, null)]
    [InlineData("Newmarket, New Zealand", "Auckland", null)]
    [InlineData("None", null, null)]
    [InlineData("North Island", null, null)]
    [InlineData("Parnell", "Auckland", null)]
    [InlineData("Queenstown, Otago Region, NZ", "Queenstown, Otago", null)]
    [InlineData("Sydney", null, "International")]
    [InlineData("Taranaki", "Taranaki", null)]
    [InlineData("Tauranga", "Tauranga, Bay of Plenty", null)]
    [InlineData("Tauranga, Bay of Plenty, NZ", "Tauranga, Bay of Plenty", null)]
    [InlineData("Tauranga, New Zealand", "Tauranga, Bay of Plenty", null)]
    [InlineData("Washington or California State. Requirements Your work will", null, "International")]
    [InlineData("Wellington", "Wellington", null)]
    [InlineData("Wellington - Hutt Valley; Kapiti; Masterton", "Wellington", null)]
    [InlineData("Wellington - Trentham", "Upper Hutt, Wellington", null)]
    [InlineData("Wellington - Wellington", "Wellington", null)]
    [InlineData("Wellington - Wellington CBD", "Wellington", null)]
    [InlineData("Wellington Central", "Wellington", null)]
    [InlineData("Wellington Central FULL_TIME Job", "Wellington", null)]
    [InlineData("Wellington Central, Greater Wellington, NZ", "Wellington", null)]
    [InlineData("Wellington Rate: $100–$120/hr + GST Start: ASAP / urgent", "Wellington", null)]
    [InlineData("Wellington, Greater Wellington, NZ", "Wellington", null)]
    [InlineData("Wellington, NZ", "Wellington", null)]
    [InlineData("Wellington, New Zealand", "Wellington", null)]
    [InlineData("Westpac on Takutai Square", "Auckland", null)]
    [InlineData("Whangarei, Northland, NZ", "Whangarei, Northland", null)]
    [InlineData("another country Internship Development & Outcomes By the", null, "International")]
    [InlineData("either Washington or California State. Requirements By", null, "International")]
    [InlineData("our Bengaluru office, you are a high", null, "International")]
    public void Sanitise_maps_every_live_string_to_a_canonical_location(
        string raw,
        string? expectedLocation,
        string? expectedHint)
    {
        var result = LocationSanitiser.Sanitise(raw);

        result.Location.Should().Be(expectedLocation);
        result.RegionHint.Should().Be(expectedHint);
    }

    [Fact]
    public void Sanitise_outputs_never_exceed_the_contamination_length_cap()
    {
        foreach (var raw in LiveLocationStrings)
        {
            var result = LocationSanitiser.Sanitise(raw);
            if (result.Location is null)
            {
                continue;
            }

            result.Location.Length.Should().BeLessThanOrEqualTo(LocationSanitiser.MaxLength);

            // No duplicated "City, City" token after normalisation.
            var segments = result.Location.Split(", ");
            segments.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(segments.Length);
        }
    }

    [Fact]
    public void Sanitise_collapses_the_live_distinct_set_to_a_small_canonical_set()
    {
        var canonical = LiveLocationStrings
            .Select(raw => LocationSanitiser.Sanitise(raw).Location)
            .Where(location => location is not null)
            .ToHashSet(StringComparer.Ordinal);

        canonical.Should().OnlyContain(location => location!.Length <= LocationSanitiser.MaxLength);

        // 65 raw strings (many spellings of five cities) must not stay 65 rows.
        canonical.Count.Should().BeLessThan(20);
        canonical.Should().Contain(["Auckland", "Wellington", "Christchurch, Canterbury", "Hamilton, Waikato"]);
    }

    [Fact]
    public void Sanitise_is_idempotent_for_every_live_string()
    {
        foreach (var raw in LiveLocationStrings)
        {
            var first = LocationSanitiser.Sanitise(raw);
            var second = LocationSanitiser.Sanitise(first.Location);

            second.Location.Should().Be(first.Location, "sanitising '{0}' must be stable", raw);
            second.RemovedRemainder.Should().BeNull("a canonical location has no remainder", raw);
        }
    }

    [Theory]
    [InlineData("Remote")]
    [InlineData("Work from home")]
    [InlineData("NZ: Remote")]
    public void Sanitise_treats_remote_wording_as_remote_new_zealand(string remote)
    {
        LocationSanitiser.Sanitise(remote).Location.Should().Be("Remote (NZ)");
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitise_returns_null_for_missing_locations(string? location)
    {
        var result = LocationSanitiser.Sanitise(location);

        result.Location.Should().BeNull();
        result.RemovedRemainder.Should().BeNull();
        result.RegionHint.Should().BeNull();
    }

    private static readonly string[] LiveLocationStrings =
    [
        "2 Locations",
        "Adelaide, Brisbane, Canberra",
        "All New Zealand",
        "Allen, TX; Colorado Springs, CO; Redmond, WA; Remote",
        "Auckland",
        "Auckland (Albany HQ) (NZ)",
        "Auckland - Auckland",
        "Auckland CBD TEMPORARY Job",
        "Auckland Central",
        "Auckland City, Auckland, NZ",
        "Auckland, Auckland, nz",
        "Auckland, Auckland,NZ, NZ",
        "Auckland, NZ",
        "Auckland, New Zealand",
        "Canterbury",
        "Canterbury - Christchurch;Wellington - Wellington",
        "Christchurch",
        "Christchurch CBD",
        "Christchurch City, Canterbury, NZ",
        "Christchurch, Canterbury",
        "East Tamaki, New Zealand",
        "Flexible Location",
        "Hamilton",
        "Hamilton, New Zealand",
        "Hamilton, New Zealand, NZ",
        "Hamilton, Waikato Region, NZ",
        "Hawke's Bay",
        "Lower Hutt, Greater Wellington, NZ",
        "Manukau & East Auckland",
        "Manukau City, Auckland, NZ",
        "Matamata-Piako, Waikato, NZ",
        "Melbourne, Australia, AU",
        "NZ: Remote",
        "Nelson, Nelson Region, New Zealand",
        "New Zealand",
        "New Zealand - Christchurch",
        "New Zealand’s legal and political heart",
        "Newmarket, New Zealand",
        "None",
        "North Island",
        "Parnell",
        "Queenstown, Otago Region, NZ",
        "Sydney",
        "Taranaki",
        "Tauranga",
        "Tauranga, Bay of Plenty, NZ",
        "Tauranga, New Zealand",
        "Washington or California State. Requirements Your work will",
        "Wellington",
        "Wellington - Hutt Valley; Kapiti; Masterton",
        "Wellington - Trentham",
        "Wellington - Wellington",
        "Wellington - Wellington CBD",
        "Wellington Central",
        "Wellington Central FULL_TIME Job",
        "Wellington Central, Greater Wellington, NZ",
        "Wellington Rate: $100–$120/hr + GST Start: ASAP / urgent",
        "Wellington, Greater Wellington, NZ",
        "Wellington, NZ",
        "Wellington, New Zealand",
        "Westpac on Takutai Square",
        "Whangarei, Northland, NZ",
        "another country Internship Development & Outcomes By the",
        "either Washington or California State. Requirements By",
        "our Bengaluru office, you are a high"
    ];
}
