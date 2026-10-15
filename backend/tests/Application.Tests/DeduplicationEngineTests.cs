using Application.Deduplication;
using FluentAssertions;

namespace Application.Tests;

public sealed class DeduplicationEngineTests
{
    private static readonly Guid Employer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Base = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Identical_title_employer_salary_across_sources_merges()
    {
        var a = Listing("Senior Developer", "SEEK NZ", salary: "$120,000 - $140,000", posted: Base);
        var b = Listing("senior developer", "Trade Me Jobs", salary: "$120000-$140000", posted: Base.AddDays(3));

        var evaluation = DeduplicationEngine.Evaluate(a, b);

        evaluation.Relation.Should().Be(DedupPairRelation.SalaryWindowMatch);
        evaluation.MatchMethod.Should().Be(DeduplicationMatchMethods.TitleEmployerSalaryWindow);
    }

    [Fact]
    public void Listings_from_the_same_source_are_never_grouped_by_the_engine()
    {
        var a = Listing("Senior Developer", "SEEK NZ", salary: "$120,000", posted: Base);
        var b = Listing("Senior Developer", "SEEK NZ", salary: "$120,000", posted: Base);

        DeduplicationEngine.Evaluate(a, b).Relation.Should().Be(DedupPairRelation.SameSource);
    }

    [Fact]
    public void Salary_mismatch_blocks_merge_even_with_identical_descriptions()
    {
        var description = string.Join(' ', Enumerable.Range(0, 200).Select(index => $"word{index}"));
        var a = Listing("Senior Developer", "SEEK NZ", salary: "$120,000", posted: Base, description: description);
        var b = Listing("Senior Developer", "Trade Me Jobs", salary: "$150,000", posted: Base, description: description);

        var evaluation = DeduplicationEngine.Evaluate(a, b);

        evaluation.Relation.Should().Be(DedupPairRelation.SalaryConflict);
        evaluation.IsMatch.Should().BeFalse();
    }

    [Fact]
    public void Legitimate_twin_openings_with_different_descriptions_stay_separate()
    {
        var a = Listing(
            "Graduate Developer",
            "SEEK NZ",
            location: "Auckland",
            posted: Base,
            description: "Join our payments platform team building fraud detection services with C# and Kafka.");
        var b = Listing(
            "Graduate Developer",
            "Trade Me Jobs",
            location: "Auckland",
            posted: Base,
            description: "Our marine engineering group needs a graduate to model vessel telemetry data.");

        var plan = DeduplicationEngine.Plan([a, b]);

        plan.Groups.Should().BeEmpty("nothing stronger than a title+employer match exists");
        plan.ReviewCandidates.Should().ContainSingle(candidate => candidate.Reason == "gates-failed");
    }

    [Fact]
    public void More_than_four_identical_openings_become_review_candidates_not_a_group()
    {
        var listings = Enumerable.Range(0, 5)
            .Select(index => Listing(
                "Graduate Developer",
                index % 2 == 0 ? "SEEK NZ" : "Trade Me Jobs",
                salary: "$70,000",
                posted: Base.AddDays(index % 2)))
            .ToList();

        var plan = DeduplicationEngine.Plan(listings);

        plan.Groups.Should().BeEmpty("the cap forbids merging more than four listings");
        plan.ReviewCandidates.Should().NotBeEmpty();
        plan.ReviewCandidates.Should().OnlyContain(candidate => candidate.Reason == "group-cap-exceeded");
    }

    [Fact]
    public void Description_similarity_boundary_merges_at_0_8_but_not_at_0_79()
    {
        var (at079Left, at079Right) = ShinglePair(intersection: 79, leftCount: 90, rightCount: 89);
        var (at08Left, at08Right) = ShinglePair(intersection: 80, leftCount: 90, rightCount: 90);

        DescriptionSimilarity.Jaccard(at079Left, at079Right).Should().BeApproximately(0.79, 0.0000001);
        DescriptionSimilarity.Jaccard(at08Left, at08Right).Should().BeApproximately(0.8, 0.0000001);

        var below = DeduplicationEngine.Evaluate(
            ListingWithShingles("Senior Developer", "SEEK NZ", at079Left, Base),
            ListingWithShingles("Senior Developer", "Trade Me Jobs", at079Right, Base.AddDays(10)));
        var atThreshold = DeduplicationEngine.Evaluate(
            ListingWithShingles("Senior Developer", "SEEK NZ", at08Left, Base),
            ListingWithShingles("Senior Developer", "Trade Me Jobs", at08Right, Base.AddDays(10)));

        below.Relation.Should().Be(DedupPairRelation.NoRelation);
        atThreshold.Relation.Should().Be(DedupPairRelation.DescriptionMatch);
    }

    [Fact]
    public void Date_gap_over_seven_days_without_description_evidence_does_not_merge()
    {
        var a = Listing("Senior Developer", "SEEK NZ", salary: "$120,000", posted: Base, description: "alpha bravo charlie delta echo foxtrot");
        var b = Listing("Senior Developer", "Trade Me Jobs", salary: "$120,000", posted: Base.AddDays(8), description: "zulu yankee xray whiskey victor uniform");

        DeduplicationEngine.Evaluate(a, b).Relation.Should().Be(DedupPairRelation.NoRelation);
    }

    private static DedupListing Listing(
        string title,
        string source,
        string? salary = null,
        string location = "Auckland",
        DateTime? posted = null,
        DateTime? firstSeen = null,
        DateTime? closing = null,
        string? description = null)
    {
        var start = posted ?? firstSeen ?? Base;
        var end = closing ?? start.AddDays(30);
        var salaryKey = ListingMatchKeys.NormalizeSalary(salary);
        return new DedupListing(
            Guid.NewGuid(),
            source,
            ListingMatchKeys.NormalizeTitle(title),
            Employer,
            HasSalary: salaryKey.Length > 0,
            SalaryKey: salaryKey,
            LocationKey: ListingMatchKeys.NormalizeLocation(location),
            WindowStart: start,
            WindowEnd: end,
            RepresentativeDate: posted ?? firstSeen ?? Base,
            DescriptionShingles: DescriptionSimilarity.Shingles(description));
    }

    private static DedupListing ListingWithShingles(string title, string source, IReadOnlySet<string> shingles, DateTime posted) =>
        new(
            Guid.NewGuid(),
            source,
            ListingMatchKeys.NormalizeTitle(title),
            Employer,
            HasSalary: false,
            SalaryKey: string.Empty,
            LocationKey: "auckland",
            WindowStart: posted,
            WindowEnd: posted.AddDays(30),
            RepresentativeDate: posted,
            DescriptionShingles: shingles);

    private static (IReadOnlySet<string> Left, IReadOnlySet<string> Right) ShinglePair(int intersection, int leftCount, int rightCount)
    {
        var shared = Enumerable.Range(0, intersection).Select(index => $"s{index}").ToList();
        var left = shared.Concat(Enumerable.Range(0, leftCount - intersection).Select(index => $"l{index}")).ToHashSet(StringComparer.Ordinal);
        var right = shared.Concat(Enumerable.Range(0, rightCount - intersection).Select(index => $"r{index}")).ToHashSet(StringComparer.Ordinal);
        return (left, right);
    }
}
