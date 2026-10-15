import { describe, expect, it } from 'vitest'
import { formatAdvertisedSalary, isComparableAnnual, salaryMidpoint } from './salary'
import type { Job } from '../types/api'

function job(overrides: Partial<Job> = {}): Job {
  return {
    id: '1',
    source: 'Test',
    title: 'Software Engineer',
    company: 'Example',
    location: 'Auckland',
    region: 'Auckland',
    salaryMin: null,
    salaryMax: null,
    salaryText: null,
    salaryCurrency: 'NZD',
    salaryPeriod: 'Unknown',
    salaryQuestionable: false,
    salaryQuestionableReason: null,
    employmentType: 'Full-time',
    seniority: 'Unknown',
    workMode: 'Unknown',
    experienceBand: 'Unknown',
    industry: null,
    descriptionSummary: null,
    url: 'https://example.com/job',
    postedDate: null,
    closingDate: null,
    firstSeenAt: '2026-10-01T00:00:00Z',
    lastSeenAt: '2026-10-02T00:00:00Z',
    isActive: true,
    expiredAt: null,
    skills: [],
    programmingLanguages: [],
    ...overrides,
  }
}

describe('salary helpers', () => {
  it('computes a midpoint from a range and a single amount', () => {
    expect(salaryMidpoint(job({ salaryMin: 120000, salaryMax: 140000, salaryPeriod: 'Year' }))).toBe(130000)
    expect(salaryMidpoint(job({ salaryMin: 150000, salaryPeriod: 'Year' }))).toBe(150000)
    expect(salaryMidpoint(job())).toBeNull()
  })

  it('only treats permanent annual NZD values as comparable', () => {
    expect(isComparableAnnual(job({ salaryMin: 120000, salaryPeriod: 'Year' }))).toBe(true)
    expect(isComparableAnnual(job({ salaryMin: 85, salaryPeriod: 'Hour', employmentType: 'Contract' }))).toBe(false)
    expect(isComparableAnnual(job({ salaryMin: 120000, salaryPeriod: 'Year', employmentType: 'Contract' }))).toBe(false)
    expect(isComparableAnnual(job({ salaryMin: 120000, salaryPeriod: 'Year', salaryCurrency: 'AUD' }))).toBe(false)
    expect(isComparableAnnual(job({ salaryPeriod: 'Year' }))).toBe(false)
  })

  it('never annualises an hourly rate when formatting', () => {
    expect(formatAdvertisedSalary(job({ salaryMin: 85, salaryPeriod: 'Hour', employmentType: 'Contract' }))).toBe(
      '$85 per hour',
    )
  })

  it('labels a missing salary instead of showing a figure', () => {
    expect(formatAdvertisedSalary(job())).toBe('Salary not advertised')
  })

  it('formats a range with its pay period', () => {
    expect(
      formatAdvertisedSalary(job({ salaryMin: 120000, salaryMax: 140000, salaryPeriod: 'Year' })),
    ).toBe('$120,000 – $140,000 per year')
  })
})
