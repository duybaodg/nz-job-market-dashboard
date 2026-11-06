using Application.Jobs;
using FluentAssertions;

namespace Application.Tests;

public sealed class DataSubRoleClassifierTests
{
    [Fact]
    public void Taxonomy_version_is_published_and_roles_are_unique()
    {
        var taxonomy = DataSubRoleTaxonomy.Default;

        taxonomy.Version.Should().Be("data-subrole-v1");
        taxonomy.Family.Should().Be(RoleFamilyClassifier.Classify("Data Analyst").Family);
        taxonomy.Roles.Should().HaveCount(6);
        taxonomy.Roles.Select(role => role.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_fixture_title_classifies_as_documented()
    {
        var fixture = DataAnalyticsFixtures.Load();

        foreach (var item in fixture.Items)
        {
            var family = RoleFamilyClassifier.Classify(item.Title).Family;
            var match = DataSubRoleClassifier.Classify(item.Title, family);

            match.TaxonomyVersion.Should().Be(DataSubRoleTaxonomy.Default.Version);

            if (!item.SubRoleApplicable)
            {
                match.Applicable.Should().BeFalse(because: $"'{item.Title}' is outside the Data & Analytics family");
                match.Key.Should().BeNull();
                match.Uncertain.Should().BeFalse();
                match.CandidateKeys.Should().BeEmpty();
                continue;
            }

            match.Applicable.Should().BeTrue(because: $"'{item.Title}' is in the Data & Analytics family");
            match.Key.Should().Be(item.ExpectedSubRoleKey, because: $"'{item.Title}' is a {item.Case} case");
            match.Uncertain.Should().Be(item.ExpectedUncertain, because: $"'{item.Title}' must keep an explicit uncertain state when applicable");
            match.CandidateKeys.Should().BeEquivalentTo(item.ExpectedCandidates, because: $"'{item.Title}' candidates are part of the documented state");

            if (item.ExpectedUncertain)
            {
                match.UncertainReason.Should().NotBeNullOrWhiteSpace();
            }
            else
            {
                match.UncertainReason.Should().BeNull();
                match.Label.Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void Multiple_role_titles_are_never_forced_into_one_label()
    {
        var family = RoleFamilyClassifier.Classify("Data Analyst / Data Engineer").Family;
        var match = DataSubRoleClassifier.Classify("Data Analyst / Data Engineer", family);

        match.Key.Should().BeNull();
        match.Uncertain.Should().BeTrue();
        match.CandidateKeys.Should().BeEquivalentTo(["data-analyst", "data-engineer"]);
        match.CandidateLabels.Should().HaveCount(2);
        match.UncertainReason.Should().Contain("Multiple sub-role signals");
    }

    [Fact]
    public void Unspecified_family_titles_stay_uncertain_with_a_reason()
    {
        var family = RoleFamilyClassifier.Classify("Data Architect").Family;
        var match = DataSubRoleClassifier.Classify("Data Architect", family);

        match.Key.Should().BeNull();
        match.Uncertain.Should().BeTrue();
        match.CandidateKeys.Should().BeEmpty();
        match.UncertainReason.Should().Contain("no specific sub-role");
    }

    [Fact]
    public void Non_family_titles_are_not_classified_even_when_a_pattern_matches()
    {
        // Role-family-v1 puts this title in Management & Leadership. The
        // sub-role taxonomy is deliberately scoped to Data & Analytics, so it
        // must not reclassify or relabel the existing family.
        var family = RoleFamilyClassifier.Classify("Head of Data, Digital & Information Services").Family;
        family.Should().Be("Management & Leadership");

        var match = DataSubRoleClassifier.Classify("Head of Data, Digital & Information Services", family);

        match.Applicable.Should().BeFalse();
        match.Key.Should().BeNull();
        match.Uncertain.Should().BeFalse();
        match.TaxonomyVersion.Should().Be("data-subrole-v1");
    }
}
