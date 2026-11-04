import { lazy, Suspense, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowRight, Building2, CalendarPlus, Percent, WalletCards } from 'lucide-react'
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
  type TrendsFilterState,
} from '../lib/urlState'
import { getHistory, getJobs, getOverview } from '../services/api'
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

  const jobsQuery = useQuery({ queryKey: ['jobs'], queryFn: () => getJobs({ status: 'all', limit: 5000 }) })
  const overviewQuery = useQuery({ queryKey: ['overview'], queryFn: getOverview })

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

  const clearFilters = () => navigate('/trends')

  const matchingJobsHref = buildJobsHref({
    location: filters.region,
    roleFamily: filters.roleFamily,
    skill: filters.skill,
    seniority: filters.seniority,
  })

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
            <strong>{overview ? formatNzDateTime(overview.dataStatus.lastSuccessfulUpdateUtc) : 'unavailable'}</strong>
          </div>
          {filteredScope ? (
            <button type="button" className="clear-all" onClick={clearFilters}>
              Clear filters
            </button>
          ) : null}
        </section>

        {overviewQuery.isError || jobsQuery.isError ? (
          <section className="empty-state error-state" role="alert">
            Market metrics could not be loaded, so no counts are shown. This is a data-loading failure, not zero demand.
          </section>
        ) : null}

        {overview ? <DataStatusBanner status={overview.dataStatus} /> : null}

        <section className="stats-grid" aria-busy={overviewQuery.isLoading || jobsQuery.isLoading}>
          <StatCard
            icon={Building2}
            label={showUniqueVacancies ? 'Unique vacancies (all active listings, as of now)' : 'Active tracked listings (as of now)'}
            value={
              showUniqueVacancies
                ? overview!.uniqueVacancies!.toLocaleString('en-NZ')
                : overview
                  ? cohort.length.toLocaleString('en-NZ')
                  : 'Unavailable'
            }
            detail={
              showUniqueVacancies
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
            label={`New listings (${filters.period}d)`}
            value={newListings.current == null ? 'Unavailable' : newListings.current.toLocaleString('en-NZ')}
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
            label="Median advertised salary"
            value={salary.suppressed ? 'Not enough data' : formatCurrency(salary.median)}
            detail={`${salary.sampleSize} comparable annual NZD listings of ${cohort.length}${salary.smallSample ? ' · small sample' : ''}`}
          />
          <StatCard
            icon={Percent}
            label="Hiring employers"
            value={employers.distinctEmployers.toLocaleString('en-NZ')}
            detail={`${employers.disclosedListings} listings name an employer · ${employers.agencyOnlyListings} agency-only · ${employers.unknownListings} unknown`}
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
                {showUniqueVacancies
                  ? `${overview!.uniqueVacancies!.toLocaleString('en-NZ')} validated vacancy groups in the active population`
                  : `Unavailable — ${deduplicationStatus}`}
                <small>
                  {showUniqueVacancies
                    ? `Active listing rows minus duplicate rows inside validated groups. ${deduplicationStatus}.`
                    : 'No validated deduplication grouping is available for this capture.'}
                </small>
              </dd>
            </div>
            <div>
              <dt>Active tracked listings</dt>
              <dd>
                {overview ? `${overview.activeListings.toLocaleString('en-NZ')} source rows` : 'Unavailable'}
                <small>
                  {overview
                    ? `As of ${formatNzDateTime(overview.generatedAtUtc)}; one advertisement per source, before grouping.`
                    : 'The live overview has not loaded.'}
                </small>
              </dd>
            </div>
            <div>
              <dt>Historical active listings</dt>
              <dd>
                {history?.latestSnapshotDate
                  ? `${history.latestActiveCount.toLocaleString('en-NZ')} listings on the ${history.latestSnapshotDate} snapshot`
                  : 'Unavailable'}
                <small>
                  {history?.latestSnapshotDate
                    ? 'Retained daily snapshot as of that Pacific/Auckland capture date; it can differ from the live count because the snapshot is frozen.'
                    : 'No retained snapshot is available for this period.'}
                </small>
              </dd>
            </div>
          </dl>
        </section>

        <section className="panel trend-panel" id="weekly-trend">
          <div className="panel-header">
            <div>
              <h2>Weekly new listings</h2>
              <p>
                Unique listings by first-seen week from retained daily snapshots (Pacific/Auckland weeks). Incomplete
                weeks are labelled and never compared as if complete.
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
            {coverage?.hasHistory
              ? `Snapshot coverage ${coverage.firstDate} to ${coverage.lastDate} (${coverage.snapshotDays} daily snapshots${
                  missingSnapshotDays > 0 ? `, ${missingSnapshotDays} missing day${missingSnapshotDays === 1 ? '' : 's'}` : ''
                }). ${newListings.dateBasis}. ${newListings.note}`
              : coverage?.note ?? 'No snapshots retained yet; history will appear as daily captures accumulate.'}
          </p>
        </section>

        <section className="panel trend-panel" id="active-trend">
          <div className="panel-header">
            <div>
              <h2>Active listings over time</h2>
              <p>
                Historical active counts from retained daily snapshots, with source-coverage changes annotated so an
                added source is never read as demand growth.
              </p>
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
                on every day in between. Raw totals stay visible beside it.
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
              {regions.nationwide} nationwide · {regions.multiLocation} multi-location listings (counted once under their
              primary region)
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
                  posted date. <strong>Active tracked listings</strong> is a as-of count of rows still active.
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
                  it exists; otherwise the comparison is declared unavailable.
                </li>
                <li>
                  Source-coverage changes are annotated on the active trend so added sources cannot silently look like
                  demand growth.
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
