/**
 * URL state for the two audience views. Every filter, the sorting, the reporting
 * period and the pagination live in the query string so refresh, copy-link and
 * browser Back/Forward all reproduce the same view. Parsing validates every
 * value and silently falls back to the documented default for anything unknown.
 */

import { DATA_SUB_ROLE_KEYS, dataSubRoleLabel } from './dataRoles'

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
  dataSubRole: string
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
  dataSubRole: '',
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
  'dataSubRole',
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
  if (filters.dataSubRole && !DATA_SUB_ROLE_KEYS.includes(filters.dataSubRole)) filters.dataSubRole = ''

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
  dataSubRole: 'Data sub-role',
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
    if (key === 'dataSubRole') display = dataSubRoleLabel(value)
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
  // Round 22 (C1): the Data & Analytics explorer has its own URL-backed
  // filters, so changing them does not silently move the market-trend panels.
  exploreRole: string
  exploreRegion: string
  exploreSeniority: string
  exploreWorkMode: string
  exploreSkill: string
}

export const defaultTrendsFilters: TrendsFilterState = {
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
}

const TRENDS_KEYS: Array<keyof TrendsFilterState> = [
  'period',
  'region',
  'roleFamily',
  'skill',
  'seniority',
  'exploreRole',
  'exploreRegion',
  'exploreSeniority',
  'exploreWorkMode',
  'exploreSkill',
]
type TrendsCohortKey = 'region' | 'roleFamily' | 'skill' | 'seniority'
const TRENDS_FILTER_KEYS: TrendsCohortKey[] = [
  'region',
  'roleFamily',
  'skill',
  'seniority',
]
const EXPLORER_KEYS: Array<Exclude<keyof TrendsFilterState, 'period' | 'region' | 'roleFamily' | 'skill' | 'seniority'>> = [
  'exploreRole',
  'exploreRegion',
  'exploreSeniority',
  'exploreWorkMode',
  'exploreSkill',
]
const TRENDS_SAFE_FILTER_KEYS: Array<Exclude<keyof TrendsFilterState, 'period'>> = [
  'region',
  'roleFamily',
  'skill',
  'seniority',
  ...EXPLORER_KEYS,
]
const MAX_FILTER_LENGTH = 120
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f]/

/**
 * A filter value from the URL is untrusted input. Trim it, reject control
 * characters and overlong values, and fall back to "no filter" rather than
 * rendering a broken or misleading cohort.
 */
function safeFilterValue(value: string | null): string {
  if (value == null) return ''
  const trimmed = value.trim()
  if (trimmed.length === 0 || trimmed.length > MAX_FILTER_LENGTH || CONTROL_CHARACTERS.test(trimmed)) {
    return ''
  }
  return trimmed
}

export function parseTrendsFilterState(search: string): TrendsFilterState {
  const params = new URLSearchParams(search)
  const filters: TrendsFilterState = { ...defaultTrendsFilters }

  for (const key of TRENDS_SAFE_FILTER_KEYS) {
    filters[key] = safeFilterValue(params.get(key))
  }

  const period = params.get('period')
  filters.period = oneOf(TREND_PERIODS, period) ? period : defaultTrendsFilters.period
  if (filters.exploreRole && !DATA_SUB_ROLE_KEYS.includes(filters.exploreRole)) filters.exploreRole = ''
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
  if (name === 'exploreRole' && filters.exploreRole && !DATA_SUB_ROLE_KEYS.includes(filters.exploreRole)) {
    filters.exploreRole = ''
  }
  return filters
}

export type TrendsFilterChip = {
  name: TrendsCohortKey
  label: string
  display: string
}

const TRENDS_CHIP_LABELS: Record<TrendsCohortKey, string> = {
  region: 'Region',
  roleFamily: 'Role family',
  skill: 'Skill',
  seniority: 'Seniority',
}

/** Visible chips for the trends cohort; the reporting period is a separate control. */
export function trendsFilterChips(state: TrendsFilterState): TrendsFilterChip[] {
  return TRENDS_FILTER_KEYS
    .filter((key) => Boolean(state[key]))
    .map((key) => ({ name: key, label: TRENDS_CHIP_LABELS[key], display: state[key] }))
}

export type DataExplorerFilterChip = {
  name: (typeof EXPLORER_KEYS)[number]
  label: string
  display: string
}

const EXPLORER_CHIP_LABELS: Record<DataExplorerFilterChip['name'], string> = {
  exploreRole: 'Sub-role',
  exploreRegion: 'Region',
  exploreSeniority: 'Seniority',
  exploreWorkMode: 'Work mode',
  exploreSkill: 'Skill',
}

/** Visible chips for the Data & Analytics explorer only. */
export function dataExplorerFilterChips(state: TrendsFilterState): DataExplorerFilterChip[] {
  return EXPLORER_KEYS
    .filter((key) => Boolean(state[key]))
    .map((key) => ({
      name: key,
      label: EXPLORER_CHIP_LABELS[key],
      display: key === 'exploreRole' ? dataSubRoleLabel(state[key]) : state[key],
    }))
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

// ---- Round 22 (C2): two-cohort comparison URL state ----------------------

export type ComparisonState = {
  days: string
  aRole: string
  aRegion: string
  aSeniority: string
  aWorkMode: string
  aSkill: string
  bRole: string
  bRegion: string
  bSeniority: string
  bWorkMode: string
  bSkill: string
}

export const defaultComparisonState: ComparisonState = {
  days: '7',
  aRole: 'data-analyst',
  aRegion: '',
  aSeniority: '',
  aWorkMode: '',
  aSkill: '',
  bRole: 'data-engineer',
  bRegion: '',
  bSeniority: '',
  bWorkMode: '',
  bSkill: '',
}

const COMPARISON_KEYS: Array<keyof ComparisonState> = [
  'days',
  'aRole',
  'aRegion',
  'aSeniority',
  'aWorkMode',
  'aSkill',
  'bRole',
  'bRegion',
  'bSeniority',
  'bWorkMode',
  'bSkill',
]
const COMPARISON_TEXT_KEYS: Array<Exclude<keyof ComparisonState, 'days' | 'aRole' | 'bRole'>> = [
  'aRegion',
  'aSeniority',
  'aWorkMode',
  'aSkill',
  'bRegion',
  'bSeniority',
  'bWorkMode',
  'bSkill',
]

/** Parse the comparison workflow state, falling back safely on invalid input. */
export function parseComparisonState(search: string): ComparisonState {
  const params = new URLSearchParams(search)
  const state: ComparisonState = { ...defaultComparisonState }
  for (const key of COMPARISON_TEXT_KEYS) {
    state[key] = safeFilterValue(params.get(key))
  }

  const days = params.get('days')
  state.days = oneOf(TREND_PERIODS, days) ? days : defaultComparisonState.days

  for (const key of ['aRole', 'bRole'] as const) {
    const raw = safeFilterValue(params.get(key))
    if (!raw) {
      // No parameter at all means the documented default cohort. An explicit
      // "all" token preserves a cleared sub-role through reload/Back.
      state[key] = defaultComparisonState[key]
    } else if (raw === 'all') {
      state[key] = ''
    } else {
      state[key] = DATA_SUB_ROLE_KEYS.includes(raw) ? raw : defaultComparisonState[key]
    }
  }

  return state
}

export function serializeComparisonState(state: ComparisonState): string {
  const params = new URLSearchParams()
  for (const key of COMPARISON_KEYS) {
    if (key === 'aRole' || key === 'bRole') {
      const role = state[key]
      if (role === defaultComparisonState[key]) continue
      params.set(key, role || 'all')
      continue
    }

    const value = state[key]
    if (!value) continue
    if (key === 'days' && value === defaultComparisonState.days) continue
    params.set(key, value)
  }
  return params.toString()
}

export function setComparisonFilter(state: ComparisonState, name: keyof ComparisonState, value: string): ComparisonState {
  const next = { ...state, [name]: value }
  if (name === 'days' && !oneOf(TREND_PERIODS, next.days)) next.days = defaultComparisonState.days
  if ((name === 'aRole' || name === 'bRole') && next[name] && !DATA_SUB_ROLE_KEYS.includes(next[name])) {
    next[name] = defaultComparisonState[name]
  }
  return next
}
