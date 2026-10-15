export type SalaryPeriod = 'Year' | 'Hour' | 'Day' | 'Unknown'

export type Job = {
  id: string
  source: string
  title: string
  company: string | null
  location: string | null
  region: string | null
  salaryMin: number | null
  salaryMax: number | null
  salaryText: string | null
  salaryCurrency: string
  salaryPeriod: SalaryPeriod
  salaryQuestionable: boolean
  salaryQuestionableReason: string | null
  employmentType: string
  seniority: string
  workMode: string
  experienceBand: string
  industry: string | null
  descriptionSummary: string | null
  url: string
  postedDate: string | null
  closingDate: string | null
  firstSeenAt: string
  lastSeenAt: string
  isActive: boolean
  expiredAt: string | null
  skills: string[]
  programmingLanguages: string[]
}

export type MetricInterval = {
  startUtc: string
  endUtc: string
  startLocalDate: string
  endLocalDateExclusive: string
  days: number
  label: string
  isPartial: boolean
}

export type Change = {
  current: number
  previous: number | null
  absoluteChange: number | null
  percentChange: number | null
  comparisonAvailable: boolean
  noPriorBaseline: boolean
  note: string | null
}

export type SalarySummary = {
  medianAnnualNzd: number | null
  eligibleCount: number
  cohortCount: number
  coveragePercent: number
  questionableCount: number
  suppressed: boolean
  smallSample: boolean
  deduplicated: boolean
  exclusions: Record<string, number>
  currency: string
  payPeriod: string
  basis: string
}

export type EmployerSummary = {
  identifiableEmployers: number
  listingsWithEmployer: number
  listingsWithoutEmployer: number
  coveragePercent: number
}

export type DataFreshnessState = 'Fresh' | 'Stale' | 'Partial' | 'Unavailable'

export type DataStatus = {
  state: DataFreshnessState
  lastSuccessfulUpdateUtc: string | null
  newestListingSeenUtc: string | null
  enabledSources: number
  enabledSourcesWithRecentSuccess: number
  message: string
}

export type Overview = {
  generatedAtUtc: string
  activeListings: number
  postedDateKnown: number
  postedDateMissing: number
  newlyDiscovered: Change
  newlyPosted: Change
  salary: SalarySummary
  employers: EmployerSummary
  dataStatus: DataStatus
  mostActiveRegion: string | null
  mostRequestedSkill: string | null
  interval: MetricInterval
}

export type SkillDemand = {
  skillName: string
  jobCount: number
}

export type JobsByRegion = {
  region: string
  jobCount: number
}

export type FreshnessWindow = {
  days: number
  interval: MetricInterval
  newlyPosted: number
  previousNewlyPosted: number
  newlyDiscovered: number
  previousNewlyDiscovered: number
  postedDateKnown: number
  postedDateMissing: number
  comparisonAvailable: boolean
}

export type Freshness = {
  totalActiveJobs: number
  generatedAtUtc: string
  dataStatus: DataStatus
  windows: FreshnessWindow[]
}

export type SourceHealthState = 'healthy' | 'empty' | 'degraded' | 'stale' | 'disabled' | 'never-run'

export type SourceHealth = {
  source: string
  enabled: boolean
  parser: string
  method: string
  lastCrawledAt: string | null
  lastStatus: string | null
  lastError: string | null
  consecutiveFailures: number
  consecutiveZeroYield: number
  jobsYielded: number
  state: SourceHealthState
  degradeReason: string | null
  crawlFrequencyMinutes: number
}

export type HealthSummary = {
  healthy: number
  degraded: number
  stale: number
  empty: number
  disabled: number
  neverRun: number
  totalSources: number
}

export type HealthReport = {
  generatedAt: string
  summary: HealthSummary
  sources: SourceHealth[]
}
