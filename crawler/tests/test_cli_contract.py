"""CLI contract tests: the worker's argparse must accept everything the
registry can send it.

Regression for the 2026-10-11 Xero / Trade Me Jobs failures, where the .NET
adapter passed ``--parser ashby`` / ``--parser trade-me`` but the worker's
choices tuple had fallen behind and argparse exited 2.
"""
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from adapters.scrapling.scrapling_worker import (  # noqa: E402
    FETCHER_CHOICES,
    PARSER_CHOICES,
    parse_args,
)

REGISTRY_PATH = Path(__file__).resolve().parents[1] / "sources" / "sites.json"


def load_sites():
    return json.loads(REGISTRY_PATH.read_text(encoding="utf-8"))["sites"]


class ParserChoiceContractTests(unittest.TestCase):
    def test_every_registry_parser_is_accepted_by_the_cli(self) -> None:
        registry_parsers = {site["parser"] for site in load_sites() if site.get("parser")}
        missing = sorted(registry_parsers - set(PARSER_CHOICES))
        self.assertEqual([], missing, f"registry parsers not accepted by argparse: {missing}")

    def test_every_registry_fetcher_is_accepted_by_the_cli(self) -> None:
        registry_fetchers = {site["fetcher"] for site in load_sites() if site.get("fetcher")}
        missing = sorted(registry_fetchers - set(FETCHER_CHOICES))
        self.assertEqual([], missing, f"registry fetchers not accepted by argparse: {missing}")


class AdapterArgvTests(unittest.TestCase):
    """The exact argv that Infrastructure.Scrapling.ScraplingJobAdapter builds."""

    @staticmethod
    def adapter_argv(source, url, parser, fetcher="http", max_pages=50, delay=5.0):
        argv = [
            "scrapling_worker.py",
            "--source", source,
            "--url", url,
            "--parser", parser,
            "--fetcher-type", fetcher,
            "--max-pages", str(max_pages),
            "--delay-seconds", str(delay),
            "--robots-txt-obey",
        ]
        return argv

    def _parse(self, argv):
        with patch.object(sys, "argv", argv):
            return parse_args()

    def test_xero_argv_parses(self) -> None:
        args = self._parse(
            self.adapter_argv("Xero", "https://careers.xero.com/jobs/", "xero")
        )
        self.assertEqual("Xero", args.source)
        self.assertEqual("xero", args.parser)
        self.assertTrue(args.robots_txt_obey)

    def test_trade_me_jobs_argv_parses(self) -> None:
        args = self._parse(
            self.adapter_argv(
                "Trade Me Jobs", "https://www.trademe.co.nz/a/jobs/it/search", "trade-me"
            )
        )
        self.assertEqual("trade-me", args.parser)

    def test_adapter_argv_parses_for_every_registry_site(self) -> None:
        for site in load_sites():
            if not site.get("parser"):
                continue
            argv = self.adapter_argv(
                site["name"],
                site.get("jobs_url") or site.get("homepage") or "https://example.com",
                site["parser"],
                site.get("fetcher") or "http",
            )
            with self.subTest(site=site["name"], parser=site["parser"]):
                self._parse(argv)

    def test_every_enabled_registry_source_produces_a_valid_argv(self) -> None:
        """The contract the worker depends on: enabled sources can always run."""
        enabled = [site for site in load_sites() if site.get("enabled", True)]
        self.assertTrue(enabled, "the registry should have at least one enabled source")
        for site in enabled:
            argv = self.adapter_argv(
                site["name"],
                site.get("jobs_url") or site.get("homepage") or "https://example.com",
                site["parser"],
                site.get("fetcher") or "http",
            )
            with self.subTest(site=site["name"]):
                self._parse(argv)

    def test_every_global_fetcher_choice_parses(self) -> None:
        """The adapter sends the configured global fetcher, not the per-site one."""
        for fetcher in FETCHER_CHOICES:
            with self.subTest(fetcher=fetcher):
                args = self._parse(
                    self.adapter_argv("Example", "https://example.com/jobs", "generic", fetcher)
                )
                self.assertEqual(fetcher, args.fetcher_type)

    def test_unknown_parser_value_is_rejected_by_argparse(self) -> None:
        """Documents why the .NET adapter must map registry values (exit 2 bug)."""
        with self.assertRaises(SystemExit):
            self._parse(self.adapter_argv("Example", "https://example.com/jobs", "not-a-parser"))


if __name__ == "__main__":
    unittest.main()
