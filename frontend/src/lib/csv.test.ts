import { describe, expect, it } from 'vitest'
import { buildJobsCsv } from './csv'
import { defaultJobFilters } from './urlState'
import type { Job } from '../types/api'

function job(overrides: Partial<Job> = {}): Job {
  return {
    id: '1',
    source: 'SEEK NZ',
    title: 'Data Engineer',
    company: 'Xero',
    location: 'Auckland',
    region: 'Auckland',
    salaryMin: 120000,
    salaryMax: 140000,
    salaryText: '$120,000 - $140,000',
    salaryCurrency: 'NZD',
    salaryPeriod: 'Year',
    salaryQuestionable: false,
    salaryQuestionableReason: null,
    employmentType: 'Full-time',
    seniority: 'Senior',
    workMode: 'Hybrid',
    experienceBand: '2-5 years',
    industry: null,
    descriptionSummary: null,
    url: 'https://example.com/job',
    postedDate: '2026-10-10T00:00:00Z',
    closingDate: null,
    firstSeenAt: '2026-10-11T00:00:00Z',
    lastSeenAt: '2026-10-14T00:00:00Z',
    isActive: true,
    expiredAt: null,
    skills: ['SQL', 'Python'],
    programmingLanguages: ['SQL'],
    sourceCategory: 'job-board',
    advertiser: 'SEEK NZ',
    advertiserType: 'Board',
    hiringEmployer: 'Xero',
    employerDisclosure: 'Disclosed',
    roleFamily: 'Data & Analytics',
    roleFamilyInferred: true,
    duplicateGroupId: null,
    duplicateCount: 0,
    alsoOnSources: [],
    isCanonicalListing: false,
    employerCanonicalId: null,
    employerCanonicalName: null,
    ...overrides,
  }
}

describe('filtered CSV export', () => {
  it('states the applied filters, period, definitions and timestamp, and reuses the screen formatting', () => {
    const csv = buildJobsCsv([job()], {
      view: 'Find jobs',
      filters: { ...defaultJobFilters, location: 'Auckland', days: '7' },
      sort: 'salary-desc',
      period: 'Last 7 days',
      generatedAt: '2026-10-15T00:00:00.000Z',
      definitions: ['Listing counts are source listings, not deduplicated vacancies.'],
    })

    expect(csv).toContain('# Generated (UTC): 2026-10-15T00:00:00.000Z')
    expect(csv).toContain('Location=Auckland')
    expect(csv).toContain('Posted within=Last 7 days')
    expect(csv).toContain('# Reporting period: Last 7 days')
    expect(csv).toContain('# Sorting: salary-desc')
    expect(csv).toContain('# Export scope: all matching records (not the current page)')
    expect(csv).toContain('# Definition: Listing counts are source listings, not deduplicated vacancies.')
    expect(csv).toContain('"Data Engineer"')
    expect(csv).toContain('"$120,000 – $140,000 per year"')
    expect(csv).toContain('"Posted"')
    expect(csv).toContain('"Xero"')
  })

  it('exports every matching row with ids, timestamps and provenance but no internal secrets', () => {
    const csv = buildJobsCsv(
      [
        job({ id: 'listing-1' }),
        job({ id: 'listing-2', duplicateGroupId: 'group-1', duplicateCount: 2, alsoOnSources: ['Trade Me Jobs'], isCanonicalListing: true }),
        job({ id: 'listing-3' }),
      ],
      {
        view: 'Find jobs',
        filters: { ...defaultJobFilters },
        sort: 'newest',
        period: 'All retained listings',
        generatedAt: '2026-11-05T00:00:00.000Z',
        definitions: [],
      },
    )

    expect(csv).toContain('# Row count: 3')
    const lines = csv.trimEnd().split('\n')
    const headerIndex = lines.findIndex((line) => line.startsWith('"Title"'))
    expect(headerIndex).toBeGreaterThan(0)
    expect(lines.slice(headerIndex + 1)).toHaveLength(3)

    const header = lines[headerIndex]
    for (const column of [
      'Listing ID',
      'Advertised company',
      'Duplicate group ID',
      'Duplicate rows removed',
      'Also on sources',
      'Canonical listing',
      'Employer canonical ID',
      'Employer canonical name',
      'First seen',
      'Last seen',
      'Source',
      'Listing URL',
    ]) {
      expect(header).toContain(`"${column}"`)
    }
    expect(lines.slice(headerIndex + 1).join('\n')).toContain('"listing-2"')
    expect(lines.slice(headerIndex + 1).join('\n')).toContain('"group-1"')
    expect(csv).not.toMatch(/password|api[_-]?key|access[_-]?token|secret/i)
  })

  it('protects spreadsheet formula prefixes and preserves the original text', () => {
    const csv = buildJobsCsv(
      [
        job({
          title: '=HYPERLINK("https://evil.example")',
          company: '+cmd|calc',
          location: '-Auckland',
          url: '@evil.example',
        }),
      ],
      {
        view: 'Find jobs',
        filters: { ...defaultJobFilters },
        sort: 'newest',
        period: 'All retained listings',
        generatedAt: '2026-11-05T00:00:00.000Z',
        definitions: [],
      },
    )

    expect(csv).toContain(`"'=HYPERLINK(""https://evil.example"")"`)
    expect(csv).toContain('"\'+cmd|calc"')
    expect(csv).toContain('"\'-Auckland"')
    expect(csv).toContain('"\'@evil.example"')
  })

  it('escapes embedded quotes and commas without dropping values', () => {
    const csv = buildJobsCsv(
      [job({ title: 'Engineer, "Platform"', company: 'Acme, Ltd' })],
      {
        view: 'Find jobs',
        filters: { ...defaultJobFilters },
        sort: 'newest',
        period: 'All retained listings',
        generatedAt: '2026-11-05T00:00:00.000Z',
        definitions: [],
      },
    )

    expect(csv).toContain('"Engineer, ""Platform"""')
    expect(csv).toContain('"Acme, Ltd"')
  })
})
