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
    expect(csv).toContain('# Definition: Listing counts are source listings, not deduplicated vacancies.')
    expect(csv).toContain('"Data Engineer"')
    expect(csv).toContain('"$120,000 – $140,000 per year"')
    expect(csv).toContain('"Posted"')
    expect(csv).toContain('"Xero"')
  })
})
