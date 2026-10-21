using FluentAssertions;
using Infrastructure.Firecrawl;

namespace Application.Tests;

/// <summary>
/// Mirrors <c>crawler/tests/test_robots.py</c> for the .NET Firecrawl path. The
/// two implementations must agree: a URL the Python worker refuses must also be
/// refused before the self-hosted Firecrawl instance is asked to fetch it.
/// </summary>
public sealed class RobotsTxtMatcherTests
{
    private const string SeekRobots = """
        # robots.txt file for nz.seek.com
        User-agent: Mediapartners-Google
        Disallow:

        User-agent: *
        Disallow: */job/
        Disallow: *?
        Disallow: /graphql
        Disallow: /api/jobsearch/
        Allow: *?advertiserid
        Allow: *?keywords

        User-agent: LinkedInBot
        Disallow: /
        """;

    private const string TradeMeRobots = """
        User-agent: *
        Allow: /
        Disallow: /a/*?*)
        Disallow: /a/*search?
        Disallow: /a/*rsqid
        Disallow: /a/*page=
        """;

    private const string UserAgent = "Mozilla/5.0 (compatible; NZJobMarketDashboardBot/0.2)";

    [Fact]
    public void Seek_allows_listing_but_not_job_details()
    {
        var rules = RobotsTxtMatcher.Parse(SeekRobots);

        RobotsTxtMatcher.CanFetch(rules, UserAgent, "https://nz.seek.com/information-technology-jobs").Should().BeTrue();
        RobotsTxtMatcher.CanFetch(rules, UserAgent, "https://nz.seek.com/job/95156028").Should().BeFalse();
    }

    [Fact]
    public void Seek_keyword_slices_allowed_but_pagination_blocked()
    {
        var rules = RobotsTxtMatcher.Parse(SeekRobots);

        RobotsTxtMatcher
            .CanFetch(rules, UserAgent, "https://nz.seek.com/information-technology-jobs?keywords=developer")
            .Should().BeTrue();
        RobotsTxtMatcher
            .CanFetch(rules, UserAgent, "https://nz.seek.com/information-technology-jobs?page=2")
            .Should().BeFalse();
    }

    [Fact]
    public void Seek_specific_bot_group_overrides_wildcard()
    {
        var rules = RobotsTxtMatcher.Parse(SeekRobots);

        RobotsTxtMatcher
            .CanFetch(rules, "LinkedInBot", "https://nz.seek.com/information-technology-jobs")
            .Should().BeFalse();
    }

    [Fact]
    public void TradeMe_allows_clean_paths_and_blocks_queries()
    {
        var rules = RobotsTxtMatcher.Parse(TradeMeRobots);

        RobotsTxtMatcher
            .CanFetch(rules, UserAgent, "https://www.trademe.co.nz/a/jobs/it/search")
            .Should().BeTrue();
        RobotsTxtMatcher
            .CanFetch(rules, UserAgent, "https://www.trademe.co.nz/a/jobs/it/listing/6160498813")
            .Should().BeTrue();
        RobotsTxtMatcher
            .CanFetch(rules, UserAgent, "https://www.trademe.co.nz/a/jobs/it/listing/6160498813?rsqid=abc")
            .Should().BeFalse();
    }

    [Fact]
    public void Longest_rule_wins_and_allow_beats_equal_length()
    {
        var rules = RobotsTxtMatcher.Parse("User-agent: *\nDisallow: /private/\nAllow: /private/open\n");
        RobotsTxtMatcher.CanFetch(rules, UserAgent, "https://example.com/private/x").Should().BeFalse();
        RobotsTxtMatcher.CanFetch(rules, UserAgent, "https://example.com/private/open/x").Should().BeTrue();

        var tie = RobotsTxtMatcher.Parse("User-agent: *\nDisallow: /a\nAllow: /a\n");
        RobotsTxtMatcher.CanFetch(tie, UserAgent, "https://example.com/a").Should().BeTrue();
    }

    [Fact]
    public void Empty_or_missing_rules_allow_everything()
    {
        RobotsTxtMatcher.CanFetch(RobotsTxtMatcher.Parse(""), UserAgent, "https://example.com/anything")
            .Should().BeTrue();
    }

    [Fact]
    public void Pathological_wildcard_run_does_not_backtrack()
    {
        var pattern = "/a/" + new string('*', 400);
        var rules = RobotsTxtMatcher.Parse($"User-agent: *\nDisallow: {pattern}\n");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var allowed = RobotsTxtMatcher.CanFetch(rules, UserAgent, "https://www.trademe.co.nz/a/jobs/it/search");
        stopwatch.Stop();

        allowed.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void User_agent_matching_uses_token_boundaries()
    {
        var rules = RobotsTxtMatcher.Parse("User-agent: bot\nDisallow: /blocked\n");

        RobotsTxtMatcher.CanFetch(rules, "Googlebot/2.1", "https://example.com/blocked")
            .Should().BeTrue("'bot' must not match 'Googlebot'");
        RobotsTxtMatcher.CanFetch(rules, "bot/1.0", "https://example.com/blocked")
            .Should().BeFalse();
        RobotsTxtMatcher.CanFetch(rules, "Example-Bot/1.0", "https://example.com/blocked")
            .Should().BeFalse("a hyphen is a token boundary");
    }
}
