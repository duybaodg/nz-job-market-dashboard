using System.Reflection;
using System.Text.Json;
using Application.Jobs;
using FluentAssertions;

namespace Application.Tests;

/// <summary>
/// Round 22 P0: the C1 test set. These titles were traced from the active
/// 2026-11-06 feed and frozen with the role-family-v1 answer at that time, plus
/// documented ambiguous and outside-family controls. P0 asserts the current
/// family mapping; P1 reuses the same fixture for the sub-role taxonomy.
/// </summary>
public sealed class DataAnalyticsFixtureTests
{
    [Fact]
    public void Fixture_contains_the_six_target_roles_and_ambiguous_controls()
    {
        var fixture = DataAnalyticsFixtures.Load();

        fixture.FamilyRuleVersion.Should().Be(RoleFamilyClassifier.RuleVersion);
        fixture.Items.Should().NotBeEmpty();
        fixture.Items.Should().Contain(item => item.Case == "known");
        fixture.Items.Should().Contain(item => item.Case == "ambiguous" || item.Case == "multiple");
        fixture.Items.Should().Contain(item => item.Case == "outside-family");

        var applicable = fixture.Items.Where(item => item.SubRoleApplicable).ToList();
        applicable.Should().Contain(item => item.ExpectedSubRoleKey == "data-analyst");
        applicable.Should().Contain(item => item.ExpectedSubRoleKey == "data-engineer");
        applicable.Should().Contain(item => item.ExpectedSubRoleKey == "analytics-engineer");
        applicable.Should().Contain(item => item.ExpectedSubRoleKey == "bi-analyst-developer");
        applicable.Should().Contain(item => item.ExpectedSubRoleKey == "data-scientist");
        applicable.Should().Contain(item => item.ExpectedSubRoleKey == "leadership");
    }

    [Fact]
    public void P0_fixture_matches_the_current_role_family_classifier()
    {
        foreach (var item in DataAnalyticsFixtures.Load().Items)
        {
            var actual = RoleFamilyClassifier.Classify(item.Title).Family;
            actual.Should().Be(
                item.CurrentRoleFamily,
                because: $"the P0 fixture records role-family-v1 for '{item.Title}'");
        }
    }
}

internal sealed record DataAnalyticsTitleFixture(
    string Title,
    string CurrentRoleFamily,
    bool SubRoleApplicable,
    string? ExpectedSubRoleKey,
    bool ExpectedUncertain,
    IReadOnlyList<string> ExpectedCandidates,
    string Case,
    string? Note);

internal sealed record DataAnalyticsFixtures(
    string CapturedOn,
    string FamilyRuleVersion,
    IReadOnlyList<DataAnalyticsTitleFixture> Items)
{
    private const string ResourceSuffix = "Fixtures.data-analytics-titles.json";

    public static DataAnalyticsFixtures Load()
    {
        var assembly = typeof(DataAnalyticsFixtures).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded fixture '{resourceName}' was not found.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var items = root.GetProperty("items").EnumerateArray()
            .Select(item => new DataAnalyticsTitleFixture(
                item.GetProperty("title").GetString()!,
                item.GetProperty("currentRoleFamily").GetString()!,
                item.GetProperty("subRoleApplicable").GetBoolean(),
                item.TryGetProperty("expectedSubRoleKey", out var key) && key.ValueKind == JsonValueKind.String
                    ? key.GetString()
                    : null,
                item.GetProperty("expectedUncertain").GetBoolean(),
                item.TryGetProperty("expectedCandidates", out var candidates)
                    ? candidates.EnumerateArray().Select(candidate => candidate.GetString()!).ToList()
                    : [],
                item.GetProperty("case").GetString()!,
                item.TryGetProperty("note", out var note) ? note.GetString() : null))
            .ToList();

        return new DataAnalyticsFixtures(
            root.GetProperty("capturedOn").GetString()!,
            root.GetProperty("familyRuleVersion").GetString()!,
            items);
    }
}
