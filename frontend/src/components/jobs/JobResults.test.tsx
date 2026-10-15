// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { JobResults } from './JobResults'
import { JobCard } from './JobCard'
import type { Job } from '../../types/api'

function job(overrides: Partial<Job> = {}): Job {
  return {
    id: '1',
    source: 'SEEK NZ',
    title: 'Senior Data Engineer',
    company: 'Woolworths Group New Zealand',
    location: 'Auckland',
    region: 'Auckland',
    salaryMin: 130000,
    salaryMax: 150000,
    salaryText: '$130,000 - $150,000',
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
    closingDate: '2026-11-01T00:00:00Z',
    firstSeenAt: '2026-10-11T00:00:00Z',
    lastSeenAt: '2026-10-14T00:00:00Z',
    isActive: true,
    expiredAt: null,
    skills: ['SQL', 'Python', 'AWS', 'Airflow', 'dbt', 'Snowflake', 'Terraform'],
    programmingLanguages: ['Python', 'SQL'],
    sourceCategory: 'job-board',
    advertiser: 'SEEK NZ',
    advertiserType: 'Board',
    hiringEmployer: 'Woolworths Group New Zealand',
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

afterEach(cleanup)

describe('JobCard essential information', () => {
  it('keeps salary, work mode and posted date visible without a hover', () => {
    render(<JobCard job={job()} />)

    expect(screen.getByText('$130,000 – $150,000 per year')).toBeTruthy()
    expect(screen.getByText('Hybrid')).toBeTruthy()
    expect(screen.getByText('Posted')).toBeTruthy()
    expect(screen.getByText(/Woolworths Group New Zealand/)).toBeTruthy()
    expect(screen.getByText(/Advertised via SEEK NZ/)).toBeTruthy()
    expect(screen.getByText('+2 more skills')).toBeTruthy()
  })

  it('labels a first-seen date and an absent salary honestly', () => {
    render(
      <JobCard
        job={job({ postedDate: null, salaryMin: null, salaryMax: null, employerDisclosure: 'AgencyOnly', hiringEmployer: null, advertiser: 'Beyond Recruitment', advertiserType: 'Agency' })}
      />,
    )

    expect(screen.getByText('Salary not advertised')).toBeTruthy()
    expect(screen.getByText('First seen')).toBeTruthy()
    expect(screen.getByText('Hiring employer not disclosed')).toBeTruthy()
    expect(screen.getByText(/Advertised by Beyond Recruitment \(recruiter\)/)).toBeTruthy()
  })

  it('badges a listing that is also advertised on another source', () => {
    render(
      <JobCard
        job={job({
          duplicateGroupId: 'group-1',
          duplicateCount: 2,
          alsoOnSources: ['Trade Me Jobs', 'LinkedIn'],
          isCanonicalListing: true,
          employerCanonicalId: 'employer-1',
          employerCanonicalName: 'Woolworths Group New Zealand',
        })}
      />,
    )

    expect(screen.getByText('Also on Trade Me Jobs, LinkedIn')).toBeTruthy()
  })
})

describe('JobResults states', () => {
  it('distinguishes an empty result from a populated one', () => {
    const { rerender } = render(
      <JobResults jobs={[]} total={0} page={1} pageSize={10} sort="newest" onSortChange={vi.fn()} onPageChange={vi.fn()} />,
    )

    expect(screen.getByText(/No roles match the current filters/)).toBeTruthy()

    rerender(
      <JobResults jobs={[job()]} total={1} page={1} pageSize={10} sort="newest" onSortChange={vi.fn()} onPageChange={vi.fn()} />,
    )

    expect(screen.queryByText(/No roles match the current filters/)).toBeNull()
    expect(screen.getByText('Senior Data Engineer')).toBeTruthy()
    expect(screen.getByRole('navigation', { name: 'Job results pages' })).toBeTruthy()
  })
})
