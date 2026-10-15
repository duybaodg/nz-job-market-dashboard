import { isComparableAnnual, salaryMidpoint } from './salary'
import type { Job } from '../types/api'
import type { JobFilterState, JobSort } from './urlState'

/**
 * Shared, deterministic cohort calculations. Both audience views and the CSV
 * export call these functions so a number can never mean two different things.
 */

export function postedOrFirstSeen(job: Job): string {
  return job.postedDate ?? job.firstSeenAt
}

function time(value: string | null): number {
  return value ? new Date(value).getTime() : 0
}

export function inSalaryBand(job: Job, band: string): boolean {
  const midpoint = salaryMidpoint(job)

  if (band === 'missing') return midpoint == null
  if (!isComparableAnnual(job) || midpoint == null) return false
  if (band === 'lt70') return midpoint < 70000
  if (band === '70-100') return midpoint >= 70000 && midpoint < 100000
  if (band === '100-130') return midpoint >= 100000 && midpoint < 130000
  if (band === 'gte130') return midpoint >= 130000
  return true
}

export function filterJobs(jobs: Job[], filters: JobFilterState, now: number = Date.now()): Job[] {
  const search = filters.q.trim().toLowerCase()
  const earliest = filters.days ? now - Number(filters.days) * 24 * 60 * 60 * 1000 : null

  return jobs.filter((job) => {
    const posted = time(postedOrFirstSeen(job))

    const matchesSearch =
      !search ||
      job.title.toLowerCase().includes(search) ||
      job.company?.toLowerCase().includes(search) ||
      job.hiringEmployer?.toLowerCase().includes(search) ||
      job.advertiser.toLowerCase().includes(search) ||
      job.descriptionSummary?.toLowerCase().includes(search) ||
      job.skills.some((skill) => skill.toLowerCase().includes(search))

    return (
      (filters.status === 'all' || job.isActive === (filters.status === 'active')) &&
      (!earliest || posted >= earliest) &&
      (!filters.location || job.location === filters.location || job.region === filters.location) &&
      (!filters.skill || job.skills.includes(filters.skill)) &&
      (!filters.roleFamily || job.roleFamily === filters.roleFamily) &&
      (!filters.company || job.company === filters.company) &&
      (!filters.source || job.source === filters.source) &&
      (!filters.workMode || job.workMode === filters.workMode) &&
      (!filters.seniority || job.seniority === filters.seniority) &&
      (!filters.employmentType || job.employmentType === filters.employmentType) &&
      (!filters.experienceBand || job.experienceBand === filters.experienceBand) &&
      (!filters.programmingLanguage || job.programmingLanguages.includes(filters.programmingLanguage)) &&
      (!filters.salaryBand || inSalaryBand(job, filters.salaryBand)) &&
      matchesSearch
    )
  })
}

/** Sorts a filtered cohort. Unknown salary values are always placed last. */
export function sortJobs(jobs: Job[], sort: JobSort): Job[] {
  const copy = [...jobs]

  switch (sort) {
    case 'salary-desc':
      return copy.sort((a, b) => compareSalary(a, b, 'desc'))
    case 'salary-asc':
      return copy.sort((a, b) => compareSalary(a, b, 'asc'))
    case 'closing':
      return copy.sort((a, b) => {
        const aDate = a.closingDate ? time(a.closingDate) : Number.POSITIVE_INFINITY
        const bDate = b.closingDate ? time(b.closingDate) : Number.POSITIVE_INFINITY
        return aDate - bDate || time(postedOrFirstSeen(b)) - time(postedOrFirstSeen(a))
      })
    default:
      return copy.sort((a, b) => time(postedOrFirstSeen(b)) - time(postedOrFirstSeen(a)))
  }
}

function compareSalary(a: Job, b: Job, direction: 'asc' | 'desc'): number {
  const aValue = isComparableAnnual(a) ? salaryMidpoint(a) : null
  const bValue = isComparableAnnual(b) ? salaryMidpoint(b) : null

  if (aValue == null && bValue == null) {
    return time(postedOrFirstSeen(b)) - time(postedOrFirstSeen(a))
  }

  if (aValue == null) return 1
  if (bValue == null) return -1

  const delta = direction === 'desc' ? bValue - aValue : aValue - bValue
  return delta || time(postedOrFirstSeen(b)) - time(postedOrFirstSeen(a))
}

export type DemandBar = {
  label: string
  count: number
  share: number
}

const NATIONWIDE = 'New Zealand'

export function isNationwide(job: Job): boolean {
  return job.region === NATIONWIDE || /nationwide|new zealand/i.test(job.location ?? '')
}

/** A listing whose source location string spans more than one place. */
export function isMultiLocation(job: Job): boolean {
  return /;/.test(job.location ?? '')
}

function toBars(counts: Map<string, number>, cohort: number): DemandBar[] {
  return [...counts.entries()]
    .map(([label, count]) => ({ label, count, share: cohort === 0 ? 0 : Math.round((count / cohort) * 1000) / 10 }))
    .sort((a, b) => b.count - a.count || a.label.localeCompare(b.label))
}

export function countBy<T>(items: T[], getKey: (item: T) => string | null | undefined): Map<string, number> {
  const counts = new Map<string, number>()

  for (const item of items) {
    const key = getKey(item)
    if (key) counts.set(key, (counts.get(key) ?? 0) + 1)
  }

  return counts
}

/**
 * Regional demand. Every listing is counted once under its primary normalised
 * region, so shares reconcile and never overlap; multi-location and nationwide
 * listings are reported separately instead of being double-counted.
 */
export function regionDemand(jobs: Job[]): { bars: DemandBar[]; multiLocation: number; nationwide: number } {
  const counts = countBy(jobs, (job) => job.region)
  return {
    bars: toBars(counts, jobs.length),
    multiLocation: jobs.filter(isMultiLocation).length,
    nationwide: jobs.filter(isNationwide).length,
  }
}

export type RoleFamilyDemand = DemandBar & {
  bySeniority: DemandBar[]
}

export function roleFamilyDemand(jobs: Job[]): RoleFamilyDemand[] {
  const families = new Map<string, Job[]>()

  for (const job of jobs) {
    const family = job.roleFamily || 'Other / Unknown'
    families.set(family, [...(families.get(family) ?? []), job])
  }

  return [...families.entries()]
    .map(([label, familyJobs]) => ({
      label,
      count: familyJobs.length,
      share: jobs.length === 0 ? 0 : Math.round((familyJobs.length / jobs.length) * 1000) / 10,
      bySeniority: toBars(countBy(familyJobs, (job) => job.seniority || 'Unknown'), familyJobs.length),
    }))
    .sort((a, b) => b.count - a.count || a.label.localeCompare(b.label))
}

export type SkillDemand = DemandBar & {
  skill: string
}

/**
 * Skill demand. Each skill is counted at most once per listing and the
 * percentage denominator is the displayed listing cohort, so percentages can
 * legitimately sum above 100%.
 */
export function skillDemand(jobs: Job[], limit = 15): { bars: SkillDemand[]; cohort: number; extractionCoverage: number } {
  const counts = new Map<string, number>()
  let listingsWithSkills = 0

  for (const job of jobs) {
    const unique = [...new Set(job.skills)]
    if (unique.length > 0) listingsWithSkills++
    for (const skill of unique) counts.set(skill, (counts.get(skill) ?? 0) + 1)
  }

  const bars = toBars(counts, jobs.length)
    .slice(0, limit)
    .map((bar) => ({ ...bar, skill: bar.label }))

  return {
    bars,
    cohort: jobs.length,
    extractionCoverage: jobs.length === 0 ? 0 : Math.round((listingsWithSkills / jobs.length) * 1000) / 10,
  }
}

export const MINIMUM_SALARY_SAMPLE = 5
export const SMALL_SALARY_SAMPLE = 9

export type SalaryBin = {
  label: string
  count: number
}

export type SalaryDistribution = {
  suppressed: boolean
  smallSample: boolean
  sampleSize: number
  cohortCount: number
  median: number | null
  bins: SalaryBin[]
  excluded: {
    hourlyOrDaily: number
    nonNzd: number
    contractOrInternship: number
    missing: number
  }
}

const SALARY_BIN_DEFS: Array<{ label: string; min: number; max: number | null }> = [
  { label: 'Under $80k', min: 0, max: 80000 },
  { label: '$80k – $100k', min: 80000, max: 100000 },
  { label: '$100k – $120k', min: 100000, max: 120000 },
  { label: '$120k – $140k', min: 120000, max: 140000 },
  { label: '$140k – $160k', min: 140000, max: 160000 },
  { label: '$160k+', min: 160000, max: null },
]

function median(values: number[]): number | null {
  if (values.length === 0) return null
  const sorted = [...values].sort((a, b) => a - b)
  const middle = Math.floor(sorted.length / 2)
  return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2
}

/** Salary distribution with the same guardrails as the backend salary metric. */
export function salaryDistribution(jobs: Job[]): SalaryDistribution {
  const values: number[] = []
  const excluded = { hourlyOrDaily: 0, nonNzd: 0, contractOrInternship: 0, missing: 0 }

  for (const job of jobs) {
    const midpoint = salaryMidpoint(job)
    if (midpoint == null) {
      excluded.missing++
      continue
    }

    if (job.salaryPeriod !== 'Year') {
      excluded.hourlyOrDaily++
      continue
    }

    if (job.salaryCurrency !== 'NZD') {
      excluded.nonNzd++
      continue
    }

    if (job.employmentType === 'Contract' || job.employmentType === 'Internship') {
      excluded.contractOrInternship++
      continue
    }

    values.push(midpoint)
  }

  const suppressed = values.length < MINIMUM_SALARY_SAMPLE

  return {
    suppressed,
    smallSample: values.length > 0 && values.length <= SMALL_SALARY_SAMPLE,
    sampleSize: values.length,
    cohortCount: jobs.length,
    median: suppressed ? null : median(values),
    bins: SALARY_BIN_DEFS.map((band) => ({
      label: band.label,
      count: values.filter((value) => value >= band.min && (band.max == null || value < band.max)).length,
    })),
    excluded,
  }
}

export type RoleSalaryRow = {
  family: string
  sampleSize: number
  median: number
  smallSample: boolean
}

/** Median annual NZD midpoint by role family, suppressed below the sample floor. */
export function salaryByRoleFamily(jobs: Job[], minimum = MINIMUM_SALARY_SAMPLE): RoleSalaryRow[] {
  const byFamily = new Map<string, number[]>()

  for (const job of jobs) {
    if (!isComparableAnnual(job)) continue
    const midpoint = salaryMidpoint(job)
    if (midpoint == null) continue
    const family = job.roleFamily || 'Other / Unknown'
    byFamily.set(family, [...(byFamily.get(family) ?? []), midpoint])
  }

  return [...byFamily.entries()]
    .filter(([, values]) => values.length >= minimum)
    .map(([family, values]) => ({
      family,
      sampleSize: values.length,
      median: median(values)!,
      smallSample: values.length <= SMALL_SALARY_SAMPLE,
    }))
    .sort((a, b) => b.median - a.median || a.family.localeCompare(b.family))
}

export type EmployerActivityRow = {
  name: string
  count: number
  advertiserType: string
}
export type EmployerActivity = {
  rows: EmployerActivityRow[]
  disclosedListings: number
  agencyOnlyListings: number
  unknownListings: number
  distinctEmployers: number
}

/** Ranked employers, keeping agency-only and unknown-employer listings separate. */
export function employerActivity(jobs: Job[], limit = 10): EmployerActivity {
  const employers = new Map<string, EmployerActivityRow>()
  let agencyOnly = 0
  let unknown = 0
  let disclosed = 0

  for (const job of jobs) {
    if (job.employerDisclosure === 'Disclosed' && job.hiringEmployer) {
      disclosed++
      const displayName = job.employerCanonicalName ?? job.hiringEmployer.trim()
      const key = job.employerCanonicalId ?? `name:${job.hiringEmployer.trim().toLowerCase()}`
      const existing = employers.get(key)
      if (existing) {
        existing.count++
      } else {
        employers.set(key, { name: displayName, count: 1, advertiserType: job.advertiserType })
      }
      continue
    }

    if (job.employerDisclosure === 'AgencyOnly') agencyOnly++
    else unknown++
  }

  const rows = [...employers.values()].sort((a, b) => b.count - a.count || a.name.localeCompare(b.name))

  return {
    rows: rows.slice(0, limit),
    disclosedListings: disclosed,
    agencyOnlyListings: agencyOnly,
    unknownListings: unknown,
    distinctEmployers: employers.size,
  }
}
