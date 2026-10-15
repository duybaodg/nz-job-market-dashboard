import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
  BriefcaseBusiness,
  Building2,
  CalendarPlus,
  Lightbulb,
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
import { formatCurrency, formatNzDateTime } from './lib/format'
import { isComparableAnnual, salaryMidpoint } from './lib/salary'
import { getFreshness, getJobs, getOverview } from './services/api'
import type { Change, DataStatus, Job } from './types/api'

const emptyFilters: JobFilters = {
  timeRange: '',
  location: '',
  skill: '',
  company: '',
  industry: '',
  source: '',
  seniority: '',
  workMode: '',
  employmentType: '',
  salaryBand: '',
  experienceBand: '',
  programmingLanguage: '',
  status: 'active',
  search: '',
}

const emptyJobs: Job[] = []

function unique(values: Array<string | null | undefined>) {
  return [...new Set(values.filter((value): value is string => Boolean(value)))].sort()
}

function inSalaryBand(job: Job, band: string) {
  const midpoint = salaryMidpoint(job)

  if (band === 'missing') return midpoint == null
  if (!isComparableAnnual(job) || midpoint == null) return false
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

function describeChange(change: Change) {
  if (!change.comparisonAvailable) return change.note ?? 'Comparison unavailable'

  const absolute = change.absoluteChange ?? 0
  const sign = absolute > 0 ? '+' : ''

  if (change.noPriorBaseline) {
    return `${sign}${absolute} vs prior period (no prior baseline)`
  }

  const percent = change.percentChange ?? 0
  return `${sign}${absolute} (${percent > 0 ? '+' : ''}${percent}%) vs prior equal period`
}

function DataStatusBanner({ status }: { status: DataStatus }) {
  const tone = status.state === 'Fresh' ? 'ok' : status.state === 'Partial' ? 'warn' : 'alert'

  return (
    <section className={`data-status data-status-${tone}`} role="status" aria-label="Data freshness">
      <strong>{status.state} data</strong>
      <span>{status.message}</span>
      <span className="muted">
        Last successful update {formatNzDateTime(status.lastSuccessfulUpdateUtc)} ·{' '}
        {status.enabledSourcesWithRecentSuccess}/{status.enabledSources} enabled sources refreshed
      </span>
    </section>
  )
}

function App() {
  const [filters, setFilters] = useState<JobFilters>(emptyFilters)
  const jobsQuery = useQuery({ queryKey: ['jobs'], queryFn: () => getJobs({ status: 'all' }) })
  const overviewQuery = useQuery({ queryKey: ['overview'], queryFn: getOverview })
  const freshnessQuery = useQuery({ queryKey: ['freshness'], queryFn: getFreshness })
  const allJobs = jobsQuery.data ?? emptyJobs
  const overview = overviewQuery.data

  const options = useMemo(
    () => ({
      locations: unique(allJobs.map((job) => job.location)),
      skills: unique(allJobs.flatMap((job) => job.skills)),
      companies: unique(allJobs.map((job) => job.company)),
      // "Unknown" is not a usable filter value; hide the control entirely when
      // no source reports a real industry.
      industries: unique(allJobs.map((job) => job.industry)).filter((industry) => industry !== 'Unknown'),
      sources: unique(allJobs.map((job) => job.source)),
      seniorities: unique(allJobs.map((job) => job.seniority)),
      workModes: unique(allJobs.map((job) => job.workMode)),
      employmentTypes: unique(allJobs.map((job) => job.employmentType)),
      programmingLanguages: unique(allJobs.flatMap((job) => job.programmingLanguages)),
    }),
    [allJobs],
  )

  const jobs = useMemo(() => {
    const search = filters.search.trim().toLowerCase()
    const earliestPostedDate = filters.timeRange
      ? Date.now() - Number(filters.timeRange) * 24 * 60 * 60 * 1000
      : null

    return allJobs.filter((job) => {
      // Prefer the source's posted date, but fall back to when we first saw the
      // job; the table labels which basis was used for each row.
      const postedTime = job.postedDate
        ? new Date(job.postedDate).getTime()
        : new Date(job.firstSeenAt).getTime()

      return (
        (!earliestPostedDate || postedTime >= earliestPostedDate) &&
        (!filters.location || job.location === filters.location) &&
        (!filters.skill || job.skills.includes(filters.skill)) &&
        (!filters.company || job.company === filters.company) &&
        (!filters.industry || job.industry === filters.industry) &&
        (!filters.source || job.source === filters.source) &&
        (!filters.seniority || job.seniority === filters.seniority) &&
        (!filters.workMode || job.workMode === filters.workMode) &&
        (!filters.employmentType || job.employmentType === filters.employmentType) &&
        (!filters.salaryBand || inSalaryBand(job, filters.salaryBand)) &&
        (!filters.experienceBand || job.experienceBand === filters.experienceBand) &&
        (!filters.programmingLanguage || job.programmingLanguages.includes(filters.programmingLanguage)) &&
        (filters.status === 'all' || job.isActive === (filters.status === 'active')) &&
        (!search ||
          job.title.toLowerCase().includes(search) ||
          job.company?.toLowerCase().includes(search) ||
          job.descriptionSummary?.toLowerCase().includes(search) ||
          job.skills.some((skill) => skill.toLowerCase().includes(search)))
      )
    })
  }, [allJobs, filters])

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
    }),
    [jobs],
  )

  const isLoading = jobsQuery.isLoading || overviewQuery.isLoading
  const hasJobsError = jobsQuery.isError
  const hasOverviewError = overviewQuery.isError
  const freshness = freshnessQuery.data

  return (
    <div className="app-shell" id="overview">
      <AppSidebar />
      <main>
        <PageHeader
          jobCount={overview?.activeListings ?? 0}
          sourceCount={unique(allJobs.map((job) => job.source)).length}
        />

        {hasOverviewError ? (
          <section className="empty-state error-state" role="alert">
            Dashboard metrics could not be loaded, so no counts are shown. This is a data-loading failure,
            not zero demand. Check the backend API on <code>http://localhost:5050</code> and refresh.
          </section>
        ) : null}

        {overview ? <DataStatusBanner status={overview.dataStatus} /> : null}

        <FilterBar
          {...options}
          filters={filters}
          onFilterChange={(name, value) => setFilters((current) => ({ ...current, [name]: value }))}
        />

        {overview ? (
          <section className="stats-grid" aria-busy={isLoading}>
            <StatCard
              icon={BriefcaseBusiness}
              label="Active listings"
              value={overview.activeListings}
              detail="All active tracked listings"
            />
            <StatCard
              icon={CalendarPlus}
              label="New listings (7d)"
              value={overview.newlyDiscovered.current}
              detail={`First seen · ${describeChange(overview.newlyDiscovered)}`}
            />
            <StatCard
              icon={WalletCards}
              label="Median salary"
              value={
                overview.salary.medianAnnualNzd == null
                  ? 'Not enough data'
                  : formatCurrency(overview.salary.medianAnnualNzd)
              }
              detail={
                overview.salary.suppressed
                  ? `Suppressed: ${overview.salary.eligibleCount} comparable observations (minimum 5)`
                  : `${overview.salary.eligibleCount} comparable annual NZD listings${overview.salary.smallSample ? ' · small sample' : ''}`
              }
            />
            <StatCard
              icon={Percent}
              label="Salary coverage"
              value={`${overview.salary.coveragePercent}%`}
              detail={`${overview.salary.eligibleCount} of ${overview.salary.cohortCount} active listings`}
            />
          </section>
        ) : null}

        {overview ? (
          <section className="market-pulse" aria-label="Market pulse">
            <div>
              <span>Newly posted (7d)</span>
              <strong>
                <CalendarPlus aria-hidden="true" />
                {overview.newlyPosted.current}
                <small className="pulse-note">{describeChange(overview.newlyPosted)}</small>
              </strong>
            </div>
            <div>
              <span>Posted date known</span>
              <strong>
                <CalendarPlus aria-hidden="true" />
                {overview.activeListings === 0
                  ? '0%'
                  : `${Math.round((overview.postedDateKnown / overview.activeListings) * 100)}%`}
                <small className="pulse-note">
                  {overview.postedDateKnown} posted · {overview.postedDateMissing} first-seen only
                </small>
              </strong>
            </div>
            <div>
              <span>Identifiable employers</span>
              <strong>
                <Building2 aria-hidden="true" />
                {overview.employers.identifiableEmployers}
                <small className="pulse-note">
                  {overview.employers.coveragePercent}% of listings name an employer
                </small>
              </strong>
            </div>
            <div>
              <span>Most requested skill</span>
              <strong>
                <Lightbulb aria-hidden="true" />
                {overview.mostRequestedSkill ?? '-'}
              </strong>
            </div>
          </section>
        ) : null}

        <section className="chart-grid" id="market-signals">
          <ChartCard title="Jobs by region" detail="Share of filtered roles">
            <JobsByRegionChart data={chartData.jobsByRegion} />
          </ChartCard>
          <ChartCard title="Top skills" detail="Demand across filtered listings">
            <TopSkillsChart data={chartData.topSkills} />
          </ChartCard>
          <ChartCard title="Salary by role" detail="Median annual NZD midpoint · roles with 5+ comparable listings">
            <SalaryRangeChart jobs={jobs} />
          </ChartCard>
        </section>

        <section className="insight-grid">
          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>New listings by date basis</h2>
                <p>Posted counts use the source posted date only; first-seen counts use discovery time.</p>
              </div>
              <CalendarPlus aria-hidden="true" />
            </div>
            {freshnessQuery.isError ? (
              <div className="empty-state error-state" role="alert">
                Freshness could not be loaded. This is not zero new listings.
              </div>
            ) : (freshness?.windows ?? []).length === 0 ? (
              <span className="muted">No freshness data yet</span>
            ) : (
              <>
                <table className="freshness-table">
                  <thead>
                    <tr>
                      <th scope="col">Window</th>
                      <th scope="col">Posted</th>
                      <th scope="col">First seen</th>
                    </tr>
                  </thead>
                  <tbody>
                    {freshness!.windows.map((window) => (
                      <tr key={window.days}>
                        <th scope="row">Last {window.days} days</th>
                        <td>
                          {window.newlyPosted}
                          <small>
                            {window.comparisonAvailable
                              ? `prev ${window.previousNewlyPosted}`
                              : 'comparison unavailable'}
                          </small>
                        </td>
                        <td>
                          {window.newlyDiscovered}
                          <small>
                            {window.comparisonAvailable
                              ? `prev ${window.previousNewlyDiscovered}`
                              : 'comparison unavailable'}
                          </small>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <p className="muted freshness-caption">
                  {freshness!.windows[0]?.interval.label}. Posted-date coverage across the active cohort:{' '}
                  {freshness!.windows[0]?.postedDateKnown ?? 0} posted /{' '}
                  {(freshness!.windows[0]?.postedDateKnown ?? 0) + (freshness!.windows[0]?.postedDateMissing ?? 0)}{' '}
                  listings.
                </p>
              </>
            )}
          </section>
          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>Top hiring companies</h2>
                <p>Companies with the most filtered roles (advertiser and employer are not yet separated)</p>
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

        <section className="panel jobs-panel" id="jobs">
          <div className="panel-header">
            <h2>Recent jobs</h2>
            <span>
              {jobs.length} matching {filters.status === 'active' ? 'active ' : ''}roles
            </span>
          </div>
          {hasJobsError ? (
            <div className="empty-state error-state" role="alert">
              Job listings could not be loaded. This is a data-loading failure, not an empty result.
            </div>
          ) : (
            <JobsTable jobs={jobs} />
          )}
        </section>
      </main>
    </div>
  )
}

export default App
