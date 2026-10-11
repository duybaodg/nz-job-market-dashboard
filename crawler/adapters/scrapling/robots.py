"""Robots.txt parsing and matching with Google-style ``*`` and ``$`` wildcards.

Python's stdlib :class:`urllib.robotparser.RobotFileParser` implements only the
1994 draft: it ignores ``*`` and ``$`` and therefore treats rules such as
``Disallow: */job/`` or ``Disallow: *?`` as literal paths.  Real-world
robots.txt files rely on those wildcards, so using the stdlib parser silently
crawls pages a site asked us not to fetch.

This module implements the widely-used matching algorithm:

* rule paths support ``*`` (any sequence) and a trailing ``$`` (end anchor)
* the most specific (longest) matching rule wins
* on an equal-length tie, ``Allow`` wins
* ``User-agent`` groups are selected by longest case-insensitive match,
  falling back to ``*``
"""
from __future__ import annotations

import re
from dataclasses import dataclass
from functools import lru_cache
from urllib.parse import urlparse

Group = tuple[tuple[str, ...], tuple[tuple[bool, str], ...]]


@dataclass(frozen=True)
class RobotsRules:
    groups: tuple[Group, ...] = ()

    @property
    def is_empty(self) -> bool:
        return not self.groups


def parse_robots(text: str) -> RobotsRules:
    """Parse robots.txt into ``(user_agents, rules)`` groups."""
    groups: list[Group] = []
    agents: list[str] = []
    rules: list[tuple[bool, str]] = []

    def flush() -> None:
        nonlocal agents, rules
        if agents:
            groups.append((tuple(agents), tuple(rules)))
        agents = []
        rules = []

    for raw_line in text.splitlines():
        line = raw_line.split("#", 1)[0].strip()
        if not line or ":" not in line:
            continue
        field, _, value = line.partition(":")
        field = field.strip().lower()
        value = value.strip()
        if field == "user-agent":
            if rules:  # a new agent line after rules starts a new group
                flush()
            agents.append(value.lower())
        elif field in ("allow", "disallow"):
            if not agents:
                agents.append("*")
            rules.append((field == "allow", value))

    flush()
    return RobotsRules(tuple(groups))


@lru_cache(maxsize=65_536)
def _pattern_to_regex(pattern: str) -> re.Pattern[str] | None:
    if pattern == "":
        return None
    anchored_end = pattern.endswith("$")
    body = pattern[:-1] if anchored_end else pattern
    escaped = re.escape(body).replace(r"\*", ".*")
    # Collapse runs of wildcards. Some real robots.txt files contain a rule such
    # as "Disallow: /a/*****…" (Trade Me); translating every "*" literally gives
    # a regex of ".*****…" that backtracks exponentially on a short path and
    # hangs the whole crawl. ".*.*" and ".*" match the same language, so one
    # wildcard per run is enough.
    escaped = re.sub(r"(?:\.\*)+", ".*", escaped)
    return re.compile("^" + escaped + ("$" if anchored_end else ""))


def _group_score(agents: tuple[str, ...], user_agent: str) -> int | None:
    """Longest matching agent token for this group, or None if it does not apply."""
    lowered = user_agent.lower()
    score: int | None = None
    for agent in agents:
        if agent == "*":
            score = max(score or 0, 0)
        elif agent and agent in lowered:
            score = max(score or 0, len(agent))
    return score


def can_fetch(rules: RobotsRules, user_agent: str, url: str) -> bool:
    """Return True when ``user_agent`` may fetch ``url`` per the parsed rules."""
    if rules.is_empty:
        return True

    best_score = -1
    chosen: tuple[tuple[bool, str], ...] = ()
    for agents, group_rules in rules.groups:
        score = _group_score(agents, user_agent)
        if score is None:
            continue
        if score > best_score:
            best_score = score
            chosen = group_rules

    parsed = urlparse(url)
    path = parsed.path or "/"
    if parsed.query:
        path = f"{path}?{parsed.query}"

    matches: list[tuple[int, bool]] = []
    for allow, pattern in chosen:
        regex = _pattern_to_regex(pattern)
        if regex and regex.match(path):
            matches.append((len(pattern), allow))
    if not matches:
        return True

    longest = max(length for length, _ in matches)
    # Equal-length Allow beats Disallow.
    return any(allow for length, allow in matches if length == longest)


def allows(rules: RobotsRules, user_agent: str, url: str) -> bool:
    return can_fetch(rules, user_agent, url)
