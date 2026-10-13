export type Job = {
  id: string
  source: string
  title: string
  company: string | null
  location: string | null
  region: string | null
  salaryMin: number | null
  salaryMax: number | null
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

export type Overview = {
  totalActiveJobs: number
  newJobsToday: number
  newJobsThisWeek: number
  totalCompanies: number
  averageSalary: number | null
  medianSalary: number | null
  mostActiveRegion: string | null
  mostRequestedSkill: string | null
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
  jobCount: number
}

export type Freshness = {
  totalActiveJobs: number
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
