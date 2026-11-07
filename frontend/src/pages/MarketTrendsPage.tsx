import { lazy, Suspense, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowRight, Building2, CalendarPlus, Percent, WalletCards } from 'lucide-react'
import { FilterChips } from '../components/jobs/FilterChips'
import { DataRoleExplorer } from '../components/explore/DataRoleExplorer'
import { AppSidebar } from '../components/layout/AppSidebar'
import { DataStatusBanner } from '../components/dashboard/DataStatusBanner'
import { RankedBarList } from '../components/charts/RankedBarList'
import { StatCard } from '../components/dashboard/StatCard'
import { ViewHeader } from '../components/layout/ViewHeader'
import { navigate, useAppLocation } from '../hooks/useAppLocation'

// Round 19: the three Recharts charts are the only consumers of the charting
// library. Loading them on demand keeps Recharts out of the initial bundle for
// the /jobs and /health views; the chart code arrives when /trends renders.
const WeeklyTrendChart = lazy(() =>
  import('../components/charts/WeeklyTrendChart').then((module) => ({ default: module.WeeklyTrendChart })),
)
const ActiveTrendChart = lazy(() =>
  import('../components/charts/ActiveTrendChart').then((module) => ({ default: module.ActiveTrendChart })),
)
const SalaryDistributionChart = lazy(() =>
  import('../components/charts/SalaryDistributionChart').then((module) => ({
    default: module.SalaryDistributionChart,
  })),
)
// Round 24: the two type-aware skill-demand charts are split out the same way,
// so their table alternative and bar list only load when /trends renders.
const SkillDemandChart = lazy(() =>
  import('../components/charts/SkillDemandChart').then((module) => ({ default: module.SkillDemandChart })),
)

const chartFallback = (
  <div className="chart-empty" aria-busy="true">
    Loading chart…
  </div>
)

import {
  employerActivity,
  regionDemand,
  roleFamilyDemand,
  salaryByRoleFamily,
  salaryDistribution,
  skillDemand,
} from '../lib/analytics'
import { describeChange } from '../lib/change'
import { formatCurrency, formatNzDateTime } from '../lib/format'
import {
  buildJobsHref,
  parseTrendsFilterState,
  serializeTrendsFilterState,
  setTrendsFilter,
  trendsFilterChips,
  type TrendsFilterState,
} from '../lib/urlState'
import { getHistory, getJobs, getOverview, getQuality, getSkillDemand } from '../services/api'
import type { Job } from '../types/api'

const emptyJobs: Job[] = []

function time(value: string | null): number {
  return value ? new Date(value).getTime() : 0
}

function applyTrends(jobs: Job[], filters: TrendsFilterState, exclude?: keyof TrendsFilterState): Job[] {
  return jobs.filter(
    (job) =>
      (exclude === 'region' || !filters.region || job.region === filters.region || job.location === filters.region) &&
      (exclude === 'roleFamily' || !filters.roleFamily || job.roleFamily === filters.roleFamily) &&
      (exclude === 'skill' || !filters.skill || job.skills.includes(filters.skill)) &&
      (exclude === 'seniority' || !filters.seniority || job.seniority === filters.seniority),
  )
}

function countFirstSeenInInterval(
  jobs: Job[],
  interval: { startUtc: string; endUtc: string } | null | undefined,
): number | null {
  if (!interval) return null
  const start = time(interval.startUtc)
  const end = time(interval.endUtc)
  if (!start || !end) return null
  return jobs.filter((job) => {
    const firstSeen = time(job.firstSeenAt)
    return firstSeen >= start && firstSeen < end
  }).length
}

export function MarketTrendsPage() {
  const { search } = useAppLocation()
  const filters = useMemo(() => parseTrendsFilterState(search), [search])
  const periodDays = Number(filters.period)
  const filteredScope = Boolean(filters.region || filters.roleFamily || filters.skill || filters.seniority)
  const chips = trendsFilterChips(filters)

  const jobsQuery = useQuery({ queryKey: ['jobs'], queryFn: () => getJobs({ status: 'all', limit: 5000 }) })
  const overviewQuery = useQuery({ queryKey: ['overview'], queryFn: getOverview })
  const qualityQuery = useQuery({ queryKey: ['quality'], queryFn: getQuality })
  // Round 24: server-side type-aware demand for languages/frameworks. The
  // request is global (not narrowed by the cohort chips) and its response
  // carries its own as-of time, denominator and taxonomy version.
  const skillDemandQuery = useQuery({
    queryKey: ['skill-demand', 'ProgrammingLanguage', 'Framework', 10],
    queryFn: () => getSkillDemand({ types: ['ProgrammingLanguage', 'Framework'], limit: 10 }),
  })

  // Round 20 (A2): history takes the same cohort as the current cards, and the
  // filter values are part of the query key so an old request can never land
  // under the newly selected cohort.
  const historyFilters = useMemo(
    () => ({
      region: filters.region || undefined,
      roleFamily: filters.roleFamily || undefined,
      seniority: filters.seniority || undefined,
      skill: filters.skill || undefined,
    }),
    [filters.region, filters.roleFamily, filters.seniority, filters.skill],
  )
  const historyQuery = useQuery({
    queryKey: ['history', periodDays, historyFilters],
    queryFn: () => getHistory(periodDays, historyFilters),
  })

  const allJobs = jobsQuery.data ?? emptyJobs
  const overview = overviewQuery.data
  const history = historyQuery.data

  const activeCohort = useMemo(() => allJobs.filter((job) => job.isActive), [allJobs])
  const cohort = useMemo(() => applyTrends(activeCohort, filters), [activeCohort, filters])
  const familyCohort = useMemo(() => applyTrends(activeCohort, filters, 'roleFamily'), [activeCohort, filters])
  const regionCohort = useMemo(() => applyTrends(activeCohort, filters, 'region'), [activeCohort, filters])
  const skillCohort = useMemo(() => applyTrends(activeCohort, filters, 'skill'), [activeCohort, filters])
  // Discovery counts include retained rows that have since expired; using only
  // the active cohort would silently create a survivor-only comparison.
  const allStatusCohort = useMemo(() => applyTrends(allJobs, filters), [allJobs, filters])

  const metricsLoading = jobsQuery.isLoading || overviewQuery.isLoading
  const metricsRefreshing = !metricsLoading && (jobsQuery.isFetching || overviewQuery.isFetching)
  const jobsUnavailable = jobsQuery.isError
  const historyFilterSupported = history?.filter.historyFilterSupported ?? true
  const skillFallbackCurrent = useMemo(
    () =>
      !historyFilterSupported && filteredScope
        ? countFirstSeenInInterval(allStatusCohort, history?.newListings.interval)
        : null,
    [allStatusCohort, filteredScope, history, historyFilterSupported],
  )

  const newListings = useMemo(() => {
    if (historyQuery.isLoading) {
      return {
        status: 'loading' as const,
        current: null,
        change: null,
        comparisonAvailable: false,
        dateBasis: 'Retained daily snapshots (discovery)',
        note: 'Loading retained history…',
      }
    }

    if (historyQuery.isError || !history) {
      return {
        status: 'error' as const,
        current: null,
        change: null,
        comparisonAvailable: false,
        dateBasis: 'Retained daily snapshots (discovery)',
        note: 'Historical snapshots could not be loaded. This is a data-loading failure, not zero discovery.',
      }
    }

    if (!historyFilterSupported && filteredScope) {
      return {
        status: skillFallbackCurrent == null ? ('unavailable' as const) : ('ready' as const),
        current: skillFallbackCurrent,
        change: null,
        comparisonAvailable: false,
        dateBasis: 'Current retained rows only (discovery), including rows that have since expired',
        note: history.filter.note,
      }
    }

    const period = history.newListings
    return {
      status: period.currentPeriodCovered ? ('ready' as const) : ('unavailable' as const),
      current: period.currentPeriodCovered ? period.change.current : null,
      change: period.change,
      comparisonAvailable: period.currentPeriodCovered && period.change.comparisonAvailable,
      dateBasis: period.dateBasis,
      note: period.note ?? '',
    }
  }, [filteredScope, history, historyFilterSupported, historyQuery.isError, historyQuery.isLoading, skillFallbackCurrent])

  const salary = useMemo(() => salaryDistribution(cohort), [cohort])
  const roleSalaries = useMemo(() => salaryByRoleFamily(cohort), [cohort])
  const employers = useMemo(() => employerActivity(cohort), [cohort])
  const families = useMemo(() => roleFamilyDemand(familyCohort), [familyCohort])
  const regions = useMemo(() => regionDemand(regionCohort), [regionCohort])
  const skills = useMemo(() => skillDemand(skillCohort), [skillCohort])

  const activeChange = !filteredScope && history ? history.activeChange : null
  const selectedFamily = filters.roleFamily || families[0]?.label || ''
  const selectedFamilyRow = families.find((family) => family.label === selectedFamily) ?? null

  const setFilter = (name: keyof TrendsFilterState, value: string) => {
    const next = setTrendsFilter(filters, name, value)
    const query = serializeTrendsFilterState(next)
    navigate(query ? `/trends?${query}` : '/trends')
  }

  const clearFilters = () => {
    // "Clear all" clears the cohort filters but keeps the reporting period the
    // user selected; the period is a reporting control, not a cohort filter.
    const next: TrendsFilterState = { ...filters, region: '', roleFamily: '', skill: '', seniority: '' }
    const query = serializeTrendsFilterState(next)
    navigate(query ? `/trends?${query}` : '/trends')
  }

  const matchingJobsHref = buildJobsHref({
    location: filters.region,
    roleFamily: filters.roleFamily,
    skill: filters.skill,
    seniority: filters.seniority,
  })

  const clearExplorerFilters = () => {
    const next: TrendsFilterState = {
      ...filters,
      exploreRole: '',
      exploreRegion: '',
      exploreSeniority: '',
      exploreWorkMode: '',
      exploreSkill: '',
    }
    const query = serializeTrendsFilterState(next)
    navigate(query ? `/trends?${query}` : '/trends')
  }

  const coverage = history?.coverage
  const missingSnapshotDays = coverage?.missingDates.length ?? 0
  const constantComparison = history?.constantSourceComparison
  const deduplication = overview?.deduplication
  // Round 15 (M-5): a unique-vacancy count is global (dedup groups cannot be
  // filtered by the current facets). Under an active filter, show the filtered
  // active-listing count instead and label the scope explicitly.
  // Round 20 (A1): only a pass that passed integrity validation may publish a
  // vacancy count; a missing/failed pass is "unavailable", not zero.
  const showUniqueVacancies =
    !filteredScope && deduplication?.status === 'validated' && overview?.uniqueVacancies != null
  const deduplicationStatus = deduplication
    ? deduplication.status === 'validated'
      ? `Grouping validated ${formatNzDateTime(deduplication.lastValidatedAtUtc)}`
      : `Deduplication ${deduplication.status.replace('-', ' ')}${deduplication.unavailableReason ? ` — ${deduplication.unavailableReason}` : ''}`
    : 'Deduplication status unavailable'

  return (
    <div className="app-shell" id="market-trends">
      <AppSidebar currentPath="/trends" />
      <main id="main-content">
        <ViewHeader
          eyebrow="Tracked NZ tech listings"
          title="Market trends"
          description="Compare demand and detect changes in the tracked NZ technology market. Not a full census of NZ hiring."
          status={overview?.dataStatus}
          actions={
            filteredScope ? (
              <a className="refresh-button" href={matchingJobsHref}>
                View matching jobs <ArrowRight aria-hidden="true" />
              </a>
            ) : null
          }
        />

        <section className="scope-bar" aria-label="Reporting scope">
          <div>
            <span>Scope</span>
            <strong>Tracked NZ tech listings</strong>
          </div>
          <div>
            <span>Current cohort</span>
            <strong>
              {metricsLoading
                ? 'Loading…'
                : jobsUnavailable
                  ? 'Unavailable'
                  : `${cohort.length.toLocaleString('en-NZ')} active listing${cohort.length === 1 ? '' : 's'}`}
            </strong>
          </div>
          <div>
            <span>Reporting period</span>
            <select
              aria-label="Reporting period"
              value={filters.period}
              onChange={(event) => setFilter('period', event.target.value)}
            >
              <option value="7">Last 7 complete days</option>
              <option value="15">Last 15 complete days</option>
              <option value="30">Last 30 complete days</option>
            </select>
          </div>
          <div>
            <span>Window (NZ)</span>
            <strong>
              {history
                ? `${history.newListings.interval.startLocalDate} to ${history.newListings.interval.endLocalDateExclusive} (end exclusive)`
                : historyQuery.isLoading
                  ? 'Loading…'
                  : 'Unavailable'}
            </strong>
          </div>
          <div>
            <span>Comparison</span>
            <strong>
              {newListings.status === 'loading'
                ? 'Loading retained history…'
                : newListings.status === 'error'
                  ? 'Comparison unavailable (load failed)'
                  : newListings.comparisonAvailable
                    ? 'Previous equal complete period'
                    : 'Comparison unavailable'}
            </strong>
          </div>
          <div>
            <span>Data freshness</span>
            <strong>
              {metricsLoading
                ? 'Loading…'
                : overview
                  ? formatNzDateTime(overview.dataStatus.lastSuccessfulUpdateUtc)
                  : 'Unavailable'}
            </strong>
          </div>
        </section>
        <FilterChips chips={chips} onRemove={(name) => setFilter(name, '')} onClearAll={clearFilters} />

        {overviewQuery.isError || jobsQuery.isError ? (
          <section className="empty-state error-state" role="alert">
            Market metrics could not be loaded, so no counts are shown. This is a data-loading failure, not zero demand.
          </section>
        ) : null}

        {overview ? <DataStatusBanner status={overview.dataStatus} /> : null}

        <p className="metric-status" role="status" aria-live="polite">
          {metricsLoading
            ? 'Loading current market metrics from the API…'
            : metricsRefreshing
              ? 'Refreshing current market metrics; the previous successful response is still shown and marked.'
              : historyQuery.isLoading
                ? 'Loading retained historical snapshots…'
                : historyQuery.isFetching
                  ? 'Refreshing retained historical snapshots…'
                  : ''}
        </p>

        <section className="stats-grid" aria-busy={metricsLoading}>
          <StatCard
            icon={Building2}
            label={showUniqueVacancies ? 'Unique vacancies (all active listings, as of now)' : 'Active tracked listings (as of now)'}
            value={
              metricsLoading
                ? 'Loading…'
                : jobsUnavailable
                  ? 'Unavailable'
                  : showUniqueVacancies
                    ? overview!.uniqueVacancies!.toLocaleString('en-NZ')
                    : overview
                      ? cohort.length.toLocaleString('en-NZ')
                      : 'Unavailable'
            }
            detail={
              metricsLoading
                ? 'Waiting for the active-listing and overview responses before showing a count.'
                : jobsUnavailable
                  ? 'The active-listing feed failed to load. This is a loading failure, not zero listings.'
                  : showUniqueVacancies
                    ? `${deduplication!.activeListings.toLocaleString('en-NZ')} active listings · ${deduplication!.listingsInGroups.toLocaleString('en-NZ')} listings in ${deduplication!.groups} vacancy group${deduplication!.groups === 1 ? '' : 's'} · ${deduplication!.listingsMerged} duplicate row${deduplication!.listingsMerged === 1 ? '' : 's'} removed · ${deduplicationStatus}`
                    : activeChange
                      ? `As of now · ${describeChange(activeChange)} · ${deduplicationStatus}`
                      : filteredScope
                        ? `As of now · comparison unavailable for a filtered cohort · ${deduplicationStatus}`
                        : `As of now · comparison needs daily snapshots · ${deduplicationStatus}`
            }
          />
          <StatCard
            icon={CalendarPlus}
            label={`New listings (${filters.period}d, first seen)`}
            value={newListings.current == null ? (newListings.status === 'loading' ? 'Loading…' : 'Unavailable') : newListings.current.toLocaleString('en-NZ')}
            detail={
              newListings.status === 'loading'
                ? 'Loading retained history…'
                : newListings.status === 'error'
                  ? newListings.note
                  : newListings.status === 'unavailable'
                    ? `First seen · ${newListings.note || 'current period is not fully retained'}`
                    : `First seen · ${newListings.comparisonAvailable && newListings.change ? describeChange(newListings.change) : newListings.note || 'comparison unavailable'}`
            }
          />
          <StatCard
            icon={WalletCards}
            label="Median advertised salary (active cohort)"
            value={
              metricsLoading
                ? 'Loading…'
                : jobsUnavailable
                  ? 'Unavailable'
                  : salary.suppressed
                    ? 'Not enough data'
                    : formatCurrency(salary.median)
            }
            detail={
              metricsLoading
                ? 'Waiting for the active cohort before computing salary coverage.'
                : jobsUnavailable
                  ? 'The active-listing feed failed to load; this is not zero salary data.'
                  : `${salary.sampleSize} comparable annual NZD listings of ${cohort.length}${salary.smallSample ? ' · small sample' : ''}`
            }
          />
          <StatCard
            icon={Percent}
            label="Hiring employers (active cohort)"
            value={
              metricsLoading
                ? 'Loading…'
                : jobsUnavailable
                  ? 'Unavailable'
                  : employers.distinctEmployers.toLocaleString('en-NZ')
            }
            detail={
              metricsLoading
                ? 'Waiting for the active cohort before counting employers.'
                : jobsUnavailable
                  ? 'The active-listing feed failed to load; this is not zero employers.'
                  : `${employers.disclosedListings} listings name an employer · ${employers.agencyOnlyListings} agency-only · ${employers.unknownListings} unknown`
            }
          />
        </section>

        <section className="panel count-reconciliation" aria-label="Headline count definitions and as-of context">
          <div className="panel-header">
            <div>
              <h2>Which count is which</h2>
              <p>
                These are three different units and capture times. They are not expected to be equal, and none is
                adjusted to match another.
              </p>
            </div>
          </div>
          <dl>
            <div>
              <dt>Unique vacancies</dt>
              <dd>
                {metricsLoading
                  ? 'Loading…'
                  : showUniqueVacancies
                    ? `${overview!.uniqueVacancies!.toLocaleString('en-NZ')} validated vacancy groups in the active population`
                    : `Unavailable — ${deduplicationStatus}`}
                <small>
                  {metricsLoading
                    ? 'Waiting for the overview and validated deduplication metadata.'
                    : showUniqueVacancies
                      ? `Active listing rows minus duplicate rows inside validated groups. ${deduplicationStatus}.`
                      : 'No validated deduplication grouping is available for this capture. No zero is inferred.'}
                </small>
              </dd>
            </div>
            <div>
              <dt>Active tracked listings</dt>
              <dd>
                {metricsLoading
                  ? 'Loading…'
                  : jobsUnavailable
                    ? 'Unavailable'
                    : overview
                      ? `${overview.activeListings.toLocaleString('en-NZ')} source rows`
                      : 'Unavailable'}
                <small>
                  {metricsLoading
                    ? 'Waiting for the live overview.'
                    : jobsUnavailable
                      ? 'The active-listing feed failed to load; this is not zero listings.'
                      : overview
                        ? `As of ${formatNzDateTime(overview.generatedAtUtc)}; one advertisement per source, before grouping.`
                        : 'The live overview is unavailable.'}
                </small>
              </dd>
            </div>
            <div>
              <dt>Historical active listings</dt>
              <dd>
                {historyQuery.isLoading
                  ? 'Loading…'
                  : historyQuery.isError
                    ? 'Unavailable'
                    : history?.latestSnapshotDate
                      ? `${history.latestActiveCount.toLocaleString('en-NZ')} listings on the ${history.latestSnapshotDate} snapshot`
                      : 'Unavailable'}
                <small>
                  {historyQuery.isLoading
                    ? 'Loading retained snapshots…'
                    : historyQuery.isError
                      ? 'Retained snapshots could not be loaded; this is not zero historical activity.'
                      : history?.latestSnapshotDate
                        ? 'Retained daily snapshot as of that Pacific/Auckland capture date; it can differ from the live count because the snapshot is frozen.'
                        : 'No retained snapshot is available for this period.'}
                </small>
              </dd>
            </div>
          </dl>
        </section>

        <section className="panel quality-panel" id="quality" aria-labelledby="quality-heading">
          <div className="panel-header">
            <div>
              <h2 id="quality-heading">Data quality summary</h2>
              <p>
                Public coverage and completeness measures for the tracked dataset. Every percentage shows its
                numerator and denominator; no operational crawl detail is exposed here.
              </p>
            </div>
          </div>
          {qualityQuery.isError ? (
            <div className="empty-state error-state" role="alert">
              The public quality summary could not be loaded. This is a request failure, not evidence of complete
              data.
            </div>
          ) : qualityQuery.isLoading ? (
            <div className="chart-empty" aria-busy="true">
              Loading public quality measures…
            </div>
          ) : !qualityQuery.data ? (
            <div className="chart-empty">No public quality summary is available for this capture.</div>
          ) : (
            <>
              <div className="table-scroll">
                <table className="role-salary-table quality-table">
                  <caption>
                    {qualityQuery.data.population} Captured {formatNzDateTime(qualityQuery.data.generatedAtUtc)}.
                  </caption>
                  <thead>
                    <tr>
                      <th scope="col">Measure</th>
                      <th scope="col">Result</th>
                      <th scope="col">Numerator / denominator</th>
                      <th scope="col">Scope</th>
                      <th scope="col">Captured</th>
                    </tr>
                  </thead>
                  <tbody>
                    {qualityQuery.data.measures.map((measure) => (
                      <tr key={measure.key}>
                        <th scope="row">{measure.label}</th>
                        <td>{measure.available ? measure.displayValue : `Unavailable — ${measure.unavailableReason ?? 'no denominator'}`}</td>
                        <td>
                          {measure.numerator == null || measure.denominator == null
                            ? 'Unavailable'
                            : `${measure.numerator.toLocaleString('en-NZ')} / ${measure.denominator.toLocaleString('en-NZ')}`}
                        </td>
                        <td>{measure.scope}</td>
                        <td>{formatNzDateTime(measure.capturedAtUtc)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <details>
                <summary>Measure definitions</summary>
                <dl className="quality-definitions">
                  {qualityQuery.data.measures.map((measure) => (
                    <div key={measure.key}>
                      <dt>{measure.label}</dt>
                      <dd>{measure.definition}</dd>
                    </div>
                  ))}
                </dl>
              </details>
              <div className="quality-classification">
                <h3>Classification accuracy (reviewed sample)</h3>
                {qualityQuery.data.classification.available ? (
                  <>
                    <p>
                      <strong>{qualityQuery.data.classification.accuracyPercent?.toFixed(1)}%</strong> correct on{' '}
                      {qualityQuery.data.classification.accuracyDenominator.toLocaleString('en-NZ')} reviewed
                      role-family decisions (excluding ambiguous and unknown).{' '}
                      {qualityQuery.data.classification.reviewedListings.toLocaleString('en-NZ')} of{' '}
                      {qualityQuery.data.classification.sampledListings.toLocaleString('en-NZ')} sampled listings were
                      reviewed with rule version {qualityQuery.data.classification.ruleVersion}.
                    </p>
                    <p className="muted">
                      {qualityQuery.data.classification.scope} Method: {qualityQuery.data.classification.method}.
                      Evaluated {formatNzDateTime(qualityQuery.data.classification.evaluatedAtUtc)}.
                    </p>
                    <p className="muted">
                      Correct {qualityQuery.data.classification.correct} · false positive{' '}
                      {qualityQuery.data.classification.falsePositive} · false negative{' '}
                      {qualityQuery.data.classification.falseNegative} · ambiguous{' '}
                      {qualityQuery.data.classification.ambiguous} · unknown{' '}
                      {qualityQuery.data.classification.unknown}.
                    </p>
                  </>
                ) : (
                  <p className="muted">
                    Not published — {qualityQuery.data.classification.unavailableReason ?? 'no reviewed sample'}.
                  </p>
                )}
              </div>
              <p className="muted chart-caption">{qualityQuery.data.methodologyNote}</p>
            </>
          )}
        </section>

        <DataRoleExplorer
          jobs={allJobs}
          filters={filters}
          onChange={(name, value) => setFilter(name, value)}
          onClear={clearExplorerFilters}
        />

        <section className="panel trend-panel" id="weekly-trend">
          <div className="panel-header">
            <div>
              <h2>Weekly new listings (first seen)</h2>
              <p>
                Unique listings by first-seen week from retained daily snapshots (Pacific/Auckland weeks). This panel
                always shows every retained week; the reporting-period selector controls the new-listings card and
                comparison, not this chart. Incomplete weeks are labelled and never compared as if complete.
              </p>
            </div>
            <CalendarPlus aria-hidden="true" />
          </div>
          {historyQuery.isError ? (
            <div className="empty-state error-state" role="alert">
              Historical snapshots could not be loaded. This is not evidence of zero new listings.
            </div>
          ) : historyQuery.isLoading ? (
            <div className="chart-empty" aria-busy="true">
              Loading retained history…
            </div>
          ) : (
            <Suspense fallback={chartFallback}>
              <WeeklyTrendChart points={history?.weeklyNewListings ?? []} />
            </Suspense>
          )}
          <p className="muted chart-caption">
            {historyQuery.isLoading
              ? 'Loading snapshot coverage and retained history…'
              : historyQuery.isError
                ? 'Snapshot coverage could not be loaded. This is a request failure, not an absence of history.'
                : coverage?.hasHistory
                  ? `Snapshot coverage ${coverage.firstDate} to ${coverage.lastDate} (${coverage.snapshotDays} daily snapshots${
                      missingSnapshotDays > 0 ? `, ${missingSnapshotDays} missing day${missingSnapshotDays === 1 ? '' : 's'}` : ''
                    }). ${history?.scopeNote ?? ''} ${newListings.dateBasis}. ${newListings.note}`
                  : coverage?.note ?? 'No snapshots retained yet; history will appear as daily captures accumulate.'}
          </p>
        </section>

        <section className="panel trend-panel" id="active-trend">
          <div className="panel-header">
            <div>
              <h2>Active listings over time (snapshot stock)</h2>
              <p>
                Historical active counts from retained daily snapshots, with source-coverage changes annotated so an
                added source is never read as demand growth. The seven-day active change always compares the latest
                snapshot with the snapshot seven days earlier; the reporting-period selector does not move it. The line
                follows the selected cohort; the source-coverage annotations describe the whole collection.
              </p>
              <p className="muted chart-caption">{history?.scopeNote}</p>
            </div>
          </div>
          {historyQuery.isError ? (
            <div className="empty-state error-state" role="alert">
              Historical snapshots could not be loaded. This is not evidence of zero active listings.
            </div>
          ) : historyQuery.isLoading ? (
            <div className="chart-empty" aria-busy="true">
              Loading retained history…
            </div>
          ) : (
            <Suspense fallback={chartFallback}>
              <ActiveTrendChart points={history?.activeTrend ?? []} coverageChanges={history?.coverageChanges ?? []} />
            </Suspense>
          )}
        </section>

        <section className="panel trend-panel" id="constant-source" aria-label="Constant-source comparison">
          <div className="panel-header">
            <div>
              <h2>Constant-source comparison</h2>
              <p>
                Compares the latest snapshot with the snapshot seven days earlier using only sources that were observed
                on every day in between. Raw totals stay visible beside it. This collection panel uses all tracked
                listings and is not narrowed by the cohort filter above.
              </p>
            </div>
          </div>
          {historyQuery.isError ? (
            <div className="empty-state error-state" role="alert">
              Constant-source coverage could not be loaded. This is not evidence of zero source coverage.
            </div>
          ) : historyQuery.isLoading ? (
            <div className="chart-empty" aria-busy="true">
              Loading constant-source coverage…
            </div>
          ) : !constantComparison?.available ? (
            <div className="chart-empty">
              <strong>Constant-source comparison unavailable.</strong>{' '}
              {constantComparison?.reason ?? 'No comparable source observations are retained for this window.'}
            </div>
          ) : (
            <div className="trend-stack">
              <div className="constant-source-grid">
                <div>
                  <span>Constant-source active change</span>
                  <strong>{constantComparison.change ? describeChange(constantComparison.change) : 'Unavailable'}</strong>
                  <small>
                    {constantComparison.fromActiveCount?.toLocaleString('en-NZ')} →{' '}
                    {constantComparison.toActiveCount?.toLocaleString('en-NZ')} active listings,{' '}
                    {constantComparison.eligibleSourceCount} of {constantComparison.observedSourceCount} sources eligible
                  </small>
                </div>
                <div>
                  <span>Raw active change (all sources)</span>
                  <strong>{constantComparison.rawChange ? describeChange(constantComparison.rawChange) : 'Unavailable'}</strong>
                  <small>
                    {constantComparison.rawFromActiveCount?.toLocaleString('en-NZ')} →{' '}
                    {constantComparison.rawToActiveCount?.toLocaleString('en-NZ')} active listings across every observed
                    source
                  </small>
                </div>
                <div>
                  <span>Collection-associated residual</span>
                  <strong>
                    {constantComparison.collectionAssociatedResidual == null
                      ? 'Unavailable'
                      : `${constantComparison.collectionAssociatedResidual > 0 ? '+' : ''}${constantComparison.collectionAssociatedResidual.toLocaleString('en-NZ')}`}
                  </strong>
                  <small>
                    Raw total change minus the constant-source change. This is an explicit residual that includes source
                    mix and collection effects; it is not a measure of organic demand.
                  </small>
                </div>
              </div>
              <div className="table-scroll">
                <table className="constant-source-table">
                  <caption>Constant-source decomposition for {constantComparison.fromSnapshotDate} to {constantComparison.toSnapshotDate} (Pacific/Auckland snapshot dates)</caption>
                  <thead>
                    <tr>
                      <th scope="col">Source set</th>
                      <th scope="col">From {constantComparison.fromSnapshotDate}</th>
                      <th scope="col">To {constantComparison.toSnapshotDate}</th>
                      <th scope="col">Absolute change</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <th scope="row">Constant sources only</th>
                      <td>{constantComparison.fromActiveCount?.toLocaleString('en-NZ')}</td>
                      <td>{constantComparison.toActiveCount?.toLocaleString('en-NZ')}</td>
                      <td>{constantComparison.change?.absoluteChange ?? '—'}</td>
                    </tr>
                    <tr>
                      <th scope="row">All observed sources (raw)</th>
                      <td>{constantComparison.rawFromActiveCount?.toLocaleString('en-NZ')}</td>
                      <td>{constantComparison.rawToActiveCount?.toLocaleString('en-NZ')}</td>
                      <td>{constantComparison.rawChange?.absoluteChange ?? '—'}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
              <p className="muted chart-caption">{constantComparison.eligibilityRule}</p>
              <details>
                <summary>Eligible sources ({constantComparison.sources.length})</summary>
                <p className="muted">{constantComparison.sources.join(', ')}</p>
              </details>
            </div>
          )}
        </section>

        <section className="chart-grid">
          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>Demand by role family</h2>
                <p>Share of the current active cohort. Role family is inferred from the title.</p>
              </div>
            </div>
            <RankedBarList
              ariaLabel="Demand by role family"
              items={families.map((family) => ({ label: family.label, count: family.count, share: family.share }))}
              selected={filters.roleFamily}
              onSelect={(label) => setFilter('roleFamily', filters.roleFamily === label ? '' : label)}
              selectLabel="Filter by role family"
            />
            <p className="muted chart-caption">
              Active now · denominator {familyCohort.length.toLocaleString('en-NZ')} active listings matching the other
              filters. Selecting a family narrows the rest of the page but not this list.
            </p>
            {selectedFamilyRow ? (
              <div className="drilldown">
                <h3>Seniority within {selectedFamilyRow.label}</h3>
                <RankedBarList
                  ariaLabel={`Seniority within ${selectedFamilyRow.label}`}
                  items={selectedFamilyRow.bySeniority.map((row) => ({
                    label: row.label,
                    count: row.count,
                    share: row.share,
                  }))}
                  countLabel="listings"
                />
              </div>
            ) : null}
          </section>

          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>Regional demand</h2>
                <p>
                  Each listing counts once under its primary normalised region; nationwide and multi-location roles are
                  reported separately rather than double-counted.
                </p>
              </div>
            </div>
            <RankedBarList
              ariaLabel="Demand by region"
              items={regions.bars.map((bar) => ({ label: bar.label, count: bar.count, share: bar.share }))}
              selected={filters.region}
              onSelect={(label) => setFilter('region', filters.region === label ? '' : label)}
              selectLabel="Filter by region"
            />
            <p className="muted chart-caption">
              Active now · denominator {regionCohort.length.toLocaleString('en-NZ')} active listings matching the other
              filters. {regions.nationwide} nationwide · {regions.multiLocation} multi-location listings (counted once
              under their primary region).
            </p>
          </section>

          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>Skill demand</h2>
                <p>
                  Each skill counts at most once per listing; percentage denominator is the displayed cohort
                  ({skills.cohort} listings), so percentages can sum above 100%.
                </p>
              </div>
            </div>
            <RankedBarList
              ariaLabel="Demand by skill"
              items={skills.bars.map((bar) => ({ label: bar.label, count: bar.count, share: bar.share }))}
              selected={filters.skill}
              onSelect={(label) => setFilter('skill', filters.skill === label ? '' : label)}
              selectLabel="Filter by skill"
            />
            <p className="muted chart-caption">
              Skill extraction coverage: {skills.extractionCoverage}% of this cohort has at least one extracted skill.
            </p>
          </section>

          <section className="panel" id="top-languages">
            <div className="panel-header">
              <div>
                <h2>Top programming languages</h2>
                <p>
                  Share of active tracked listings mentioning each programming language. Descriptive only: this is not a
                  forecast, ranking of candidates, or employability score. Each skill counts at most once per listing.
                </p>
              </div>
            </div>
            <Suspense fallback={chartFallback}>
              <SkillDemandChart
                title="Top programming languages"
                type="ProgrammingLanguage"
                data={skillDemandQuery.data}
                loading={skillDemandQuery.isLoading}
                error={skillDemandQuery.isError}
              />
            </Suspense>
          </section>

          <section className="panel" id="top-frameworks">
            <div className="panel-header">
              <div>
                <h2>Top frameworks</h2>
                <p>
                  Share of active tracked listings mentioning each framework. Descriptive only: this is not a forecast,
                  ranking of candidates, or employability score. Each skill counts at most once per listing.
                </p>
              </div>
            </div>
            <Suspense fallback={chartFallback}>
              <SkillDemandChart
                title="Top frameworks"
                type="Framework"
                data={skillDemandQuery.data}
                loading={skillDemandQuery.isLoading}
                error={skillDemandQuery.isError}
                note="Includes markup/styling skills (HTML, CSS)."
              />
            </Suspense>
          </section>

          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>Salary distribution</h2>
                <p>
                  Comparable permanent annual NZD midpoints only. Groups below five observations are suppressed; 5–9 are
                  flagged as small samples.
                </p>
              </div>
            </div>
            <Suspense fallback={chartFallback}>
              <SalaryDistributionChart distribution={salary} />
            </Suspense>
            <p className="muted chart-caption">
              Active now · sample {salary.sampleSize.toLocaleString('en-NZ')} comparable annual NZD listings out of{' '}
              {salary.cohortCount.toLocaleString('en-NZ')} active listings in this cohort. Missing and incomparable
              salaries are excluded, never treated as zero.
            </p>
          </section>

          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>Median salary by role family</h2>
                <p>
                  Comparable permanent annual NZD midpoints. Families with fewer than five samples are suppressed;
                  5–9 are flagged.
                </p>
              </div>
            </div>
            {roleSalaries.length === 0 ? (
              <div className="chart-empty">
                No role family has five or more comparable annual NZD salaries in this cohort.
              </div>
            ) : (
              <table className="role-salary-table">
                <thead>
                  <tr>
                    <th scope="col">Role family</th>
                    <th scope="col">Median (NZD/year)</th>
                    <th scope="col">Sample</th>
                  </tr>
                </thead>
                <tbody>
                  {roleSalaries.map((row) => (
                    <tr key={row.family}>
                      <th scope="row">{row.family}</th>
                      <td>{formatCurrency(row.median)}</td>
                      <td>
                        {row.sampleSize}
                        {row.smallSample ? ' · small sample' : ''}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <p className="muted chart-caption">
              Active now · denominator {cohort.length.toLocaleString('en-NZ')} active listings in this cohort. Families
              with fewer than five comparable annual NZD samples are omitted, not shown as zero.
            </p>
          </section>

          <section className="panel">
            <div className="panel-header">
              <div>
                <h2>Employer activity</h2>
                <p>
                  Direct employers and agencies are separated; agency-only and unknown-employer listings are reported
                  separately and the agency's client is never inferred.
                </p>
              </div>
            </div>
            <RankedBarList
              ariaLabel="Employer activity"
              items={employers.rows.map((row) => ({ label: row.name, count: row.count }))}
              countLabel="listings"
              emptyMessage="No employer is named in this cohort."
            />
            <p className="muted chart-caption">
              Active now · denominator {cohort.length.toLocaleString('en-NZ')} active listings in this cohort.{' '}
              {employers.disclosedListings} listings disclose an employer · {employers.agencyOnlyListings} agency-only ·{' '}
              {employers.unknownListings} unknown.
              {overview
                ? ` ${overview.employers.canonicalEmployers} canonical employers tracked (${overview.employers.agencyCanonicals} agency, ${overview.employers.directEmployerCanonicals} direct), ${overview.employers.unmatchedEmployerNames} unmatched name(s).`
                : ''}
            </p>
          </section>
        </section>

        <section className="panel methodology">
          <div className="panel-header">
            <div>
              <h2>Methodology and data quality</h2>
              <p>Definitions behind every number on this page.</p>
            </div>
          </div>
          <details>
            <summary>Show methodology, coverage and known limitations</summary>
            <div className="methodology-body">
              <h3>Definitions</h3>
              <ul>
                <li>
                  A <strong>listing</strong> is one advertisement captured from one source. When a validated
                  deduplication pass has run, the headline metric switches to <strong>unique vacancies</strong>:
                  cross-source listings are merged only on strong evidence (identical title, canonical employer and
                  salary/window, or a shared location with ≥80% description similarity), capped at four listings per
                  vacancy. Source rows are never deleted; weaker title+employer matches are queued for human review.
                </li>
                <li>
                  <strong>New listings</strong> use the crawler first-seen timestamp (discovery), not a substituted
                  posted date. <strong>Active tracked listings</strong> is an as-of count of rows still active.
                </li>
                <li>
                  <strong>Reporting period</strong> is the last {filters.period} complete Pacific/Auckland calendar days,
                  ending at the most recent local midnight; the in-progress local day is excluded. The new-listings card,
                  its comparison and the window label follow this selector. Weekly and active-trend panels use their own
                  stated snapshot ranges.
                </li>
                <li>
                  <strong>First seen</strong> is a flow (listings discovered inside a window) and <strong>active
                  listings</strong> is a stock (rows active at a capture time). The two populations are labelled
                  separately and are not expected to match.
                </li>
                <li>
                  <strong>Median advertised salary</strong> is the midpoint of comparable permanent annual NZD
                  advertisements only; hourly/daily contract rates and foreign currencies are shown separately and never
                  annualised or mixed in.
                </li>
                <li>
                  <strong>Hiring employers</strong> counts distinct identifiable employers — an admin-reviewed
                  canonical where an exact or curated-alias match exists, otherwise the exact normalised advertised
                  name — and never fuzzy-merges names. Agency-only listings and unknown employers are reported
                  separately.
                </li>
                <li>
                  <strong>Geography</strong>: city, region, nationwide and work mode are independent. Remote is a work
                  mode; a listing whose only place is remote is bucketed under “Remote” in regional demand, never as a
                  city or region. Multi-location listings are counted once under their primary region.
                </li>
                <li>
                  <strong>Role family</strong> is an inferred, deterministic title classification; the original title is
                  always retained.
                </li>
              </ul>
              <h3>Data quality</h3>
              <ul>
                <li>
                  Sources refreshed in the window: {overview?.dataStatus.enabledSourcesWithRecentSuccess ?? '—'} of{' '}
                  {overview?.dataStatus.enabledSources ?? '—'} enabled.
                </li>
                <li>
                  Posted date known for {overview?.postedDateKnown ?? '—'} of {overview?.activeListings ?? '—'} active
                  listings; the rest are labelled “First seen”.
                </li>
                <li>
                  Snapshot history: {coverage?.hasHistory ? `${coverage.firstDate} to ${coverage.lastDate} (${coverage.snapshotDays} daily snapshots)` : 'not yet retained'}
                  {missingSnapshotDays > 0 ? `; ${missingSnapshotDays} missing day(s)` : ''}.
                </li>
                <li>
                  Historical active counts compare the latest snapshot with the snapshot exactly seven days earlier when
                  it exists; otherwise the comparison is declared unavailable. The seven-day change is not moved by the
                  reporting-period selector.
                </li>
                <li>
                  Historical panels follow the selected region, role family and seniority cohort when snapshots retain
                  those fields. Skills are not stored in snapshots, so a skill-filtered historical comparison is declared
                  unavailable rather than substituted.
                </li>
              </ul>
              {employers.agencyOnlyListings > 0 ? (
                <p className="muted">
                  Note: {employers.agencyOnlyListings} agency-advertised listings in this cohort do not name a hiring
                  employer. Similar employer names are not merged automatically.
                </p>
              ) : null}
            </div>
          </details>
        </section>
      </main>
    </div>
  )
}
