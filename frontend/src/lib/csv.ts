import { formatAdvertisedSalary } from './salary'
import { employerLines, locationLabel, postedBasisLabel, workModeLabel } from './jobDisplay'
import { jobFilterChips, type JobFilterState, type JobSort } from './urlState'
import type { Job } from '../types/api'

export type CsvExportMeta = {
  view: string
  filters: JobFilterState
  sort: JobSort
  period: string
  generatedAt: string
  definitions: string[]
}

function escape(value: unknown): string {
  const text = value == null ? '' : String(value)
  return `"${text.replaceAll('"', '""')}"`
}

function row(values: unknown[]): string {
  return values.map(escape).join(',')
}

/**
 * Builds the filtered CSV. It exports exactly the rows the screen shows (same
 * filter + sort input), and states the applied filters, period, metric
 * definitions and extraction timestamp in a comment preamble.
 */
export function buildJobsCsv(jobs: Job[], meta: CsvExportMeta): string {
  const chips = jobFilterChips(meta.filters)
  const lines: string[] = [
    `# NZ Jobs Intel export — ${meta.view}`,
    `# Generated (UTC): ${meta.generatedAt}`,
    `# Applied filters: ${chips.length > 0 ? chips.map((chip) => `${chip.label}=${chip.display}`).join('; ') : 'none'}`,
    `# Reporting period: ${meta.period}`,
    `# Sorting: ${meta.sort}`,
    `# Row count: ${jobs.length}`,
    ...meta.definitions.map((definition) => `# Definition: ${definition}`),
    '',
    row([
      'Title',
      'Hiring employer',
      'Employer disclosure',
      'Advertiser',
      'Advertiser type',
      'Location',
      'Region',
      'Work mode',
      'Employment type',
      'Seniority',
      'Role family (inferred)',
      'Advertised salary',
      'Salary currency',
      'Salary period',
      'Salary min',
      'Salary max',
      'Salary parse flagged',
      'Posted date basis',
      'Posted date',
      'First seen',
      'Closing date',
      'Source',
      'Source category',
      'Skills',
      'Listing URL',
    ]),
  ]

  for (const job of jobs) {
    const employer = employerLines(job)
    const posted = postedBasisLabel(job)

    lines.push(
      row([
        job.title,
        employer.primary,
        job.employerDisclosure,
        job.advertiser,
        job.advertiserType,
        locationLabel(job),
        job.region ?? '',
        workModeLabel(job),
        job.employmentType,
        job.seniority,
        job.roleFamily,
        formatAdvertisedSalary(job),
        job.salaryCurrency,
        job.salaryPeriod,
        job.salaryMin ?? '',
        job.salaryMax ?? '',
        job.salaryQuestionable ? 'yes' : 'no',
        posted.basis,
        job.postedDate ?? '',
        job.firstSeenAt,
        job.closingDate ?? '',
        job.source,
        job.sourceCategory,
        [...new Set(job.skills)].join(' | '),
        job.url,
      ]),
    )
  }

  return `${lines.join('\n')}\n`
}

export function downloadCsv(filename: string, content: string) {
  const blob = new Blob([content], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  document.body.appendChild(link)
  link.click()
  document.body.removeChild(link)
  URL.revokeObjectURL(url)
}
