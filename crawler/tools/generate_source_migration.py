#!/usr/bin/env python3
"""Generate the EF Core seed migration for ``job_sources`` from the registry.

The registry (``crawler/sources/sites.json``) is the single source of truth for
which sites we crawl, how (parser/platform) and how often.  Hand-maintaining a
matching SQL migration drifts, so this tool renders the migration from the
registry.  Deterministic UUIDv5 ids keep the migration stable across runs.

Usage::

    python3 crawler/tools/generate_source_migration.py --check   # verify in sync
    python3 crawler/tools/generate_source_migration.py --write
"""
from __future__ import annotations

import argparse
import json
import sys
import uuid
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
REGISTRY = REPO_ROOT / "crawler" / "sources" / "sites.json"
MIGRATIONS_DIR = REPO_ROOT / "backend" / "src" / "Infrastructure" / "Persistence" / "Migrations"
MIGRATION_ID = "20261011000000_SeedFullSourceRegistry"
MIGRATION_FILE = MIGRATIONS_DIR / f"{MIGRATION_ID}.cs"
# Stable namespace so regenerating never changes ids.
ID_NAMESPACE = uuid.UUID("6f1d2c3b-4a59-4e8f-9b7a-2c1d0e9f8a7b")

# Older migrations seeded these names; the registry uses the fuller names. Rename
# instead of inserting a second row, otherwise the same site gets crawled twice.
LEGACY_ALIASES = {"Salt": "Salt NZ", "LinkedIn": "LinkedIn Jobs"}


def source_id(name: str) -> str:
    return str(uuid.uuid5(ID_NAMESPACE, name))


def sql_literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def render() -> str:
    registry = json.loads(REGISTRY.read_text())
    sites = sorted(registry["sites"], key=lambda site: (site.get("tier", 9), site["name"]))

    rows = []
    for site in sites:
        name = site["name"]
        base_url = site.get("jobs_url") or site.get("homepage") or ""
        parser = site.get("parser") or "generic"
        enabled = "TRUE" if site.get("enabled", True) else "FALSE"
        frequency = int(site.get("frequency_minutes") or 1440)
        rows.append(
            "            ({id}, {name}, {url}, 'Scrapling', {parser}, {enabled}, {frequency},\n"
            "             NOW(), NOW(), NOW())".format(
                id=sql_literal(source_id(name)),
                name=sql_literal(name),
                url=sql_literal(base_url),
                parser=sql_literal(parser),
                enabled=enabled,
                frequency=frequency,
            )
        )
    values = ",\n".join(rows)
    names = ", ".join(sql_literal(site["name"]) for site in sites)
    removals = ", ".join(sql_literal(alias) for alias in LEGACY_ALIASES)
    alias_sql = "\n".join(
        f"""        DELETE FROM job_sources
        WHERE name = {sql_literal(alias)}
          AND EXISTS (SELECT 1 FROM job_sources existing WHERE existing.name = {sql_literal(target)});
        UPDATE jobs SET source = {sql_literal(target)} WHERE source = {sql_literal(alias)};
        UPDATE job_sources SET name = {sql_literal(target)}, updated_at = NOW()
        WHERE name = {sql_literal(alias)};"""
        for alias, target in LEGACY_ALIASES.items()
    )

    return f'''using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Persistence.Migrations;

/// <summary>
/// Seeds every site from the NZ IT job website list into <c>job_sources</c>.
/// Generated from crawler/sources/sites.json by
/// crawler/tools/generate_source_migration.py — edit the registry, not this file.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("{MIGRATION_ID}")]
public sealed class SeedFullSourceRegistry : Migration
{{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        -- Reconcile legacy source names with their registry names first.
{alias_sql}

        INSERT INTO job_sources
            (id, name, base_url, method, parser, enabled, crawl_frequency_minutes,
             terms_checked_at, created_at, updated_at)
        VALUES
{values}
        ON CONFLICT (name) DO UPDATE SET
            base_url = EXCLUDED.base_url,
            method = EXCLUDED.method,
            parser = EXCLUDED.parser,
            enabled = EXCLUDED.enabled,
            crawl_frequency_minutes = EXCLUDED.crawl_frequency_minutes,
            terms_checked_at = COALESCE(job_sources.terms_checked_at, EXCLUDED.terms_checked_at),
            updated_at = NOW();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DELETE FROM job_sources WHERE name IN ({names})
          AND name NOT IN ({removals});
        """);
}}
'''


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--write", action="store_true", help="write the migration file")
    ap.add_argument("--check", action="store_true", help="fail if the file is out of date")
    args = ap.parse_args()

    rendered = render()
    if args.write:
        MIGRATION_FILE.write_text(rendered)
        print(f"wrote {MIGRATION_FILE.relative_to(REPO_ROOT)}")
        return 0
    if args.check:
        current = MIGRATION_FILE.read_text() if MIGRATION_FILE.exists() else ""
        if current != rendered:
            print("migration is out of date; run with --write", file=sys.stderr)
            return 1
        print("migration is in sync with the registry")
        return 0

    print(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
