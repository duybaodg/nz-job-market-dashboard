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
  /** Round 22 (C1): versioned Data & Analytics sub-role; null when outside the family or explicitly uncertain. */
  dataSubRoleKey: string | null
  dataSubRole: string | null
  dataSubRoleUncertain: boolean
  dataSubRoleCandidates: string[]
  dataSubRoleUncertainReason: string | null
  dataSubRoleVersion: string
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
  listingsInGroups: number
  reviewCandidates: number
  groupCap: number
  lastRunStatus: string | null
  status: 'validated' | 'not-run' | 'failed' | 'invalid'
  unavailableReason: string | null
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
  discoveryBasis: string
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
  discoveryBasis: string
}

export type SourceHealthState =
  | 'healthy'
  | 'empty'
  | 'degraded'
  | 'stale'
  | 'disabled'
  | 'never-run'
  | 'retired'

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
  retired: number
  totalSources: number
}

export type HealthReport = {
  generatedAt: string
  summary: HealthSummary
  sources: SourceHealth[]
  retiredSources: SourceHealth[]
}

export type SnapshotCoverage = {
  firstDate: string | null
  lastDate: string | null
  snapshotDays: number
  expectedDays: number
  missingDates: string[]
  hasHistory: boolean
  note: string
  retentionDays: number
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

export type SourceCoverageEvent = {
  source: string
  direction: 'added' | 'removed'
  classification: string
  evidence: string
}

export type SourceCoverageChange = {
  snapshotDate: string
  sourcesAdded: string[]
  sourcesRemoved: string[]
  events: SourceCoverageEvent[]
}

export type ConstantSourceComparison = {
  available: boolean
  reason: string | null
  fromSnapshotDate: string | null
  toSnapshotDate: string | null
  observedSourceCount: number | null
  eligibleSourceCount: number | null
  fromActiveCount: number | null
  toActiveCount: number | null
  change: Change | null
  rawFromActiveCount: number | null
  rawToActiveCount: number | null
  rawChange: Change | null
  collectionAssociatedResidual: number | null
  eligibilityRule: string
  sources: string[]
}

export type HistoryFilter = {
  region: string | null
  roleFamily: string | null
  seniority: string | null
  skill: string | null
  hasFilters: boolean
  historyFilterSupported: boolean
  note: string
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
  constantSourceComparison: ConstantSourceComparison
  filter: HistoryFilter
  scopeNote: string
  note: string
}

export type QualityMeasure = {
  key: string
  label: string
  numerator: number | null
  denominator: number | null
  percent: number | null
  displayValue: string
  scope: string
  capturedAtUtc: string
  available: boolean
  unavailableReason: string | null
  definition: string
}

export type QualityClassification = {
  available: boolean
  unavailableReason: string | null
  sampledListings: number
  reviewedListings: number
  correct: number
  falsePositive: number
  falseNegative: number
  ambiguous: number
  unknown: number
  accuracyPercent: number | null
  accuracyDenominator: number
  ruleVersion: string
  scope: string
  evaluatedAtUtc: string | null
  method: string | null
}

export type QualitySummary = {
  generatedAtUtc: string
  population: string
  activeListings: number
  measures: QualityMeasure[]
  classification: QualityClassification
  methodologyNote: string
}

export type DataSubRoleSkill = {
  skillName: string
  listingCount: number
  sharePercent: number
}

export type DataSubRoleBreakdown = {
  key: string
  label: string
  uncertain: boolean
  candidateLabels: string[]
  listingCount: number
  sharePercent: number
  salary: SalarySummary
  listingsWithSkills: number
  skillCoveragePercent: number
  topSkills: DataSubRoleSkill[]
}

export type DataRoleExplorerFilters = {
  region: string | null
  seniority: string | null
  workMode: string | null
  skill: string | null
  hasFilters: boolean
  note: string
}

export type DataRoleExplorer = {
  generatedAtUtc: string
  family: string
  taxonomyVersion: string
  taxonomyEffectiveDate: string
  taxonomyDescription: string
  taxonomyScope: string
  uncertainLabel: string
  cohortCount: number
  classifiedCount: number
  uncertainCount: number
  classifiedSharePercent: number
  filters: DataRoleExplorerFilters
  roles: DataSubRoleBreakdown[]
  salaryBasis: string
  skillBasis: string
  scopeNote: string
}
