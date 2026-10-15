import { describe, expect, it } from 'vitest'
import {
  buildJobsHref,
  clearJobFilters,
  defaultJobViewState,
  jobFilterChips,
  parseJobViewState,
  parseTrendsFilterState,
  removeJobFilter,
  serializeJobViewState,
  serializeTrendsFilterState,
  setJobFilter,
  setJobPage,
  setJobSort,
  type JobViewState,
} from './urlState'

describe('job URL state', () => {
  it('round-trips a fully-populated state', () => {
    const state: JobViewState = {
      filters: {
        q: 'data engineer',
        location: 'Auckland',
        workMode: 'Hybrid',
        seniority: 'Senior',
        salaryBand: '100-130',
        roleFamily: 'Data & Analytics',
        company: 'Xero',
        source: 'SEEK NZ',
        employmentType: 'Full-time',
        days: '7',
        skill: 'Python',
        experienceBand: '2-5 years',
        programmingLanguage: 'SQL',
        status: 'all',
      },
      sort: 'salary-desc',
      page: 3,
    }

    const query = serializeJobViewState(state)
    expect(parseJobViewState(query)).toEqual(state)
    expect(serializeJobViewState(parseJobViewState(query))).toBe(query)
  })

  it('drops defaults so a pristine view has no query string', () => {
    expect(serializeJobViewState(defaultJobViewState)).toBe('')
    expect(parseJobViewState('')).toEqual(defaultJobViewState)
  })

  it('ignores invalid values gracefully', () => {
    const parsed = parseJobViewState('?status=bogus&sort=sideways&page=-4&days=99')

    expect(parsed.filters.status).toBe('active')
    expect(parsed.sort).toBe('newest')
    expect(parsed.page).toBe(1)
    expect(parsed.filters.days).toBe('')
  })

  it('resets pagination when a filter or the sorting changes', () => {
    const state = setJobPage(defaultJobViewState, 5)
    expect(state.page).toBe(5)

    expect(setJobFilter(state, 'location', 'Wellington').page).toBe(1)
    expect(setJobSort(state, 'closing').page).toBe(1)
    expect(removeJobFilter(state, 'location').page).toBe(1)
    expect(clearJobFilters(state).page).toBe(1)
  })

  it('lists visible chips and hides the default status', () => {
    const chips = jobFilterChips({
      ...defaultJobViewState.filters,
      location: 'Auckland',
      days: '7',
      salaryBand: 'gte130',
    })

    expect(chips.map((chip) => chip.name)).toEqual(['location', 'salaryBand', 'days'])
    expect(chips.find((chip) => chip.name === 'salaryBand')?.display).toBe('$130k+')
    expect(chips.find((chip) => chip.name === 'days')?.display).toBe('Last 7 days')

    const withStatus = jobFilterChips({ ...defaultJobViewState.filters, status: 'expired' })
    expect(withStatus.map((chip) => chip.name)).toEqual(['status'])
  })

  it('builds an equivalent Find-jobs link for a chart click', () => {
    expect(buildJobsHref({ roleFamily: 'Security' })).toBe('/jobs?roleFamily=Security')
    expect(buildJobsHref({ location: 'Auckland', workMode: 'Remote' })).toBe(
      '/jobs?location=Auckland&workMode=Remote',
    )
    expect(buildJobsHref({})).toBe('/jobs')
  })
})

describe('trends URL state', () => {
  it('round-trips and defaults the period', () => {
    const state = { period: '30', region: 'Canterbury', roleFamily: 'Security', skill: 'AWS', seniority: 'Lead' }
    const query = serializeTrendsFilterState(state)

    expect(parseTrendsFilterState(query)).toEqual(state)
    expect(parseTrendsFilterState('')).toEqual({ period: '7', region: '', roleFamily: '', skill: '', seniority: '' })
    expect(parseTrendsFilterState('?period=999').period).toBe('7')
  })
})
