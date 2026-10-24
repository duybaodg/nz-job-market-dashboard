"""Raw crawler data models.

Round 19: mechanically moved out of ``scrapling_worker.py``.
"""
from __future__ import annotations

from dataclasses import dataclass

@dataclass(frozen=True)
class RawJobInput:
    source: str
    sourceJobId: str | None
    title: str
    company: str | None
    location: str | None
    salaryText: str | None
    descriptionText: str | None
    url: str
    postedDate: str | None
    closingDate: str | None
    #: False only when the source itself says the posting is closed. The .NET
    #: ingest deactivates an existing row instead of saving a new active job.
    isActive: bool = True


@dataclass(frozen=True)
class RawJobPageInput:
    source: str
    url: str
    rawHtml: str | None
    markdown: str | None
    rawJson: str | None
    crawledAt: str
