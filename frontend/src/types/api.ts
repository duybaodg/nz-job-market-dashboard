export type SalaryPeriod = 'Year' | 'Hour' | 'Day' | 'Unknown'

export type AdvertiserType = 'DirectEmployer' | 'Agency' | 'Board' | 'Unknown'
export type EmployerDisclosure = 'Disclosed' | 'AgencyOnly' | 'Unknown'

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
  sourceCategory: string
  advertiser: string
  advertiserType: AdvertiserType
  hiringEmployer: string | null
  employerDisclosure: EmployerDisclosure
  roleFamily: string
  roleFamilyInferred: boolean
  duplicateGroupId: string | null
  duplicateCount: number
  alsoOnSources: string[]
  isCanonicalListing: boolean
  employerCanonicalId: string | null
  employerCanonicalName: string | null
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
  agencyOnlyListings: number
  directEmployerListings: number
  agencyAdvertisedListings: number
  boardAdvertisedListings: number
  canonicalEmployers: number
  agencyCanonicals: number
  directEmployerCanonicals: number
  unmatchedEmployerNames: number
}

export type DeduplicationSummary = {
  active: boolean
  lastValidatedAtUtc: string | null
  matchVersion: string
  activeListings: number
  uniqueVacancies: number | null
  groups: number
  listingsMerged: number
  reviewCandidates: number
  groupCap: number
  lastRunStatus: string | null
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
  uniqueVacancies: number | null
  deduplication: DeduplicationSummary
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
  uniqueVacancies: number | null
  deduplication: DeduplicationSummary
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

export type SnapshotCoverage = {
  firstDate: string | null
  lastDate: string | null
  snapshotDays: number
  expectedDays: number
  missingDates: string[]
  hasHistory: boolean
  note: string
}

export type WeeklyNewListingPoint = {
  weekStartLocal: string
  weekEndLocalExclusive: string
  count: number
  isPartial: boolean
  coverageStartDate: string | null
  note: string | null
}

export type ActiveTrendPoint = {
  snapshotDate: string
  activeCount: number
  sourceCount: number
  coverageChanged: boolean
  sourcesAdded: string[]
  sourcesRemoved: string[]
}

export type SourceCoverageChange = {
  snapshotDate: string
  sourcesAdded: string[]
  sourcesRemoved: string[]
}

export type NewListingPeriod = {
  days: number
  interval: MetricInterval
  change: Change
  currentPeriodCovered: boolean
  dateBasis: string
  note: string | null
}

export type History = {
  generatedAtUtc: string
  coverage: SnapshotCoverage
  latestActiveCount: number
  latestSnapshotDate: string | null
  activeChange: Change
  comparableFromDate: string | null
  newListings: NewListingPeriod
  weeklyNewListings: WeeklyNewListingPoint[]
  activeTrend: ActiveTrendPoint[]
  coverageChanges: SourceCoverageChange[]
  note: string
}
