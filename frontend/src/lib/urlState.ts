/**
 * URL state for the two audience views. Every filter, the sorting, the reporting
 * period and the pagination live in the query string so refresh, copy-link and
 * browser Back/Forward all reproduce the same view. Parsing validates every
 * value and silently falls back to the documented default for anything unknown.
 */

export const JOB_SORTS = ['newest', 'salary-desc', 'salary-asc', 'closing'] as const
export type JobSort = (typeof JOB_SORTS)[number]

export const JOB_STATUSES = ['active', 'expired', 'all'] as const
export type JobStatus = (typeof JOB_STATUSES)[number]

export const JOB_DAYS = ['2', '7', '15'] as const
export const SALARY_BANDS = ['lt70', '70-100', '100-130', 'gte130', 'missing'] as const
export const TREND_PERIODS = ['7', '15', '30'] as const

export type JobFilterState = {
  q: string
  location: string
  workMode: string
  seniority: string
  salaryBand: string
  roleFamily: string
  company: string
  source: string
  employmentType: string
  days: string
  skill: string
  experienceBand: string
  programmingLanguage: string
  status: JobStatus
}

export type JobViewState = {
  filters: JobFilterState
  sort: JobSort
  page: number
}

export const defaultJobFilters: JobFilterState = {
  q: '',
  location: '',
  workMode: '',
  seniority: '',
  salaryBand: '',
  roleFamily: '',
  company: '',
  source: '',
  employmentType: '',
  days: '',
  skill: '',
  experienceBand: '',
  programmingLanguage: '',
  status: 'active',
}

export const defaultJobViewState: JobViewState = {
  filters: defaultJobFilters,
  sort: 'newest',
  page: 1,
}

const JOB_FILTER_KEYS: Array<keyof JobFilterState> = [
  'q',
  'location',
  'workMode',
  'seniority',
  'salaryBand',
  'roleFamily',
  'company',
  'source',
  'employmentType',
  'days',
  'skill',
  'experienceBand',
  'programmingLanguage',
  'status',
]

function oneOf<T extends string>(values: readonly T[], value: string | null): value is T {
  return value != null && (values as readonly string[]).includes(value)
}

function positiveInt(value: string | null, fallback: number): number {
  if (value == null) return fallback

  const parsed = Number(value)
  return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback
}

export function parseJobViewState(search: string): JobViewState {
  const params = new URLSearchParams(search)
  const filters: JobFilterState = { ...defaultJobFilters }

  for (const key of JOB_FILTER_KEYS) {
    const value = params.get(key)
    if (value != null) (filters as Record<string, string>)[key] = value
  }

  if (!oneOf(JOB_STATUSES, filters.status)) filters.status = defaultJobFilters.status
  if (filters.days && !oneOf(JOB_DAYS, filters.days)) filters.days = ''
  if (filters.salaryBand && !oneOf(SALARY_BANDS, filters.salaryBand)) filters.salaryBand = ''

  const sortValue = params.get('sort')
  const sort: JobSort = oneOf(JOB_SORTS, sortValue) ? sortValue : defaultJobViewState.sort

  return { filters, sort, page: positiveInt(params.get('page'), 1) }
}

export function serializeJobViewState(state: JobViewState): string {
  const params = new URLSearchParams()

  for (const key of JOB_FILTER_KEYS) {
    const value = state.filters[key]
    if (!value) continue
    if (key === 'status' && value === defaultJobFilters.status) continue
    params.set(key, value)
  }

  if (state.sort !== defaultJobViewState.sort) params.set('sort', state.sort)
  if (state.page > 1) params.set('page', String(state.page))

  return params.toString()
}

/** Updating any filter always resets pagination, per the brief. */
export function setJobFilter(state: JobViewState, name: keyof JobFilterState, value: string): JobViewState {
  const filters = { ...state.filters, [name]: value } as JobFilterState
  if (name === 'status' && !oneOf(JOB_STATUSES, value)) filters.status = defaultJobFilters.status
  return { ...state, filters, page: 1 }
}

export function removeJobFilter(state: JobViewState, name: keyof JobFilterState): JobViewState {
  return setJobFilter(state, name, name === 'status' ? defaultJobFilters.status : '')
}

export function clearJobFilters(state: JobViewState): JobViewState {
  return { filters: { ...defaultJobFilters }, sort: state.sort, page: 1 }
}

export function setJobSort(state: JobViewState, sort: JobSort): JobViewState {
  return { ...state, sort, page: 1 }
}

export function setJobPage(state: JobViewState, page: number): JobViewState {
  return { ...state, page: Math.max(1, Math.trunc(page)) }
}

export type JobFilterChip = {
  name: keyof JobFilterState
  label: string
  display: string
}

const CHIP_LABELS: Partial<Record<keyof JobFilterState, string>> = {
  q: 'Search',
  location: 'Location',
  workMode: 'Work mode',
  seniority: 'Seniority',
  salaryBand: 'Salary',
  roleFamily: 'Role family',
  company: 'Company',
  source: 'Source',
  employmentType: 'Employment',
  days: 'Posted within',
  skill: 'Skill',
  experienceBand: 'Experience',
  programmingLanguage: 'Language',
  status: 'Status',
}

const SALARY_BAND_LABELS: Record<string, string> = {
  lt70: 'Under $70k',
  '70-100': '$70k – $100k',
  '100-130': '$100k – $130k',
  gte130: '$130k+',
  missing: 'Salary not advertised',
}

const STATUS_LABELS: Record<string, string> = {
  active: 'Active',
  expired: 'Expired',
  all: 'All statuses',
}

/** The visible filter chips, excluding defaults that are not really "applied". */
export function jobFilterChips(state: JobFilterState): JobFilterChip[] {
  const chips: JobFilterChip[] = []

  for (const key of JOB_FILTER_KEYS) {
    const value = state[key]
    if (!value) continue
    if (key === 'status' && value === defaultJobFilters.status) continue

    let display = value
    if (key === 'days') display = `Last ${value} days`
    if (key === 'salaryBand') display = SALARY_BAND_LABELS[value] ?? value
    if (key === 'status') display = STATUS_LABELS[value] ?? value

    chips.push({ name: key, label: CHIP_LABELS[key] ?? key, display })
  }

  return chips
}

export type TrendsFilterState = {
  period: string
  region: string
  roleFamily: string
  skill: string
  seniority: string
}

export const defaultTrendsFilters: TrendsFilterState = {
  period: '7',
  region: '',
  roleFamily: '',
  skill: '',
  seniority: '',
}

const TRENDS_KEYS: Array<keyof TrendsFilterState> = ['period', 'region', 'roleFamily', 'skill', 'seniority']

export function parseTrendsFilterState(search: string): TrendsFilterState {
  const params = new URLSearchParams(search)
  const filters: TrendsFilterState = { ...defaultTrendsFilters }

  for (const key of TRENDS_KEYS) {
    const value = params.get(key)
    if (value != null) filters[key] = value
  }

  if (!oneOf(TREND_PERIODS, filters.period)) filters.period = defaultTrendsFilters.period
  return filters
}

export function serializeTrendsFilterState(state: TrendsFilterState): string {
  const params = new URLSearchParams()

  for (const key of TRENDS_KEYS) {
    const value = state[key]
    if (!value) continue
    if (key === 'period' && value === defaultTrendsFilters.period) continue
    params.set(key, value)
  }

  return params.toString()
}

export function setTrendsFilter(state: TrendsFilterState, name: keyof TrendsFilterState, value: string): TrendsFilterState {
  const filters = { ...state, [name]: value }
  if (name === 'period' && !oneOf(TREND_PERIODS, filters.period)) filters.period = defaultTrendsFilters.period
  return filters
}

/** Builds a Find-jobs URL from the trends view so a chart click applies a visible filter. */
export function buildJobsHref(overrides: Partial<JobFilterState>): string {
  const state: JobViewState = {
    filters: { ...defaultJobFilters, ...overrides },
    sort: 'newest',
    page: 1,
  }
  const query = serializeJobViewState(state)
  return query ? `/jobs?${query}` : '/jobs'
}
