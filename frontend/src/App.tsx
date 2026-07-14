import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
  BriefcaseBusiness,
  Building2,
  CalendarPlus,
  Database,
  Lightbulb,
  MapPin,
  Percent,
  WalletCards,
} from 'lucide-react'
import { AppSidebar } from './components/layout/AppSidebar'
import { ChartCard } from './components/dashboard/ChartCard'
import { FilterBar, type JobFilters } from './components/dashboard/FilterBar'
import { JobsByRegionChart } from './components/charts/JobsByRegionChart'
import { JobsTable } from './components/tables/JobsTable'
import { PageHeader } from './components/layout/PageHeader'
import { SalaryRangeChart } from './components/charts/SalaryRangeChart'
import { StatCard } from './components/dashboard/StatCard'
import { TopSkillsChart } from './components/charts/TopSkillsChart'
import { formatCurrency } from './lib/format'
import { getJobs } from './services/api'
import type { Job } from './types/api'

const emptyFilters: JobFilters = {
  timeRange: '',
  region: '',
  skill: '',
  company: '',
  industry: '',
  source: '',
  seniority: '',
  workMode: '',
  employmentType: '',
  salaryBand: '',
  search: '',
}

const emptyJobs: Job[] = []

function unique(values: Array<string | null | undefined>) {
  return [...new Set(values.filter((value): value is string => Boolean(value)))].sort()
}

function salaryMidpoint(job: Job) {
  if (job.salaryMin == null && job.salaryMax == null) return null

  return (((job.salaryMin ?? job.salaryMax) ?? 0) + ((job.salaryMax ?? job.salaryMin) ?? 0)) / 2
}

function inSalaryBand(job: Job, band: string) {
  const midpoint = salaryMidpoint(job)

  if (band === 'missing') return midpoint == null
  if (midpoint == null) return false
  if (band === 'lt70') return midpoint < 70000
  if (band === '70-100') return midpoint >= 70000 && midpoint < 100000
  if (band === '100-130') return midpoint >= 100000 && midpoint < 130000
  if (band === 'gte130') return midpoint >= 130000

  return true
}

function countBy<T>(items: T[], getKey: (item: T) => string | null | undefined) {
  const counts = new Map<string, number>()

  for (const item of items) {
    const key = getKey(item)
    if (key) counts.set(key, (counts.get(key) ?? 0) + 1)
  }

  return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
}

function median(values: number[]) {
  if (values.length === 0) return null

  const sorted = values.toSorted((a, b) => a - b)
  const midpoint = Math.floor(sorted.length / 2)

  return sorted.length % 2 === 0 ? (sorted[midpoint - 1] + sorted[midpoint]) / 2 : sorted[midpoint]
}

function App() {
  const [filters, setFilters] = useState<JobFilters>(emptyFilters)
  const jobsQuery = useQuery({ queryKey: ['jobs'], queryFn: () => getJobs({}) })
  const allJobs = jobsQuery.data ?? emptyJobs

  const options = useMemo(
    () => ({
      regions: unique(allJobs.map((job) => job.region)),
      skills: unique(allJobs.flatMap((job) => job.skills)),
      companies: unique(allJobs.map((job) => job.company)),
      industries: unique(allJobs.map((job) => job.industry)),
      sources: unique(allJobs.map((job) => job.source)),
      seniorities: unique(allJobs.map((job) => job.seniority)),
      workModes: unique(allJobs.map((job) => job.workMode)),
      employmentTypes: unique(allJobs.map((job) => job.employmentType)),
    }),
    [allJobs],
  )

  const jobs = useMemo(() => {
    const search = filters.search.trim().toLowerCase()
    const earliestPostedDate = filters.timeRange
      ? Date.now() - Number(filters.timeRange) * 24 * 60 * 60 * 1000
      : null

    return allJobs.filter((job) => {
      const postedTime = job.postedDate ? new Date(job.postedDate).getTime() : null

      return (
        (!earliestPostedDate || (postedTime != null && postedTime >= earliestPostedDate)) &&
        (!filters.region || job.region === filters.region) &&
        (!filters.skill || job.skills.includes(filters.skill)) &&
        (!filters.company || job.company === filters.company) &&
        (!filters.industry || job.industry === filters.industry) &&
        (!filters.source || job.source === filters.source) &&
        (!filters.seniority || job.seniority === filters.seniority) &&
        (!filters.workMode || job.workMode === filters.workMode) &&
        (!filters.employmentType || job.employmentType === filters.employmentType) &&
        (!filters.salaryBand || inSalaryBand(job, filters.salaryBand)) &&
        (!search ||
          job.title.toLowerCase().includes(search) ||
          job.company?.toLowerCase().includes(search) ||
          job.skills.some((skill) => skill.toLowerCase().includes(search)))
      )
    })
  }, [allJobs, filters])

  const summary = useMemo(() => {
    const salaryValues = jobs.map(salaryMidpoint).filter((value): value is number => value != null)
    const topSkill = countBy(
      jobs.flatMap((job) => job.skills),
      (skill) => skill,
    )[0]?.[0]
    const topRegion = countBy(jobs, (job) => job.region)[0]?.[0]
    const today = new Date()
    today.setHours(0, 0, 0, 0)
    const weekStart = Date.now() - 7 * 24 * 60 * 60 * 1000

    return {
      averageSalary:
        salaryValues.length === 0 ? null : salaryValues.reduce((total, value) => total + value, 0) / salaryValues.length,
      medianSalary: median(salaryValues),
      newToday: jobs.filter((job) => job.postedDate && new Date(job.postedDate).getTime() >= today.getTime()).length,
      newThisWeek: jobs.filter((job) => job.postedDate && new Date(job.postedDate).getTime() >= weekStart).length,
      salaryCoverage: jobs.length === 0 ? 0 : Math.round((salaryValues.length / jobs.length) * 100),
      topRegion: topRegion ?? '-',
      topSkill: topSkill ?? '-',
      sourceCount: countBy(jobs, (job) => job.source).length,
    }
  }, [jobs])

  const chartData = useMemo(
    () => ({
      jobsByRegion: countBy(jobs, (job) => job.region).map(([region, jobCount]) => ({ region, jobCount })),
      topSkills: countBy(
        jobs.flatMap((job) => job.skills),
        (skill) => skill,
      )
        .slice(0, 10)
        .map(([skillName, jobCount]) => ({ skillName, jobCount })),
      topCompanies: countBy(jobs, (job) => job.company).slice(0, 6),
      sources: countBy(jobs, (job) => job.source),
    }),
    [jobs],
  )

  const isLoading = jobsQuery.isLoading
  const hasError = jobsQuery.isError

  return (
    <div className="app-shell">
      <AppSidebar />
      <main>
        <PageHeader />

        {hasError ? (
          <section className="empty-state">
            Start the backend API on <code>http://localhost:5050</code> and refresh the dashboard.
          </section>
        ) : null}

        <FilterBar
          {...options}
          filters={filters}
          onFilterChange={(name, value) => setFilters((current) => ({ ...current, [name]: value }))}
        />

        <section className="stats-grid" aria-busy={isLoading}>
          <StatCard icon={BriefcaseBusiness} label="Active jobs" value={jobs.length} detail="Filtered roles" />
          <StatCard icon={CalendarPlus} label="New today" value={summary.newToday} detail="Posted date" />
          <StatCard icon={CalendarPlus} label="New this week" value={summary.newThisWeek} detail="Posted date" />
          <StatCard icon={WalletCards} label="Average salary" value={formatCurrency(summary.averageSalary)} detail="Salary midpoint" />
          <StatCard icon={WalletCards} label="Median salary" value={formatCurrency(summary.medianSalary)} detail="Salary midpoint" />
          <StatCard icon={Percent} label="Salary coverage" value={`${summary.salaryCoverage}%`} detail="Jobs with salary" />
          <StatCard icon={MapPin} label="Top region" value={summary.topRegion} detail="By job count" />
          <StatCard icon={Lightbulb} label="Top skill" value={summary.topSkill} detail="By job count" />
        </section>

        <section className="chart-grid">
          <ChartCard title="Jobs by region" detail="Filtered roles by market">
            <JobsByRegionChart data={chartData.jobsByRegion} />
          </ChartCard>
          <ChartCard title="Top skills" detail="Demand across filtered listings">
            <TopSkillsChart data={chartData.topSkills} />
          </ChartCard>
          <ChartCard title="Salary midpoint by role" detail="Filtered role salary signals">
            <SalaryRangeChart jobs={jobs} />
          </ChartCard>
        </section>

        <section className="insight-grid">
          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>Data sources</h2>
                <p>Boards and adapters represented in this view</p>
              </div>
              <Database aria-hidden="true" />
            </div>
            <div className="rank-list">
              {chartData.sources.length === 0 ? (
                <span className="muted">No sources in this view</span>
              ) : (
                chartData.sources.map(([source, count]) => (
                  <div key={source} className="rank-row">
                    <span>{source}</span>
                    <strong>{count}</strong>
                  </div>
                ))
              )}
            </div>
          </section>

          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>Top hiring companies</h2>
                <p>Companies with the most filtered roles</p>
              </div>
              <Building2 aria-hidden="true" />
            </div>
            <div className="rank-list">
              {chartData.topCompanies.length === 0 ? (
                <span className="muted">No companies in this view</span>
              ) : (
                chartData.topCompanies.map(([company, count]) => (
                  <div key={company} className="rank-row">
                    <span>{company}</span>
                    <strong>{count}</strong>
                  </div>
                ))
              )}
            </div>
          </section>
        </section>

        <section className="panel">
          <div className="panel-header">
            <h2>Recent jobs</h2>
            <span>{jobs.length} roles</span>
          </div>
          <JobsTable jobs={jobs} />
        </section>
      </main>
    </div>
  )
}

export default App
