"""Load the NZ job source registry (``crawler/sources/sites.json``).

The registry is the shared contract between the probe tool, the crawler worker
and the database seed: it records, for every site on the NZ IT job website list,
which crawl strategy to use and the platform credentials (public ATS tokens)
needed to run it.  The worker resolves a source by name so the .NET adapter does
not need to know anything beyond the source name and URL.
"""
from __future__ import annotations

import json
import os
from functools import lru_cache
from pathlib import Path
from typing import Any

DEFAULT_REGISTRY_PATH = Path(__file__).resolve().parents[2] / "sources" / "sites.json"


def _registry_path() -> Path:
    override = os.environ.get("CRAWLER_SOURCE_REGISTRY")
    return Path(override) if override else DEFAULT_REGISTRY_PATH


@lru_cache(maxsize=4)
def _load(path: str) -> dict[str, dict[str, Any]]:
    registry_file = Path(path)
    if not registry_file.exists():
        return {}
    data = json.loads(registry_file.read_text())
    return {
        str(site.get("name", "")).strip().lower(): site
        for site in data.get("sites", [])
        if site.get("name")
    }


def load_registry(path: str | os.PathLike[str] | None = None) -> dict[str, dict[str, Any]]:
    """Return the registry keyed by lower-cased source name."""
    return _load(str(path) if path else str(_registry_path()))


def lookup_source(name: str | None) -> dict[str, Any] | None:
    if not name:
        return None
    return load_registry().get(name.strip().lower())


def clear_cache() -> None:
    _load.cache_clear()
