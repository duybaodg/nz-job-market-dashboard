using Application.Employers;
using FluentAssertions;

namespace Application.Tests;

public sealed class EmployerClassifierTests
{
    [Fact]
    public void Direct_employer_board_names_the_employer()
    {
        var result = EmployerClassifier.Classify("Xero", "company", "Xero");

        result.AdvertiserType.Should().Be(AdvertiserType.DirectEmployer);
        result.Disclosure.Should().Be(EmployerDisclosure.Disclosed);
        result.HiringEmployer.Should().Be("Xero");
    }

    [Fact]
    public void Direct_employer_board_falls_back_to_the_source_when_company_is_missing()
    {
        var result = EmployerClassifier.Classify("Datacom", "company", null);

        result.Disclosure.Should().Be(EmployerDisclosure.Disclosed);
        result.HiringEmployer.Should().Be("Datacom");
    }

    [Fact]
    public void Agency_advertising_its_own_brand_is_agency_only()
    {
        var result = EmployerClassifier.Classify("Beyond Recruitment", "recruiter", "Beyond Recruitment");

        result.AdvertiserType.Should().Be(AdvertiserType.Agency);
        result.Disclosure.Should().Be(EmployerDisclosure.AgencyOnly);
        result.HiringEmployer.Should().BeNull("the client must never be invented");
    }

    [Fact]
    public void Agency_listing_a_named_client_discloses_the_employer()
    {
        var result = EmployerClassifier.Classify("Tribe Recruitment", "recruiter", "Tau Mai");

        result.Disclosure.Should().Be(EmployerDisclosure.Disclosed);
        result.HiringEmployer.Should().Be("Tau Mai");
    }

    [Fact]
    public void Board_listing_an_agency_alias_is_agency_only()
    {
        var result = EmployerClassifier.Classify("Trade Me Jobs", "job-board", "Comspek International");

        result.AdvertiserType.Should().Be(AdvertiserType.Board);
        result.Disclosure.Should().Be(EmployerDisclosure.AgencyOnly);
        result.HiringEmployer.Should().BeNull();
    }

    [Fact]
    public void Board_listing_a_named_employer_discloses_it()
    {
        var result = EmployerClassifier.Classify("Trade Me Jobs", "job-board", "Woolworths Group New Zealand");

        result.Disclosure.Should().Be(EmployerDisclosure.Disclosed);
        result.HiringEmployer.Should().Be("Woolworths Group New Zealand");
    }

    [Fact]
    public void Board_without_a_company_is_unknown_not_invented()
    {
        var result = EmployerClassifier.Classify("SEEK NZ", "job-board", null);

        result.Disclosure.Should().Be(EmployerDisclosure.Unknown);
        result.HiringEmployer.Should().BeNull();
    }

    [Fact]
    public void Board_echoing_its_own_name_is_not_counted_as_an_employer()
    {
        var result = EmployerClassifier.Classify("Trade Me Jobs", "job-board", "Trade Me");

        result.Disclosure.Should().Be(EmployerDisclosure.Unknown);
        result.HiringEmployer.Should().BeNull("the board repeating its own name is not employer evidence");
    }
}
