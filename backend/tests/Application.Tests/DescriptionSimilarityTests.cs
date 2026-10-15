using Application.Deduplication;
using FluentAssertions;

namespace Application.Tests;

public sealed class DescriptionSimilarityTests
{
    [Fact]
    public void Identical_text_is_a_perfect_match()
    {
        DescriptionSimilarity.Jaccard("Build reliable C# services with Kafka", "Build reliable C# services with Kafka")
            .Should().Be(1d);
    }

    [Fact]
    public void Punctuation_and_case_are_ignored_when_shingling()
    {
        var left = DescriptionSimilarity.Shingles("We build C# services, reliable and fast!");
        var right = DescriptionSimilarity.Shingles("we build c services reliable and fast");

        DescriptionSimilarity.Jaccard(left, right).Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void Empty_or_missing_descriptions_never_match()
    {
        DescriptionSimilarity.Jaccard(null, "anything").Should().Be(0d);
        DescriptionSimilarity.Jaccard(string.Empty, string.Empty).Should().Be(0d);
    }
}
