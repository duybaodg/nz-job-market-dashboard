"""Site-specific detail parser dispatch.

Round 19: mechanically moved out of ``scrapling_worker.py``. The if/elif order
is byte-for-byte the same as the original.
"""
from __future__ import annotations

from models import RawJobInput
from normalisation import (
    GENERIC_DETAIL_TITLE_RE,
    clean_job_title,
    extract_title,
    parse_jobs,
)
from parsers.ats import parse_airnz_detail, parse_pageup_detail
from parsers.generic import parse_structured_job, structured_job_to_raw
from linkedin import parse_linkedin_job
from parsers.recruiters import (
    parse_absolute_it_detail,
    parse_beyond_recruitment_detail,
    parse_comspek_detail,
    parse_consult_recruitment_detail,
    parse_digital_garage_detail,
    parse_jobseek_detail,
    parse_salt_detail,
    parse_talent_army_detail,
)
from parsers.seek import parse_seek_detail

def parse_job_detail(
    source: str,
    url: str,
    html: str,
    text: str,
    fallback_title: str,
    parser: str = "generic",
) -> RawJobInput:
    if source_data := parse_structured_job(html):
        return structured_job_to_raw(source, url, source_data, text, fallback_title)

    if parser == "absolute-it":
        return parse_absolute_it_detail(source, url, html, text, fallback_title)
    if parser == "talent-army":
        return parse_talent_army_detail(source, url, html, text, fallback_title)
    if parser == "beyond-recruitment":
        return parse_beyond_recruitment_detail(source, url, html, text, fallback_title)
    if parser == "comspek":
        return parse_comspek_detail(source, url, html, text, fallback_title)
    if parser == "consult-recruitment":
        return parse_consult_recruitment_detail(source, url, html, text, fallback_title)
    if parser == "salt":
        return parse_salt_detail(source, url, html, text, fallback_title)
    if parser == "digital-garage":
        return parse_digital_garage_detail(source, url, html, text, fallback_title)
    if parser == "jobseek":
        return parse_jobseek_detail(source, url, html, text, fallback_title)
    if parser == "airnz":
        return parse_airnz_detail(source, url, html, text, fallback_title)
    if parser == "seek":
        return parse_seek_detail(source, url, html, text, fallback_title)
    if parser == "pageup":
        return parse_pageup_detail(source, url, html, text, fallback_title)
    if parser == "linkedin":
        return parse_linkedin_job(source, url, html, text, fallback_title)

    parsed = parse_jobs(source, url, html, text)
    detail = parsed[0]
    extracted = clean_job_title(extract_title(html, text), source)
    fallback = clean_job_title(fallback_title, source) if fallback_title else ""
    # A Next.js detail route can leave a placeholder document title ("Job
    # Details") or a concatenated back-link breadcrumb as the page title even
    # though the listing card already carried the real role name. Prefer the
    # listing title when the page title is one of those placeholders; a page
    # <h1> (the Talent International case) is still trusted over the fallback.
    if fallback and (
        not extracted
        or extracted == "Untitled job"
        or len(extracted) > 160
        or GENERIC_DETAIL_TITLE_RE.search(extracted)
    ):
        title = fallback
    else:
        title = extracted

    return RawJobInput(
        source=source,
        sourceJobId=detail.sourceJobId,
        title=title,
        company=detail.company,
        location=detail.location,
        salaryText=detail.salaryText,
        descriptionText=text[:12000] or None,
        url=url,
        postedDate=detail.postedDate,
        closingDate=detail.closingDate,
    )
