"""Shared technical-skill taxonomy and extractor.

This module is the Python half of the single-source-of-truth contract: the
taxonomy data lives in ``crawler/skills_taxonomy.json`` and is read by both
this module (used by the scrapling worker) and by the .NET ingestion pipeline
(``backend/src/Application/Jobs/SkillsTaxonomy.cs``).  Never hard-code skill
lists anywhere else.

The extractor matches aliases with word boundaries, applies the per-skill
false-positive guards declared in the JSON (``deny`` and ``contextAny``) and
caps the result at ``maxSkillsPerJob`` so a verbose description cannot turn
into keyword soup.
"""

from __future__ import annotations

import json
import re
from functools import lru_cache
from pathlib import Path
from typing import Any, Iterable

TAXONOMY_PATH = Path(__file__).resolve().parent / "skills_taxonomy.json"


class _Rule:
    __slots__ = ("name", "skill_type", "order", "aliases", "deny", "context", "window")

    def __init__(
        self,
        name: str,
        skill_type: str,
        order: int,
        aliases: list[tuple[re.Pattern[str], bool]],
        deny: list[re.Pattern[str]],
        context: list[re.Pattern[str]],
        window: int,
    ) -> None:
        self.name = name
        self.skill_type = skill_type
        self.order = order
        self.aliases = aliases
        self.deny = deny
        self.context = context
        self.window = window


def _alias_pattern(alias: str, case_sensitive: bool) -> re.Pattern[str]:
    flags = 0 if case_sensitive else re.IGNORECASE
    return re.compile(r"(?<![A-Za-z0-9])" + re.escape(alias) + r"(?![A-Za-z0-9])", flags)


class SkillExtractor:
    """Loads one taxonomy file and extracts canonical skill names from text."""

    def __init__(self, taxonomy_path: str | Path = TAXONOMY_PATH) -> None:
        path = Path(taxonomy_path)
        with path.open(encoding="utf-8") as handle:
            data = json.load(handle)

        self.max_skills_per_job = int(data.get("maxSkillsPerJob", 15))
        self.categories: list[str] = []
        self._rules: list[_Rule] = []
        self._by_name: dict[str, tuple[str, str]] = {}
        order = 0
        for category in data.get("categories", []):
            skill_type = category["type"]
            self.categories.append(skill_type)
            for skill in category.get("skills", []):
                name = skill["name"]
                aliases: list[tuple[re.Pattern[str], bool]] = []
                case_sensitive_default = bool(skill.get("caseSensitive", False))
                for alias in skill.get("aliases") or [name]:
                    if isinstance(alias, dict):
                        text = alias["text"]
                        case_sensitive = bool(alias.get("caseSensitive", case_sensitive_default))
                    else:
                        text = alias
                        case_sensitive = case_sensitive_default
                    aliases.append((_alias_pattern(text, case_sensitive), case_sensitive))

                deny = [
                    re.compile(pattern, re.IGNORECASE)
                    for pattern in skill.get("deny", [])
                ]
                context = [
                    re.compile(pattern, re.IGNORECASE)
                    for pattern in skill.get("contextAny", [])
                ]
                self._rules.append(
                    _Rule(
                        name=name,
                        skill_type=skill_type,
                        order=order,
                        aliases=aliases,
                        deny=deny,
                        context=context,
                        window=int(skill.get("contextWindow", 0) or 0),
                    )
                )
                self._by_name[name.casefold()] = (name, skill_type)
                order += 1

    def canonical(self, name: str) -> tuple[str, str] | None:
        """Return the canonical (name, type) for a skill name, if known."""
        return self._by_name.get(name.casefold())

    def extract(self, text: str | None) -> list[dict[str, str]]:
        """Return up to ``maxSkillsPerJob`` skills ordered by mention count."""
        if not text:
            return []

        scored: list[tuple[int, int, str, str]] = []
        for rule in self._rules:
            count = self._count(rule, text)
            if count:
                scored.append((count, rule.order, rule.name, rule.skill_type))

        scored.sort(key=lambda item: (-item[0], item[1]))
        return [
            {"name": name, "type": skill_type}
            for _, _, name, skill_type in scored[: self.max_skills_per_job]
        ]

    def extract_for_job(self, job: dict[str, Any]) -> list[dict[str, str]]:
        """Extract from a serialised RawJobInput (title + full description)."""
        title = job.get("title") or ""
        description = job.get("descriptionText") or job.get("description") or ""
        return self.extract(f"{title}\n{description}")

    def normalise_provided(self, skills: Iterable[dict[str, Any]]) -> list[dict[str, str]]:
        """Canonicalise a caller-provided skill list (e.g. from another worker)."""
        result: list[dict[str, str]] = []
        seen: set[str] = set()
        for skill in skills:
            name = str(skill.get("name") or skill.get("skillName") or "").strip()
            if not name:
                continue
            canonical = self.canonical(name)
            if canonical is None:
                continue
            canonical_name, canonical_type = canonical
            key = canonical_name.casefold()
            if key in seen:
                continue
            seen.add(key)
            result.append({"name": canonical_name, "type": canonical_type})
            if len(result) >= self.max_skills_per_job:
                break
        return result

    @staticmethod
    def _count(rule: _Rule, text: str) -> int:
        count = 0
        for pattern, _ in rule.aliases:
            for match in pattern.finditer(text):
                if rule.deny and _denied(text, match.end(), rule.deny):
                    continue
                if rule.context and not _in_context(text, match.start(), match.end(), rule):
                    continue
                count += 1
        return count


def _denied(text: str, end: int, deny: list[re.Pattern[str]]) -> bool:
    remainder = text[end:]
    return any(pattern.match(remainder) for pattern in deny)


def _in_context(text: str, start: int, end: int, rule: _Rule) -> bool:
    if rule.window > 0:
        window = text[max(0, start - rule.window) : min(len(text), end + rule.window)]
    else:
        window = text
    return any(pattern.search(window) for pattern in rule.context)


@lru_cache(maxsize=1)
def default_extractor() -> SkillExtractor:
    return SkillExtractor(TAXONOMY_PATH)


def extract_skills(text: str | None) -> list[dict[str, str]]:
    """Convenience wrapper around the shared default extractor."""
    return default_extractor().extract(text)


def extract_job_skills(job: dict[str, Any]) -> list[dict[str, str]]:
    """Convenience wrapper for a serialised RawJobInput mapping."""
    return default_extractor().extract_for_job(job)
