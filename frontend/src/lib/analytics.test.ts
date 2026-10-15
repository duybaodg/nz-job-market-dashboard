import { describe, expect, it } from 'vitest'
import {
  employerActivity,
  filterJobs,
  regionDemand,
  roleFamilyDemand,
  salaryByRoleFamily,
  salaryDistribution,
  skillDemand,
  sortJobs,
} from './analytics'
import { defaultJobFilters } from './urlState'
import type { Job } from '../types/api'

function job(overrides: Partial<Job> = {}): Job {
  return {
    id: Math.random().toString(36).slice(2),
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
    sourceCategory: 'company',
    advertiser: 'Test',
    advertiserType: 'DirectEmployer',
    hiringEmployer: 'Example',
    employerDisclosure: 'Disclosed',
    roleFamily: 'Software Engineering',
    roleFamilyInferred: true,
    ...overrides,
  }
}

const NOW = new Date('2026-10-15T00:00:00Z').getTime()

describe('filterJobs', () => {
  it('defaults to active listings and separates expired ones', () => {
    const jobs = [job({ id: 'a' }), job({ id: 'b', isActive: false })]

    expect(filterJobs(jobs, defaultJobFilters, NOW).map((j) => j.id)).toEqual(['a'])
    expect(filterJobs(jobs, { ...defaultJobFilters, status: 'expired' }, NOW).map((j) => j.id)).toEqual(['b'])
    expect(filterJobs(jobs, { ...defaultJobFilters, status: 'all' }, NOW)).toHaveLength(2)
  })

  it('searches title, employer, advertiser and skills', () => {
    const jobs = [
      job({ id: 'a', title: 'Data Engineer' }),
      job({ id: 'b', hiringEmployer: 'Woolworths Group New Zealand' }),
      job({ id: 'c', skills: ['Kubernetes'] }),
    ]

    expect(filterJobs(jobs, { ...defaultJobFilters, q: 'data' }, NOW).map((j) => j.id)).toEqual(['a'])
    expect(filterJobs(jobs, { ...defaultJobFilters, q: 'woolworths' }, NOW).map((j) => j.id)).toEqual(['b'])
    expect(filterJobs(jobs, { ...defaultJobFilters, q: 'kubernetes' }, NOW).map((j) => j.id)).toEqual(['c'])
  })

  it('uses posted date but falls back to first-seen when it is absent', () => {
    const jobs = [
      job({ id: 'posted', postedDate: '2026-10-13T00:00:00Z', firstSeenAt: '2026-01-01T00:00:00Z' }),
      job({ id: 'discovered', postedDate: null, firstSeenAt: '2026-10-14T00:00:00Z' }),
      job({ id: 'stale', postedDate: '2026-01-01T00:00:00Z', firstSeenAt: '2026-10-14T00:00:00Z' }),
    ]

    expect(filterJobs(jobs, { ...defaultJobFilters, days: '2' }, NOW).map((j) => j.id)).toEqual([
      'posted',
      'discovered',
    ])
  })
})

describe('sortJobs', () => {
  it('puts unknown salary values predictably at the end', () => {
    const jobs = [
      job({ id: 'none' }),
      job({ id: 'low', salaryMin: 90000, salaryPeriod: 'Year' }),
      job({ id: 'high', salaryMin: 180000, salaryPeriod: 'Year' }),
    ]

    expect(sortJobs(jobs, 'salary-desc').map((j) => j.id)).toEqual(['high', 'low', 'none'])
    expect(sortJobs(jobs, 'salary-asc').map((j) => j.id)).toEqual(['low', 'high', 'none'])
  })

  it('never mixes hourly rates into annual salary sorting', () => {
    const jobs = [
      job({ id: 'hourly', salaryMin: 120, salaryPeriod: 'Hour' }),
      job({ id: 'annual', salaryMin: 100000, salaryPeriod: 'Year' }),
    ]

    expect(sortJobs(jobs, 'salary-desc').map((j) => j.id)).toEqual(['annual', 'hourly'])
  })

  it('sorts newest first using the posted-or-first-seen fallback', () => {
    const jobs = [
      job({ id: 'old', postedDate: '2026-01-01T00:00:00Z' }),
      job({ id: 'new', firstSeenAt: '2026-10-10T00:00:00Z' }),
    ]

    expect(sortJobs(jobs, 'newest').map((j) => j.id)).toEqual(['new', 'old'])
  })
})

describe('regionDemand', () => {
  it('counts each listing once and reports multi-location and nationwide separately', () => {
    const jobs = [
      job({ id: 'a', region: 'Auckland' }),
      job({ id: 'b', region: 'Auckland' }),
      job({ id: 'c', region: 'Wellington', location: 'Wellington - Hutt Valley; Kapiti; Masterton' }),
      job({ id: 'd', region: 'New Zealand', location: null }),
    ]

    const demand = regionDemand(jobs)

    expect(demand.bars[0]).toMatchObject({ label: 'Auckland', count: 2, share: 50 })
    expect(demand.bars.reduce((sum, bar) => sum + bar.count, 0)).toBe(jobs.length)
    expect(demand.multiLocation).toBe(1)
    expect(demand.nationwide).toBe(1)
  })
})

describe('skillDemand', () => {
  it('counts a skill at most once per listing and uses the cohort as denominator', () => {
    const jobs = [
      job({ id: 'a', skills: ['Python', 'Python', 'SQL'] }),
      job({ id: 'b', skills: ['Python'] }),
      job({ id: 'c', skills: [] }),
    ]

    const demand = skillDemand(jobs)
    const python = demand.bars.find((bar) => bar.skill === 'Python')

    expect(python).toMatchObject({ count: 2, share: 66.7 })
    expect(demand.cohort).toBe(3)
    expect(demand.extractionCoverage).toBe(66.7)
  })
})

describe('roleFamilyDemand', () => {
  it('drills into seniority within each family', () => {
    const jobs = [
      job({ roleFamily: 'Security', seniority: 'Senior' }),
      job({ roleFamily: 'Security', seniority: 'Senior' }),
      job({ roleFamily: 'Security', seniority: 'Junior' }),
    ]

    const families = roleFamilyDemand(jobs)

    expect(families[0]).toMatchObject({ label: 'Security', count: 3 })
    expect(families[0].bySeniority[0]).toMatchObject({ label: 'Senior', count: 2 })
  })
})

describe('salaryDistribution', () => {
  it('suppresses groups below five comparable observations', () => {
    const result = salaryDistribution([job({ salaryMin: 100000, salaryPeriod: 'Year' })])

    expect(result.suppressed).toBe(true)
    expect(result.median).toBeNull()
    expect(result.sampleSize).toBe(1)
  })

  it('bins eligible midpoints and reports exclusions', () => {
    const jobs = [
      job({ salaryMin: 90000, salaryMax: 110000, salaryPeriod: 'Year' }),
      job({ salaryMin: 150000, salaryPeriod: 'Year' }),
      job({ salaryMin: 85, salaryPeriod: 'Hour', employmentType: 'Contract' }),
      job({ salaryMin: 120000, salaryPeriod: 'Year', salaryCurrency: 'AUD' }),
      job({ salaryMin: 100000, salaryPeriod: 'Year', employmentType: 'Contract' }),
      job({}),
    ]

    const result = salaryDistribution(jobs)

    expect(result.sampleSize).toBe(2)
    expect(result.suppressed).toBe(true)
    expect(result.excluded.hourlyOrDaily).toBe(1)
    expect(result.excluded.nonNzd).toBe(1)
    expect(result.excluded.contractOrInternship).toBe(1)
    expect(result.excluded.missing).toBe(1)
    expect(result.bins.find((bin) => bin.label === '$100k – $120k')?.count).toBe(1)
    expect(result.bins.find((bin) => bin.label === '$140k – $160k')?.count).toBe(1)
  })
})

describe('employerActivity', () => {
  it('separates disclosed employers from agency-only and unknown listings', () => {
    const jobs = [
      job({ hiringEmployer: 'Xero', employerDisclosure: 'Disclosed' }),
      job({ hiringEmployer: 'Xero', employerDisclosure: 'Disclosed' }),
      job({ hiringEmployer: null, employerDisclosure: 'AgencyOnly' }),
      job({ hiringEmployer: null, employerDisclosure: 'Unknown' }),
    ]

    const activity = employerActivity(jobs)

    expect(activity.rows).toEqual([{ name: 'Xero', count: 2, advertiserType: 'DirectEmployer' }])
    expect(activity.disclosedListings).toBe(2)
    expect(activity.agencyOnlyListings).toBe(1)
    expect(activity.unknownListings).toBe(1)
  })
})

describe('salaryByRoleFamily', () => {
  it('suppresses role families below the sample floor', () => {
    const jobs = [
      job({ roleFamily: 'Security', salaryMin: 100000, salaryPeriod: 'Year' }),
      job({ roleFamily: 'Security', salaryMin: 120000, salaryPeriod: 'Year' }),
      job({ roleFamily: 'Security', salaryMin: 140000, salaryPeriod: 'Year' }),
      job({ roleFamily: 'Security', salaryMin: 160000, salaryPeriod: 'Year' }),
      job({ roleFamily: 'Security', salaryMin: 180000, salaryPeriod: 'Year' }),
      job({ roleFamily: 'Data & Analytics', salaryMin: 90000, salaryPeriod: 'Year' }),
    ]

    const rows = salaryByRoleFamily(jobs)

    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({ family: 'Security', sampleSize: 5, median: 140000, smallSample: true })
  })
})
