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
  trendsFilterChips,
  dataExplorerFilterChips,
  defaultTrendsFilters,
  defaultComparisonState,
  parseComparisonState,
  serializeComparisonState,
  setComparisonFilter,
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
        dataSubRole: 'data-engineer',
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
    const parsed = parseJobViewState('?status=bogus&sort=sideways&page=-4&days=99&dataSubRole=not-a-role')

    expect(parsed.filters.status).toBe('active')
    expect(parsed.sort).toBe('newest')
    expect(parsed.page).toBe(1)
    expect(parsed.filters.days).toBe('')
    expect(parsed.filters.dataSubRole).toBe('')
  })

  it('round-trips a versioned data sub-role filter and labels its chip', () => {
    const state = setJobFilter(defaultJobViewState, 'dataSubRole', 'data-engineer')
    expect(parseJobViewState(serializeJobViewState(state)).filters.dataSubRole).toBe('data-engineer')
    expect(jobFilterChips(state.filters)).toContainEqual({
      name: 'dataSubRole',
      label: 'Data sub-role',
      display: 'Data Engineer',
    })
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
    const state = {
      period: '30',
      region: 'Canterbury',
      roleFamily: 'Security',
      skill: 'AWS',
      seniority: 'Lead',
      exploreRole: 'data-engineer',
      exploreRegion: 'Auckland',
      exploreSeniority: 'Senior',
      exploreWorkMode: 'Hybrid',
      exploreSkill: 'SQL',
    }
    const query = serializeTrendsFilterState(state)

    expect(parseTrendsFilterState(query)).toEqual(state)
    expect(parseTrendsFilterState('')).toEqual({
      period: '7',
      region: '',
      roleFamily: '',
      skill: '',
      seniority: '',
      exploreRole: '',
      exploreRegion: '',
      exploreSeniority: '',
      exploreWorkMode: '',
      exploreSkill: '',
    })
    expect(parseTrendsFilterState('?period=999').period).toBe('7')
  })

  it('trims values and falls back safely on invalid trend filter input', () => {
    expect(parseTrendsFilterState('?region=%20Auckland%20&roleFamily=Security').region).toBe('Auckland')

    const longValue = 'x'.repeat(121)
    const parsed = parseTrendsFilterState(`?region=${longValue}&roleFamily=%00bad&skill=%20%20&seniority=Lead`)
    expect(parsed.region).toBe('')
    expect(parsed.roleFamily).toBe('')
    expect(parsed.skill).toBe('')
    expect(parsed.seniority).toBe('Lead')
  })

  it('round-trips the explorer role and filters without moving the market cohort', () => {
    const state = {
      ...defaultTrendsFilters,
      exploreRole: 'data-analyst',
      exploreRegion: 'Wellington',
      exploreSeniority: 'Senior',
      exploreWorkMode: 'Hybrid',
      exploreSkill: 'SQL',
    }
    const parsed = parseTrendsFilterState(serializeTrendsFilterState(state))
    expect(parsed).toEqual(state)
    expect(parseTrendsFilterState('?exploreRole=not-a-role').exploreRole).toBe('')
    expect(dataExplorerFilterChips(parsed).map((chip) => [chip.name, chip.display])).toEqual([
      ['exploreRole', 'Data Analyst'],
      ['exploreRegion', 'Wellington'],
      ['exploreSeniority', 'Senior'],
      ['exploreWorkMode', 'Hybrid'],
      ['exploreSkill', 'SQL'],
    ])
  })

  it('lists visible trend filter chips without the reporting period', () => {
    const chips = trendsFilterChips({
      period: '15',
      region: 'Auckland',
      roleFamily: 'Data & Analytics',
      skill: '',
      seniority: 'Senior',
      exploreRole: '',
      exploreRegion: '',
      exploreSeniority: '',
      exploreWorkMode: '',
      exploreSkill: '',
    })

    expect(chips.map((chip) => chip.name)).toEqual(['region', 'roleFamily', 'seniority'])
    expect(chips.find((chip) => chip.name === 'roleFamily')?.display).toBe('Data & Analytics')
  })
})

describe('comparison URL state', () => {
  it('round-trips both cohort definitions and falls back safely', () => {
    const state = {
      ...defaultComparisonState,
      days: '15',
      aRole: 'data-analyst',
      aRegion: 'Auckland',
      aSeniority: 'Senior',
      aWorkMode: 'Hybrid',
      aSkill: 'SQL',
      bRole: 'data-engineer',
      bRegion: 'Wellington',
    }
    expect(parseComparisonState(serializeComparisonState(state))).toEqual(state)
    expect(serializeComparisonState(defaultComparisonState)).toBe('')
    expect(parseComparisonState('?days=99&aRole=nope&bRole=%00bad').days).toBe('7')
    expect(parseComparisonState('?days=99&aRole=nope&bRole=%00bad').aRole).toBe('data-analyst')
    expect(parseComparisonState('?days=99&aRole=nope&bRole=%00bad').bRole).toBe('data-engineer')
    const cleared = { ...defaultComparisonState, aRole: '', bRole: '' }
    expect(parseComparisonState(serializeComparisonState(cleared))).toEqual(cleared)
    expect(serializeComparisonState(cleared)).toContain('aRole=all')
  })

  it('resets an invalid role selection', () => {
    expect(setComparisonFilter(defaultComparisonState, 'aRole', 'unknown').aRole).toBe('data-analyst')
    expect(setComparisonFilter(defaultComparisonState, 'aRole', 'data-scientist').aRole).toBe('data-scientist')
  })
})
