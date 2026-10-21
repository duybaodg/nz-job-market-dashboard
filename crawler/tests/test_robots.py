"""Tests for the Google-style robots.txt matcher.

These cover the real rules that matter for this project: SEEK disallows
``*/job/`` and ``*?`` (but allows ``*?keywords``), and Trade Me disallows any
``/a/*?*`` query URL.  The stdlib parser gets all of these wrong.
"""
import sys
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "adapters" / "scrapling"))

from robots import can_fetch, parse_robots  # noqa: E402

SEEK_ROBOTS = """
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
"""

TRADEME_ROBOTS = """
User-agent: *
Allow: /
Disallow: /a/*?*)
Disallow: /a/*search?
Disallow: /a/*rsqid
Disallow: /a/*page=
"""


class RobotsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.seek = parse_robots(SEEK_ROBOTS)
        self.trademe = parse_robots(TRADEME_ROBOTS)
        self.ua = "Mozilla/5.0 (compatible; NZJobMarketDashboardBot/0.2)"

    def test_seek_allows_listing_but_not_job_details(self) -> None:
        self.assertTrue(can_fetch(self.seek, self.ua, "https://nz.seek.com/information-technology-jobs"))
        self.assertFalse(can_fetch(self.seek, self.ua, "https://nz.seek.com/job/95156028"))

    def test_seek_keyword_slices_allowed_but_pagination_blocked(self) -> None:
        self.assertTrue(
            can_fetch(self.seek, self.ua, "https://nz.seek.com/information-technology-jobs?keywords=developer")
        )
        self.assertFalse(
            can_fetch(self.seek, self.ua, "https://nz.seek.com/information-technology-jobs?page=2")
        )

    def test_seek_specific_bot_group_overrides_wildcard(self) -> None:
        self.assertFalse(can_fetch(self.seek, "LinkedInBot", "https://nz.seek.com/information-technology-jobs"))

    def test_trademe_allows_clean_paths_and_blocks_queries(self) -> None:
        self.assertTrue(can_fetch(self.trademe, self.ua, "https://www.trademe.co.nz/a/jobs/it/search"))
        self.assertTrue(
            can_fetch(
                self.trademe,
                self.ua,
                "https://www.trademe.co.nz/a/jobs/it/listing/6160498813",
            )
        )
        self.assertFalse(
            can_fetch(
                self.trademe,
                self.ua,
                "https://www.trademe.co.nz/a/jobs/it/listing/6160498813?rsqid=abc",
            )
        )

    def test_longest_rule_wins_and_allow_beats_equal_length(self) -> None:
        rules = parse_robots("User-agent: *\nDisallow: /private/\nAllow: /private/open\n")
        self.assertFalse(can_fetch(rules, self.ua, "https://example.com/private/x"))
        self.assertTrue(can_fetch(rules, self.ua, "https://example.com/private/open/x"))

        tie = parse_robots("User-agent: *\nDisallow: /a\nAllow: /a\n")
        self.assertTrue(can_fetch(tie, self.ua, "https://example.com/a"))

    def test_user_agent_matching_uses_token_boundaries(self) -> None:
        rules = parse_robots("User-agent: bot\nDisallow: /blocked\n")

        self.assertTrue(can_fetch(rules, "Googlebot/2.1", "https://example.com/blocked"),
                        "'bot' must not match 'Googlebot', so the group does not apply")
        self.assertFalse(can_fetch(rules, "bot/1.0", "https://example.com/blocked"))
        self.assertFalse(can_fetch(rules, "Example-Bot/1.0", "https://example.com/blocked"),
                         "a hyphen is a token boundary")

    def test_empty_or_missing_rules_allow_everything(self) -> None:
        self.assertTrue(can_fetch(parse_robots(""), self.ua, "https://example.com/anything"))

    def test_unmatched_paths_are_allowed(self) -> None:
        rules = parse_robots("User-agent: *\nDisallow: /secret\n")
        self.assertTrue(can_fetch(rules, self.ua, "https://example.com/public"))

    def test_pathological_wildcard_run_does_not_backtrack(self) -> None:
        # Trade Me's live robots.txt contains "Disallow: /a/*****…" with a long
        # run of asterisks. Translating each "*" to ".*" produced a chain that
        # backtracked exponentially and hung the crawl for minutes.
        pattern = "/a/" + "*" * 400
        rules = parse_robots(f"User-agent: *\nDisallow: {pattern}\n")

        start = time.perf_counter()
        allowed = can_fetch(rules, self.ua, "https://www.trademe.co.nz/a/jobs/it/search")
        elapsed = time.perf_counter() - start

        self.assertFalse(allowed)
        self.assertLess(elapsed, 1.0, f"robots match took {elapsed:.2f}s")


if __name__ == "__main__":
    unittest.main()
