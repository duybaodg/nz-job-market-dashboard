"""Recruitment-agency and board listing/detail parsers.

Round 19: mechanically moved out of ``scrapling_worker.py``.
"""
from __future__ import annotations

import re
from html import unescape
from urllib.parse import urljoin, urlparse

from models import RawJobInput
from normalisation import (
    clean_text,
    extract_label,
    extract_salary,
    html_group,
    human_date,
    iso_date,
    sanitise_location,
)
from parsers.generic import parse_structured_job, structured_location

def parse_absolute_it_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    title = html_group(html, r'<h1[^>]*class="[^"]*heading[^\"]*"[^>]*>(.*?)</h1>') or fallback_title
    location = html_group(html, r'jobs-single__location[^>]*>.*?<a[^>]*>(.*?)</a>')
    salary = html_group(html, r'jobs-single__salary[^>]*>(.*?)</div>')
    posted = html_group(html, r'jobs-single__date[^>]*>(.*?)</div>')
    description = html_group(html, r'jobs-single__content[^>]*>(.*?)(?:<div class="author|</section>)')
    source_id = re.search(r"/it-job/(bh-\d+)", url, re.I)
    return RawJobInput(
        source=source,
        sourceJobId=source_id.group(1).upper() if source_id else None,
        title=title,
        company=None,
        location=location,
        salaryText=salary or extract_salary(text),
        descriptionText=(description or text)[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%d %b %Y",)),
        closingDate=None,
    )


def parse_talent_army_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    title = html_group(html, r'<h2[^>]*>(.*?)</h2>') or fallback_title
    pills = [clean_text(value) for value in re.findall(r'category-pill.*?<p[^>]*>(.*?)</p>', html, re.I | re.S)]
    locations = {"Auckland", "Wellington", "Christchurch", "All New Zealand", "Remote", "Sydney"}
    location = next((value for value in pills if value in locations), None)
    employment = next((value for value in pills if value in {"Permanent", "Contract", "Part-time"}), None)
    posted = html_group(html, r'is-posted[^>]*>\s*Posted\s*</p>\s*<p[^>]*>(.*?)</p>')
    source_id = re.search(r"/job-ads/(\d+)", url)
    description = f"{employment}. {text}" if employment else text
    return RawJobInput(
        source=source,
        sourceJobId=source_id.group(1) if source_id else None,
        title=title,
        company=None,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=description[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%B %d, %Y",)),
        closingDate=None,
    )


def parse_beyond_recruitment_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    title = html_group(html, r'<h1[^>]*class="[^"]*job-title[^"]*"[^>]*>(.*?)</h1>') or fallback_title
    location = html_group(html, r'class="[^"]*job-location[^"]*"[^>]*>.*?<span[^>]*>Location</span>\s*(.*?)\s*</dd>')
    if not location:
        location = html_group(html, r'class="[^"]*job-location[^"]*"[^>]*>\s*(.*?)\s*</dd>')
    disc = html_group(html, r'class="[^"]*discipline[^"]*"[^>]*>\s*<span[^>]*>Discipline</span>\s*(.*?)\s*</dd>')
    job_type = html_group(html, r'class="[^"]*job-type[^"]*"[^>]*>\s*<span[^>]*>Job type</span>\s*(.*?)\s*</dd>')
    ref = html_group(html, r'class="[^"]*job-ref[^"]*"[^>]*>.*?<span[^>]*>Reference</span>\s*(.*?)\s*</dd>')
    posted = html_group(html, r'class="[^"]*date-posted[^"]*"[^>]*>\s*<span[^>]*>Posted</span>\s*(.*?)\s*</dd>')
    description = html_group(html, r'class="[^"]*desc[^"]*"[^>]*>(.*?)(?:</div>\s*<(?:section|div))')
    if not description:
        description = html_group(html, r'class="[^"]*desc[^"]*"[^>]*>(.*?)</div>')
    source_id = re.search(r"/job/[\w-]+-(\d+)", url)
    company = "Beyond Recruitment"
    return RawJobInput(
        source=source,
        sourceJobId=str(ref).strip() if ref else (source_id.group(1) if source_id else None),
        title=title,
        company=company,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=(description or text)[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%d %B %Y", "%B %d, %Y")),
        closingDate=None,
    )


def parse_beyond_recruitment_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    # Extract job cards from Volcanic results-list
    for match in re.finditer(
        r'class="job-title"[^>]*>\s*<a\s+href="(/job/([^"]+))"[^>]*>([^<]*)</a>',
        html,
        re.I,
    ):
        job_url = urljoin(url, match.group(1))
        slug = match.group(2)
        title = clean_text(match.group(3))
        if job_url in seen:
            continue
        seen.add(job_url)
        # Find the surrounding card to get location — search forward only to next card or 1000 chars
        ctx = html[match.start() : match.end() + 1500]
        # Cut at next job-title to avoid grabbing next card's location
        next_title = ctx.find('class="job-title"', 100)
        if next_title > 0:
            ctx = ctx[:next_title]
        loc_match = re.search(r'class="results-job-location"[^>]*>([^<]*)<', ctx, re.I)
        location = clean_text(loc_match.group(1)) if loc_match else None
        desc_match = re.search(r'class="job-description"[^>]*>(.*?)</p>', ctx, re.I | re.S)
        desc = clean_text(desc_match.group(1)) if desc_match else None
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=None,
                title=title,
                company=None,
                location=location,
                salaryText=None,
                descriptionText=desc[:1200] if desc else None,
                url=job_url,
                postedDate=None,
                closingDate=None,
            )
        )
    return jobs


def parse_comspek_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse Comspek International job detail pages (Volcanic ATS)."""
    title = html_group(html, r'<h1[^>]*class="[^"]*job-title[^"]*"[^>]*>(.*?)</h1>') or fallback_title
    # Live pages use <li class="flex-item job-location"><span>Location</span>Auckland</li>
    # while older fixtures use <dd>. Stop at whichever closing tag comes first so
    # the match cannot run on into the rest of the detail block.
    location = html_group(
        html,
        r'class="[^"]*job-location[^"]*"[^>]*>.*?<span[^>]*>Location</span>\s*(.*?)\s*</(?:dd|li)>',
    )
    if not location:
        location = html_group(html, r'class="[^"]*job-location[^"]*"[^>]*>\s*(.*?)\s*</(?:dd|li)>')
    location = sanitise_location(location)
    job_type = html_group(html, r'class="[^"]*job-type[^"]*"[^>]*>\s*<span[^>]*>Job type</span>\s*(.*?)\s*</(?:dd|li)>')
    ref = html_group(html, r'class="[^"]*job-ref[^"]*"[^>]*>.*?<span[^>]*>Reference</span>\s*(.*?)\s*</(?:dd|li)>')
    posted = html_group(html, r'class="[^"]*date-posted[^"]*"[^>]*>\s*<span[^>]*>Posted</span>\s*(.*?)\s*</(?:dd|li)>')
    description = html_group(html, r'class="[^"]*desc[^"]*"[^>]*>(.*?)(?:</div>\s*<(?:section|div))')
    if not description:
        description = html_group(html, r'class="[^"]*desc[^"]*"[^>]*>(.*?)</div>')
    source_id = re.search(r"/job/[\w-]+-(\d+)", url)
    return RawJobInput(
        source=source,
        sourceJobId=str(ref).strip() if ref else (source_id.group(1) if source_id else None),
        title=title,
        company=None,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=(description or text)[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%d %B %Y", "%B %d, %Y")),
        closingDate=None,
    )


def parse_comspek_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Parse Comspek International listing pages (Volcanic ATS)."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'class="job-title"[^>]*>\s*<a\s+href="(/job/([^"]+))"[^>]*>([^<]*)</a>',
        html,
        re.I,
    ):
        job_url = urljoin(url, match.group(1))
        slug = match.group(2)
        title = clean_text(match.group(3))
        if job_url in seen:
            continue
        seen.add(job_url)
        ctx = html[match.start() : match.end() + 1500]
        next_title = ctx.find('class="job-title"', 100)
        if next_title > 0:
            ctx = ctx[:next_title]
        loc_match = re.search(r'class="results-job-location"[^>]*>([^<]*)<', ctx, re.I)
        location = clean_text(loc_match.group(1)) if loc_match else None
        desc_match = re.search(r'class="job-description"[^>]*>(.*?)</p>', ctx, re.I | re.S)
        desc = clean_text(desc_match.group(1)) if desc_match else None
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=None,
                title=title,
                company=None,
                location=location,
                salaryText=None,
                descriptionText=desc[:1200] if desc else None,
                url=job_url,
                postedDate=None,
                closingDate=None,
            )
        )
    return jobs


def parse_salt_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse Salt NZ job detail pages (WordPress + Schema.org JobPosting)."""
    source_data = parse_structured_job(html)
    if source_data:
        location = structured_location(source_data)
        company = (source_data.get("hiringOrganization") or {}).get("name")
        source_job_id = (source_data.get("identifier") or {}).get("value")
        employment_type = str(source_data.get("employmentType") or "").replace("_", "-").title()
        description = clean_text(str(source_data.get("description") or text))
        if employment_type:
            description = f"{employment_type}. {description}"
        # Extract salary from Schema.org baseSalary (more reliable than text scan)
        base_salary = source_data.get("baseSalary", {})
        salary = extract_salary(text)  # fallback
        if isinstance(base_salary, dict):
            sv = base_salary.get("value", {})
            if isinstance(sv, dict):
                val = str(sv.get("value", ""))
            else:
                val = str(sv) if sv else ""
            if val and val != "None":
                if "-" in val:
                    parts = val.split("-")
                    salary = f"${parts[0].strip()}k - ${parts[1].strip()}k"
                else:
                    salary = f"${val}k"
        return RawJobInput(
            source=source,
            sourceJobId=str(source_job_id) if source_job_id else None,
            title=clean_text(str(source_data.get("title") or fallback_title)),
            company=clean_text(str(company)) or None,
            location=clean_text(str(location)) or None,
            salaryText=salary,
            descriptionText=description[:12000] or None,
            url=url,
            postedDate=iso_date(source_data.get("datePosted")),
            closingDate=iso_date(source_data.get("validThrough")),
        )
    # Fallback: parse WordPress job detail page
    title = html_group(html, r'<h1[^>]*class="[^"]*job[^"]*title[^"]*"[^>]*>(.*?)</h1>')
    if not title:
        title = html_group(html, r'<h1[^>]*>(.*?)</h1>') or fallback_title
    location = extract_label(text, "Location")
    salary = extract_salary(text)
    job_type = extract_label(text, "Job type") or extract_label(text, "Work type")
    desc_match = re.search(r'class="[^"]*description[^"]*"[^>]*>(.*?)(?:</div>\s*<(?:div|section))', html, re.DOTALL)
    description = clean_text(desc_match.group(1)) if desc_match else text[:12000]
    company = None  # WordPress detail fallback doesn't extract company
    return RawJobInput(
        source=source,
        sourceJobId=None,
        title=title,
        company=company,
        location=location,
        salaryText=salary,
        descriptionText=description or None,
        url=url,
        postedDate=None,
        closingDate=None,
    )


def parse_salt_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Parse Salt NZ listing pages (WordPress .job-item cards)."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'class="job-item__title"[^>]*>\s*<a\s+href="(https?://[^"]+/jobs/([^"]+))"[^>]*>([^<]*)</a>',
        html, re.I,
    ):
        job_url = match.group(1)
        slug = match.group(2)
        title = clean_text(match.group(3))
        if job_url in seen:
            continue
        seen.add(job_url)
        ctx_start = max(0, match.start() - 1500)
        ctx = html[ctx_start : match.end() + 500]
        loc_match = re.search(r'highlights__item--location[^<]*</i>\s*([^<]+)<', ctx, re.I)
        location = clean_text(loc_match.group(1)) if loc_match else None
        type_match = re.search(r'highlights__item--type[^<]*</i>\s*([^<]+)<', ctx, re.I)
        job_type = clean_text(type_match.group(1)) if type_match else None
        desc_match = re.search(r'job-item__excerpt[^>]*>([^<]+)<', ctx, re.I)
        desc = clean_text(desc_match.group(1)) if desc_match else None
        jobs.append(RawJobInput(
            source=source,
            sourceJobId=None,
            title=title,
            company=None,
            location=location,
            salaryText=None,
            descriptionText=desc[:1200] if desc else None,
            url=job_url,
            postedDate=None,
            closingDate=None,
        ))
    return jobs


def parse_digital_garage_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse Digital Garage job detail pages (JobAdder integration)."""
    title = html_group(html, r'<h1[^>]*>(.*?)</h1>') or fallback_title
    # Extract job ID from URL: /jobs/{id}-{slug}
    source_id = re.search(r"/jobs/(\d+)-", url)
    job_id = source_id.group(1) if source_id else None
    # Extract JobAdder reference number (7 digits)    
    ref_match = re.search(r'(\d{7,8})\s+\1', text)
    ref = ref_match.group(1) if ref_match else None
    # Extract date: DD.MM.YYYY  
    date_match = re.search(r'(\d{2}\.\d{2}\.\d{4})', text)
    posted = date_match.group(1) if date_match else None
    # Extract type: try more specific patterns first
    type_match = re.search(r'(Permanent\s*/\s*Full\s*Time|Permanent\s*/\s*Part\s*Time|Full\s*Time)', text, re.I)
    if not type_match:
        type_match = re.search(r'(Contract|Temporary|Fixed\s*Term)', text, re.I)
    job_type = type_match.group(1) if type_match else None
    # Description: use full text stripped of header nav, or just text past the type
    desc = text
    # Try to find the job title repeated after "//" pattern in the body
    marker = re.search(r'//\s*.+?\s+(?:Permanent|Contract|Full|Part)', text)
    if marker:
        desc = text[marker.start():].strip()
        apply_pos = desc.find("Apply now")
        if apply_pos > 0:
            desc = desc[apply_pos + 9:].strip()  # Skip "Apply now"
    elif job_type:
        desc_start = text.rfind(job_type)  # Use last occurrence of type
        apply_pos = text.find("Apply now", desc_start)
        if apply_pos > desc_start:
            desc = text[desc_start + len(job_type):apply_pos].strip()
    return RawJobInput(
        source=source,
        sourceJobId=job_id,
        title=title,
        company=None,
        location=None,
        salaryText=extract_salary(text),
        descriptionText=(job_type + ". " + desc if job_type else desc)[:12000] or None,
        url=url,
        postedDate=human_date(posted, ("%d.%m.%Y",)),
        closingDate=None,
    )


def parse_digital_garage_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Parse Digital Garage listing page (JobAdder iframe links)."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'href="(https://digitalgarage\.co\.nz/jobs/(\d+)-([^"]+))"',
        html, re.I,
    ):
        job_url = match.group(1)
        job_id = match.group(2)
        slug = match.group(3)
        if job_url in seen:
            continue
        seen.add(job_url)
        title = slug.replace("-", " ").title()
        jobs.append(RawJobInput(
            source=source,
            sourceJobId=job_id,
            title=title,
            company=None,
            location=None,
            salaryText=None,
            descriptionText=None,
            url=job_url,
            postedDate=None,
            closingDate=None,
        ))
    return jobs


def parse_jobseek_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    """Parse JobSeek NZ job detail pages (Bootstrap + Schema.org)."""
    # Try Schema.org first
    source_data = parse_structured_job(html)
    if source_data:
        location = structured_location(source_data)
        company = (source_data.get("hiringOrganization") or {}).get("name")
        source_job_id = (source_data.get("identifier") or {}).get("value")
        description = clean_text(str(source_data.get("description") or text))
        return RawJobInput(
            source=source,
            sourceJobId=str(source_job_id) if source_job_id else None,
            title=clean_text(str(source_data.get("title") or fallback_title)),
            company=clean_text(str(company)) or None,
            location=clean_text(str(location)) or None,
            salaryText=extract_salary(text),
            descriptionText=description[:12000] or None,
            url=url,
            postedDate=iso_date(source_data.get("datePosted")),
            closingDate=iso_date(source_data.get("validThrough")),
        )
    # Fallback: parse HTML list items
    title = html_group(html, r'<h1[^>]*>(.*?)</h1>') or fallback_title
    location = html_group(html, r'<strong>Location:</strong>\s*([^<]+)<',)
    category = html_group(html, r'<strong>Category:</strong>\s*([^<]+)<',)
    employment = html_group(html, r'<strong>Employment:</strong>\s*([^<]+)<',)
    closing = html_group(html, r'<strong>Closing:</strong>\s*([^<]+)<',)
    ref = html_group(html, r'<strong>Reference:</strong>\s*([^<]+)<',)
    desc_match = re.search(r'class="[^"]*description[^"]*"[^>]*>(.*?)(?:</div>\s*<(?:div|section))', html, re.DOTALL)
    description = clean_text(desc_match.group(1)) if desc_match else text[:12000]
    return RawJobInput(
        source=source,
        sourceJobId=ref.strip() if ref else None,
        title=title,
        company=None,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=description or None,
        url=url,
        postedDate=None,
        closingDate=human_date(closing, ("%d %b %Y",)),
    )


def parse_jobseek_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    """Parse JobSeek NZ listing pages (Bootstrap cards)."""
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'href="(/jobs/([^"]+))"[^>]*>.*?<h2[^>]*class="jp-card-title"[^>]*>([^<]+)<',
        html, re.I | re.S,
    ):  # jobseek listing
        job_url = urljoin(url, match.group(1))
        slug = match.group(2)
        title = clean_text(match.group(3))
        if job_url in seen or '/Jobs' in match.group(1):
            continue
        seen.add(job_url)
        jobs.append(RawJobInput(
            source=source,
            sourceJobId=None,
            title=title,
            company=None,
            location=None,
            salaryText=None,
            descriptionText=None,
            url=job_url,
            postedDate=None,
            closingDate=None,
        ))
    return jobs


def parse_consult_recruitment_detail(
    source: str, url: str, html: str, text: str, fallback_title: str
) -> RawJobInput:
    # Guard: if this is a listing page (has many "See job" links), reject
    see_job_count = len(re.findall(r'>See job</a>', html, re.I))
    if see_job_count >= 10:
        raise ValueError("Page appears to be a job listing, not a detail page.")
    title = html_group(html, r'<h1[^>]*>.*?<strong>([^<]+)</strong>') or fallback_title
    if not title:
        h1_text = html_group(html, r'<h1[^>]*>(.*?)</h1>')
        if h1_text:
            # Try to extract the last meaningful text from H1 (after <br> or <span>)
            parts = re.split(r'<(?:br|/span)[^>]*>', h1_text, flags=re.I)
            title = clean_text(parts[-1]) if parts else h1_text
    if not title:
        title = fallback_title
    # Try HTML-based extraction first (more reliable)
    loc_strong = re.search(r'Location:\s*<br[^>]*>\s*<strong>([^<]+)</strong>', html, re.I)
    if loc_strong:
        location = loc_strong.group(1).strip()
    else:
        location = extract_label(text, "Location") or extract_label(text, "Location:")
    job_type = html_group(html, r'Type:\s*<br[^>]*>\s*<strong>([^<]+)</strong>')
    if not job_type:
        job_type = extract_label(text, "Type") or extract_label(text, "Type:")
    sector = html_group(html, r'class="[^"]*job-sector[^"]*"[^>]*>([^<]+)<')
    # New domain (Round 17): the posted date moved into the H1 as a leading
    # <span>19 October 2026</span>; keep the legacy class fallbacks for any old
    # cached markup and fixture.
    posted = html_group(html, r'<h1[^>]*>\s*<span>([^<]+)</span>')
    if not posted:
        posted = html_group(html, r'class="[^"]*job-date[^"]*"[^>]*>([^<]+)<')
    if not posted:
        posted = html_group(html, r'class="[^"]*date[^"]*"[^>]*>([^<]+)<')
    description = None
    # Find text-editor widget that comes after the H1 (job description)
    h1_pos = html.find("<h1")
    if h1_pos > 0:
        after_h1 = html[h1_pos:]
        desc_block = re.search(
            r'data-widget_type="text-editor\.default"[^>]*>(.*?)(?:</div>\s*</div>\s*</div>)',
            after_h1, re.DOTALL
        )
        if desc_block:
            description = clean_text(desc_block.group(1))
    if not description:
        description = text[:12000] if text else None
    source_id = re.search(r"/job/([^/]+)/?$", url)
    slug = source_id.group(1) if source_id else None
    # Also extract category from WordPress pattern for IT filtering
    category_match = re.search(r'Category:\s*<br[^>]*>\s*<strong>([^<]+)</strong>', html, re.I)
    category = category_match.group(1).replace("&amp;", "&").strip() if category_match else None
    # Prefix description with category and type for downstream IT filtering
    prefix_parts = [p for p in (category, job_type) if p]
    prefix = ". ".join(part for part in prefix_parts if part)
    final_desc = description or text[:12000] or None
    if prefix and final_desc:
        final_desc = f"{prefix}. {final_desc}"
    return RawJobInput(
        source=source,
        sourceJobId=slug,
        title=title,
        company=None,
        location=location,
        salaryText=extract_salary(text),
        descriptionText=final_desc,
        url=url,
        postedDate=human_date(posted, ("%d %B %Y",)),
        closingDate=None,
    )


def parse_consult_recruitment_listing(
    source: str, url: str, html: str, text: str
) -> list[RawJobInput]:
    jobs: list[RawJobInput] = []
    seen: set[str] = set()
    for match in re.finditer(
        r'class="ja_job_link__btn"[^>]*href="(https?://[^"]*/job/([^/"]+)/?)"',
        html,
        re.I,
    ):
        job_url = urljoin(url, match.group(1)) if not match.group(1).startswith("http") else match.group(1)
        slug = match.group(2)
        if job_url in seen:  # consult line 583
            continue
        seen.add(job_url)
        ctx_start = max(0, match.start() - 2000)
        ctx = html[ctx_start : match.end() + 500]
        title_match = re.search(r'class="[^"]*elementor-heading-title[^"]*"[^>]*>\s*<a[^>]*>([^<]+)</a>', ctx, re.I)
        title = clean_text(title_match.group(1)) if title_match else slug.replace("-", " ").title()
        sector_match = re.search(r'data-sector="([^"]*)"', ctx, re.I)
        loc_match = re.search(r'data-location="([^"]*)"', ctx, re.I)
        type_match = re.search(r'data-type="([^"]*)"', ctx, re.I)
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=slug,
                title=title,
                company=None,
                location=loc_match.group(1).replace("-", " ").title() if loc_match else None,
                salaryText=None,
                descriptionText=None,
                url=job_url,
                postedDate=None,
                closingDate=None,
            )
        )
    return jobs


def parse_sjs_listing(source: str, url: str, html: str) -> list[RawJobInput]:
    """Parse the rendered Student Job Search card grid.

    The new /jobs board is a split view: every /jobs/<slug> route renders the
    full card grid and opens the selected role in a drawer whose body is behind
    an auth/skeleton placeholder. The cards are therefore the authoritative,
    fetchable data; following detail routes would only re-render the same grid
    and waste a browser page load per card. No company field is published on a
    card, so company stays None rather than being guessed.
    """
    jobs: list[RawJobInput] = []
    blocks = re.split(r'<article class="student-job-card\b', html, flags=re.I)[1:]
    for block in blocks:
        link = re.search(
            r'class="student-job-card-link"[^>]*href="([^"]+)"', block, re.I | re.S
        )
        title_match = re.search(r"<h3[^>]*>(.*?)</h3>", block, re.I | re.S)
        if not link or not title_match:
            continue
        title = clean_text(title_match.group(1))
        if not title:
            continue

        def meta(label: str) -> str | None:
            match = re.search(
                rf'<dt class="sr-only">{re.escape(label)}</dt>\s*<dd[^>]*>(.*?)</dd>',
                block,
                re.I | re.S,
            )
            return clean_text(match.group(1)) if match else None

        location = meta("Location")
        work_type = meta("Work type")
        pay = meta("Pay")
        advert_dates = meta("Advert dates") or ""
        posted_match = re.search(
            r"Posted\s+(\d{1,2}\s+[A-Za-z]+\s+\d{4})", advert_dates, re.I
        )
        closing_match = re.search(
            r"Closes\s+(\d{1,2}\s+[A-Za-z]+\s+\d{4})", advert_dates, re.I
        )

        body = re.search(
            r'<div class="student-job-card-body[^"]*">(.*?)</div>\s*</article>',
            block,
            re.I | re.S,
        )
        body_html = body.group(1) if body else ""
        bullets = [
            clean_text(item)
            for item in re.findall(r"<li[^>]*>(.*?)</li>", body_html, re.I | re.S)
        ]
        summary = re.search(
            r'class="student-job-summary"[^>]*>(.*?)</div>', body_html, re.I | re.S
        )
        description_parts = [part for part in bullets if part]
        if summary:
            description_parts.append(clean_text(summary.group(1)))
        description = " ".join(dict.fromkeys(description_parts))
        if work_type:
            description = f"{work_type}. {description}".strip()
        if pay:
            description = f"Pay: {pay}. {description}".strip()

        job_url = urljoin(url, unescape(link.group(1)))
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=urlparse(job_url).path.rstrip("/").rsplit("/", 1)[-1] or None,
                title=title,
                company=None,
                location=location,
                salaryText=pay,
                descriptionText=description or None,
                url=job_url,
                postedDate=human_date(posted_match.group(1) if posted_match else None, ("%d %B %Y", "%d %b %Y")),
                closingDate=human_date(closing_match.group(1) if closing_match else None, ("%d %B %Y", "%d %b %Y")),
            )
        )
    return jobs


def parse_employment_hero_listing(source: str, url: str, html: str) -> list[RawJobInput]:
    """Parse Employment Hero's server-rendered job cards.

    The listing is a WordPress Interactivity API block; the hydrated cards are
    duplicated from a <template>, so only ``a[data-wp-each-child]`` instances are
    read. Card data is enough to gate by technology title; the branch then fetches
    the detail page for each surviving role to pick up the JobPosting JSON-LD.
    """
    jobs: list[RawJobInput] = []
    for match in re.finditer(
        r'<a data-wp-each-child href="([^"]+)"[^>]*>(.*?)</a>', html, re.I | re.S
    ):
        job_url = urljoin(url, unescape(match.group(1)))
        block = match.group(2)
        title = html_group(block, r'class="eh-job-item__title"[^>]*>(.*?)</p>')
        if not title:
            continue
        company = html_group(
            block, r'class="eh-job-item__company"[^>]*>\s*<div[^>]*>(.*?)</div>'
        )
        location = html_group(
            block,
            r'data-wp-text="context\.item\.vendor_location_name"[^>]*>(.*?)</span>',
        )
        salary = html_group(
            block,
            r'data-wp-text="context\.item\.formatted_salary_range"[^>]*>(.*?)</span>',
        )
        posted = html_group(
            block,
            r'class="eh-job-item__posted-date"[^>]*>.*?<span[^>]*>(.*?)</span>',
        )
        tags = [
            clean_text(tag)
            for tag in re.findall(
                r'data-wp-each-child class="eh-job-item__tag"[^>]*>(.*?)</span>',
                block,
                re.I | re.S,
            )
        ]
        jobs.append(
            RawJobInput(
                source=source,
                sourceJobId=urlparse(job_url).path.rstrip("/").rsplit("/", 1)[-1] or None,
                title=title,
                company=company,
                location=location,
                salaryText=salary,
                descriptionText=". ".join(tag for tag in tags if tag) or None,
                url=job_url,
                postedDate=None,
                closingDate=None,
            )
        )
    return jobs
