import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { AppSidebar } from '../components/layout/AppSidebar'
import { DataStatusBanner } from '../components/dashboard/DataStatusBanner'
import { FilterChips } from '../components/jobs/FilterChips'
import { JobFilters, type FilterOptions } from '../components/jobs/JobFilters'
import { JobResults } from '../components/jobs/JobResults'
import { ViewHeader } from '../components/layout/ViewHeader'
import { navigate, useAppLocation } from '../hooks/useAppLocation'
import {
  employerActivity,
  filterJobs,
  regionDemand,
  roleFamilyDemand,
  salaryDistribution,
  skillDemand,
  sortJobs,
} from '../lib/analytics'
import { buildJobsCsv, downloadCsv } from '../lib/csv'
import { formatCurrency } from '../lib/format'
import {
  clearJobFilters,
  jobFilterChips,
  parseJobViewState,
  removeJobFilter,
  serializeJobViewState,
  setJobFilter,
  setJobPage,
  setJobSort,
  type JobFilterState,
  type JobViewState,
} from '../lib/urlState'
import { getJobs, getOverview } from '../services/api'
import type { Job } from '../types/api'

const PAGE_SIZE = 10
const emptyJobs: Job[] = []

const CSV_DEFINITIONS = [
  'A source listing is one advertisement captured from one source; deduplication into unique vacancies is not implemented, so counts are listings.',
  'Posted date is the source posted date; "First seen" is the crawler discovery timestamp and is never substituted for a posted date.',
  'Advertised salary is shown with its own currency and pay period; "Salary not advertised" means no figure was confirmed.',
  'Hiring employer is the named client where disclosed; agency-only and unknown-employer listings are labelled and never guessed.',
  'Role family is inferred deterministically from the job title; the original title is retained.',
]

function unique(values: Array<string | null | undefined>) {
  return [...new Set(values.filter((value): value is string => Boolean(value) && value !== 'Unknown'))].sort()
}

function optionsFor(jobs: Job[]): FilterOptions {
  return {
    locations: unique(jobs.map((job) => job.location ?? job.region)),
    workModes: unique(jobs.map((job) => job.workMode)),
    seniorities: unique(jobs.map((job) => job.seniority)),
    roleFamilies: unique(jobs.map((job) => job.roleFamily)),
    skills: unique(jobs.flatMap((job) => job.skills)),
    companies: unique(jobs.map((job) => job.company)),
    sources: unique(jobs.map((job) => job.source)),
    employmentTypes: unique(jobs.map((job) => job.employmentType)),
    experienceBands: unique(jobs.map((job) => job.experienceBand)),
    programmingLanguages: unique(jobs.flatMap((job) => job.programmingLanguages)),
  }
}

function jobHref(state: JobViewState) {
  const query = serializeJobViewState(state)
  return query ? `/jobs?${query}` : '/jobs'
}

export function FindJobsPage() {
  const { search } = useAppLocation()
  const state = useMemo(() => parseJobViewState(search), [search])
  const jobsQuery = useQuery({ queryKey: ['jobs'], queryFn: () => getJobs({ status: 'all' }) })
  const overviewQuery = useQuery({ queryKey: ['overview'], queryFn: getOverview })

  const allJobs = jobsQuery.data ?? emptyJobs
  const overview = overviewQuery.data
  const options = useMemo(() => optionsFor(allJobs), [allJobs])

  const filtered = useMemo(() => {
    const cohort = filterJobs(allJobs, state.filters, Date.now())
    return sortJobs(cohort, state.sort)
  }, [allJobs, state.filters, state.sort])

  const pageCount = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE))
  const page = Math.min(state.page, pageCount)
  const pageJobs = filtered.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE)
  const chips = jobFilterChips(state.filters)

  const context = useMemo(() => {
    const families = roleFamilyDemand(filtered)
    const skills = skillDemand(filtered, 1)
    const regions = regionDemand(filtered)
    const salary = salaryDistribution(filtered)
    const employers = employerActivity(filtered)

    return {
      topFamily: families[0] ?? null,
      topSkill: skills.bars[0] ?? null,
      topRegion: regions.bars[0] ?? null,
      salary,
      employers,
    }
  }, [filtered])

  const update = (next: JobViewState, replace = false) => navigate(jobHref(next), { replace })

  const onFilterChange = (name: keyof JobFilterState, value: string) =>
    update(setJobFilter(state, name, value), name === 'q')
  const onSortChange = (sort: JobViewState['sort']) => update(setJobSort(state, sort))
  const onPageChange = (nextPage: number) => update(setJobPage(state, nextPage))
  const onRemove = (name: keyof JobFilterState) => update(removeJobFilter(state, name))
  const onClearAll = () => update(clearJobFilters(state))

  const exportCsv = () => {
    const csv = buildJobsCsv(filtered, {
      view: 'Find jobs',
      filters: state.filters,
      sort: state.sort,
      period: state.filters.days ? `Last ${state.filters.days} days` : 'All retained listings',
      generatedAt: new Date().toISOString(),
      definitions: CSV_DEFINITIONS,
    })
    downloadCsv(`nz-jobs-${new Date().toISOString().slice(0, 10)}.csv`, csv)
  }

  return (
    <div className="app-shell" id="find-jobs">
      <AppSidebar currentPath="/jobs" />
      <main>
        <ViewHeader
          eyebrow="Tracked NZ tech listings"
          title="Find jobs"
          description="Search, compare and open current tracked NZ technology roles."
          status={overview?.dataStatus}
        />

        {overviewQuery.isError ? (
          <section className="empty-state error-state" role="alert">
            Market context could not be loaded. This is a data-loading failure, not zero demand.
          </section>
        ) : null}

        {overview ? <DataStatusBanner status={overview.dataStatus} /> : null}

        <JobFilters options={options} filters={state.filters} onChange={onFilterChange} />

        <section className="results-panel" id="results">
          <div className="results-header">
            <div>
              <h2>
                {jobsQuery.isError
                  ? 'Results unavailable'
                  : `${filtered.length.toLocaleString('en-NZ')} matching ${
                      state.filters.status === 'active' ? 'active ' : ''
                    }roles`}
              </h2>
              <FilterChips chips={chips} onRemove={onRemove} onClearAll={onClearAll} />
            </div>
            <button type="button" className="refresh-button" onClick={exportCsv} disabled={filtered.length === 0}>
              <Download aria-hidden="true" /> Export CSV
            </button>
          </div>

          {jobsQuery.isError ? (
            <div className="empty-state error-state" role="alert">
              Job listings could not be loaded. This is a data-loading failure, not an empty result.
            </div>
          ) : jobsQuery.isLoading ? (
            <div className="table-empty" aria-busy="true">
              Loading listings…
            </div>
          ) : (
            <JobResults
              jobs={pageJobs}
              total={filtered.length}
              page={page}
              pageSize={PAGE_SIZE}
              sort={state.sort}
              onSortChange={onSortChange}
              onPageChange={onPageChange}
            />
          )}
        </section>

        <section className="market-context" aria-label="Market context for the current search">
          <div>
            <span>Median advertised salary</span>
            <strong>{context.salary.suppressed ? 'Not enough data' : formatCurrency(context.salary.median)}</strong>
            <small>
              {context.salary.sampleSize} comparable annual NZD listings
              {context.salary.smallSample ? ' · small sample' : ''} of {filtered.length}
            </small>
          </div>
          <div>
            <span>Leading role family</span>
            <strong>{context.topFamily?.label ?? '—'}</strong>
            <small>{context.topFamily ? `${context.topFamily.count} listings · ${context.topFamily.share}%` : 'no data'}</small>
          </div>
          <div>
            <span>Most requested skill</span>
            <strong>{context.topSkill?.skill ?? '—'}</strong>
            <small>{context.topSkill ? `${context.topSkill.count} listings` : 'no skills extracted'}</small>
          </div>
          <div>
            <span>Top region</span>
            <strong>{context.topRegion?.label ?? '—'}</strong>
            <small>{context.topRegion ? `${context.topRegion.count} listings` : 'no data'}</small>
          </div>
        </section>
      </main>
    </div>
  )
}
