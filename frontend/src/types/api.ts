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
  industry: string | null
  descriptionSummary: string | null
  url: string
  postedDate: string | null
  skills: string[]
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
