import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowRight, GitCompareArrows } from 'lucide-react'
import { AppSidebar } from '../components/layout/AppSidebar'
import { DataStatusBanner } from '../components/dashboard/DataStatusBanner'
import { ViewHeader } from '../components/layout/ViewHeader'
import { ComparisonBars } from '../components/compare/ComparisonBars'
import { describeChange } from '../lib/change'
import { DATA_SUB_ROLE_FAMILY, DATA_SUB_ROLE_OPTIONS } from '../lib/dataRoles'
import { formatCurrency, formatNzDateTime } from '../lib/format'
import { navigate, useAppLocation } from '../hooks/useAppLocation'
import {
  buildJobsHref,
  parseComparisonState,
  serializeComparisonState,
  setComparisonFilter,
  type ComparisonState,
} from '../lib/urlState'
import { getComparison, getJobs, getOverview } from '../services/api'
import type { Change, ComparisonCohort, SalarySummary } from '../types/api'

function unique(values: Array<string | null | undefined>) {
  return [...new Set(values.filter((value): value is string => Boolean(value) && value !== 'Unknown'))].sort()
}

function periodLabel(change: Change | null): string {
  return change ? describeChange(change) : 'Unavailable'
}

function salaryLabel(salary: SalarySummary): string {
  if (salary.suppressed) return 'Suppressed (<5 comparable samples)'
  const median = formatCurrency(salary.medianAnnualNzd)
  return salary.smallSample ? `${median} · small sample (5–9)` : median
}

function cohortHref(state: ComparisonState, key: 'a' | 'b') {
  return buildJobsHref({
    roleFamily: DATA_SUB_ROLE_FAMILY,
    dataSubRole: state[`${key}Role`],
    location: state[`${key}Region`],
    seniority: state[`${key}Seniority`],
    workMode: state[`${key}WorkMode`],
    skill: state[`${key}Skill`],
  })
}

/**
 * Round 22 (C2): one two-cohort comparison workflow. Both cohorts use the same
 * reporting window and the same active capture. Descriptions are deliberately
 * descriptive: salary is an advertised median, not a causal premium; skill
 * differences are percentage points, and count changes need a real baseline.
 */
export function CompareCohortsPage() {
  const { search } = useAppLocation()
  const state = useMemo(() => parseComparisonState(search), [search])
  const jobsQuery = useQuery({ queryKey: ['jobs'], queryFn: () => getJobs({ status: 'all', limit: 5000 }) })
  const overviewQuery = useQuery({ queryKey: ['overview'], queryFn: getOverview })

  const options = useMemo(() => {
    const family = (jobsQuery.data ?? []).filter((job) => job.isActive && job.roleFamily === DATA_SUB_ROLE_FAMILY)
    return {
      regions: unique(family.map((job) => job.region ?? job.location)),
      seniorities: unique(family.map((job) => job.seniority)),
      workModes: unique(family.map((job) => job.workMode)),
      skills: unique(family.flatMap((job) => job.skills)),
    }
  }, [jobsQuery.data])

  const comparisonQuery = useQuery({
    queryKey: ['comparison', state],
    queryFn: () =>
      getComparison({
        days: state.days,
        a: {
          roleFamily: DATA_SUB_ROLE_FAMILY,
          dataSubRole: state.aRole || undefined,
          region: state.aRegion || undefined,
          seniority: state.aSeniority || undefined,
          workMode: state.aWorkMode || undefined,
          skill: state.aSkill || undefined,
        },
        b: {
          roleFamily: DATA_SUB_ROLE_FAMILY,
          dataSubRole: state.bRole || undefined,
          region: state.bRegion || undefined,
          seniority: state.bSeniority || undefined,
          workMode: state.bWorkMode || undefined,
          skill: state.bSkill || undefined,
        },
      }),
  })

  const comparison = comparisonQuery.data
  const update = (name: keyof ComparisonState, value: string) => {
    const next = setComparisonFilter(state, name, value)
    const query = serializeComparisonState(next)
    navigate(query ? `/compare?${query}` : '/compare')
  }
  const reset = () => {
    const next = parseComparisonState('')
    const query = serializeComparisonState(next)
    navigate(query ? `/compare?${query}` : '/compare')
  }
  const swap = () => {
    const next: ComparisonState = {
      ...state,
      aRole: state.bRole,
      aRegion: state.bRegion,
      aSeniority: state.bSeniority,
      aWorkMode: state.bWorkMode,
      aSkill: state.bSkill,
      bRole: state.aRole,
      bRegion: state.aRegion,
      bSeniority: state.aSeniority,
      bWorkMode: state.aWorkMode,
      bSkill: state.aSkill,
    }
    const query = serializeComparisonState(next)
    navigate(query ? `/compare?${query}` : '/compare')
  }

  const aHref = cohortHref(state, 'a')
  const bHref = cohortHref(state, 'b')
  const notes = comparison?.notes ?? []
  const loading = comparisonQuery.isLoading
  const failed = comparisonQuery.isError

  const renderControls = (key: 'a' | 'b', cohort: 'Cohort A' | 'Cohort B') => {
    const roleName = `${key}Role` as const
    const regionName = `${key}Region` as const
    const seniorityName = `${key}Seniority` as const
    const workModeName = `${key}WorkMode` as const
    const skillName = `${key}Skill` as const

    return (
      <section className="compare-cohort-card" aria-label={`${cohort} definition`}>
        <h3>{cohort}</h3>
        <p>All filters are optional; the cohort is always scoped to the Data &amp; Analytics family.</p>
        <div className="compare-cohort-fields">
          <label>
            <span>Sub-role</span>
            <select value={state[roleName]} onChange={(event) => update(roleName, event.target.value)}>
              <option value="">All sub-roles</option>
              {DATA_SUB_ROLE_OPTIONS.map((option) => (
                <option key={option.key} value={option.key}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Region</span>
            <select value={state[regionName]} onChange={(event) => update(regionName, event.target.value)}>
              <option value="">All regions</option>
              {options.regions.map((region) => (
                <option key={region} value={region}>
                  {region}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Seniority</span>
            <select value={state[seniorityName]} onChange={(event) => update(seniorityName, event.target.value)}>
              <option value="">All seniority</option>
              {options.seniorities.map((seniority) => (
                <option key={seniority} value={seniority}>
                  {seniority}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Work mode</span>
            <select value={state[workModeName]} onChange={(event) => update(workModeName, event.target.value)}>
              <option value="">All work modes</option>
              {options.workModes.map((mode) => (
                <option key={mode} value={mode}>
                  {mode}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Skill</span>
            <select value={state[skillName]} onChange={(event) => update(skillName, event.target.value)}>
              <option value="">All skills</option>
              {options.skills.map((skill) => (
                <option key={skill} value={skill}>
                  {skill}
                </option>
              ))}
            </select>
          </label>
        </div>
      </section>
    )
  }

  const summaryCard = (cohort: ComparisonCohort) => (
    <section className="compare-summary-card" aria-label={`${cohort.key} summary`}>
      <h3>
        Cohort {cohort.key} · {cohort.label}
      </h3>
      <dl>
        <div>
          <dt>Active listings</dt>
          <dd>{cohort.activeListings.toLocaleString('en-NZ')}</dd>
        </div>
        <div>
          <dt>Skill extraction coverage</dt>
          <dd>
            {cohort.listingsWithSkills.toLocaleString('en-NZ')} of {cohort.activeListings.toLocaleString('en-NZ')} (
            {cohort.skillCoveragePercent}%)
          </dd>
        </div>
        <div>
          <dt>Salary sample / cohort</dt>
          <dd>
            {cohort.salary.eligibleCount.toLocaleString('en-NZ')} of{' '}
            {cohort.salary.cohortCount.toLocaleString('en-NZ')} ({cohort.salary.coveragePercent}%)
          </dd>
        </div>
        <div>
          <dt>Median advertised salary (NZD/year)</dt>
          <dd>{salaryLabel(cohort.salary)}</dd>
        </div>
        <div>
          <dt>New listings in the selected period</dt>
          <dd>
            {cohort.newListings.available
              ? `${cohort.newListings.currentNewListings?.toLocaleString('en-NZ') ?? 'Unavailable'} · ${periodLabel(cohort.newListings.change)}`
              : `Unavailable — ${cohort.newListings.unavailableReason ?? 'not fully retained'}`}
          </dd>
        </div>
      </dl>
      <a
        className="refresh-button"
        href={cohort.key === 'A' ? aHref : bHref}
        aria-label={`Open cohort ${cohort.key} listings`}
      >
        Open {cohort.key} listings <ArrowRight aria-hidden="true" />
      </a>
    </section>
  )

  return (
    <div className="app-shell" id="compare-cohorts">
      <AppSidebar currentPath="/compare" />
      <main id="main-content">
        <ViewHeader
          eyebrow="Release C2 · descriptive comparison"
          title="Compare cohorts"
          description="Compare two Data & Analytics cohorts on the same active capture and the same complete reporting windows. Salary and skill differences are descriptive, never causal."
          status={overviewQuery.data?.dataStatus}
          actions={
            <button type="button" className="reset-button" onClick={reset}>
              Reset comparison
            </button>
          }
        />

        {overviewQuery.isError ? (
          <section className="empty-state error-state" role="alert">
            Market context could not be loaded. This is a data-loading failure, not zero demand.
          </section>
        ) : null}

        {overviewQuery.data ? <DataStatusBanner status={overviewQuery.data.dataStatus} /> : null}

        <section className="scope-bar" aria-label="Comparison reporting scope">
          <div>
            <span>Comparison</span>
            <strong>
              {comparison
                ? `${comparison.a.label} vs ${comparison.b.label}`
                : loading
                  ? 'Loading cohort definitions…'
                  : 'Unavailable'}
            </strong>
          </div>
          <div>
            <span>Reporting period</span>
            <select aria-label="Comparison reporting period" value={state.days} onChange={(event) => update('days', event.target.value)}>
              <option value="7">Last 7 complete days</option>
              <option value="15">Last 15 complete days</option>
              <option value="30">Last 30 complete days</option>
            </select>
          </div>
          <div>
            <span>Window (NZ)</span>
            <strong>
              {comparison
                ? `${comparison.interval.startLocalDate} to ${comparison.interval.endLocalDateExclusive} (end exclusive)`
                : loading
                  ? 'Loading…'
                  : 'Unavailable'}
            </strong>
          </div>
          <div>
            <span>Date rules</span>
            <strong>Identical for both cohorts</strong>
          </div>
          <div>
            <span>Data freshness</span>
            <strong>{overviewQuery.data ? formatNzDateTime(overviewQuery.data.dataStatus.lastSuccessfulUpdateUtc) : 'Unavailable'}</strong>
          </div>
          <button type="button" className="clear-all" onClick={swap}>
            Swap A and B
          </button>
        </section>

        <div className="compare-controls">
          {renderControls('a', 'Cohort A')}
          {renderControls('b', 'Cohort B')}
        </div>

        <p className="metric-status" role="status" aria-live="polite">
          {loading
            ? 'Loading both cohorts from the same capture…'
            : comparisonQuery.isFetching
              ? 'Refreshing both cohorts; the previous response is still shown.'
              : ''}
        </p>

        {failed ? (
          <section className="empty-state error-state" role="alert">
            The comparison could not be loaded. This is a data-loading failure, not zero demand or an empty cohort.
          </section>
        ) : loading ? (
          <div className="chart-empty" aria-busy="true">
            Loading counts, skill shares and salary samples for both cohorts…
          </div>
        ) : !comparison ? (
          <div className="chart-empty">No comparison is available for this capture.</div>
        ) : (
          <>
            <div className="compare-summary">
              {summaryCard(comparison.a)}
              {summaryCard(comparison.b)}
            </div>

            <section className="panel" aria-labelledby="compare-counts-heading">
              <div className="panel-header">
                <div>
                  <h2 id="compare-counts-heading">Active listing count</h2>
                  <p>
                    One active capture, both cohorts. The bar scale is shared; the table repeats the values.
                  </p>
                </div>
                <GitCompareArrows aria-hidden="true" />
              </div>
              <ComparisonBars
                ariaLabel="Active listings by cohort"
                aLabel="Cohort A"
                bLabel="Cohort B"
                items={[
                  {
                    label: 'Active listings',
                    aValue: comparison.a.activeListings,
                    bValue: comparison.b.activeListings,
                    note: 'as of this capture',
                  },
                ]}
              />
              <div className="table-scroll">
                <table className="compare-table">
                  <caption>Active count difference · Cohort A is the explicit baseline</caption>
                  <thead>
                    <tr>
                      <th scope="col">Cohort A</th>
                      <th scope="col">Cohort B</th>
                      <th scope="col">Absolute difference (B − A)</th>
                      <th scope="col">Relative difference</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td>{comparison.activeCountDifference.aCount.toLocaleString('en-NZ')}</td>
                      <td>{comparison.activeCountDifference.bCount.toLocaleString('en-NZ')}</td>
                      <td>
                        {comparison.activeCountDifference.absoluteDifference > 0 ? '+' : ''}
                        {comparison.activeCountDifference.absoluteDifference.toLocaleString('en-NZ')}
                      </td>
                      <td>
                        {comparison.activeCountDifference.relativeDifferencePercent == null
                          ? 'Unavailable — no baseline'
                          : `${comparison.activeCountDifference.relativeDifferencePercent > 0 ? '+' : ''}${comparison.activeCountDifference.relativeDifferencePercent}%`}
                      </td>
                    </tr>
                  </tbody>
                </table>
              </div>
              <p className="muted chart-caption">{comparison.activeCountDifference.note}</p>
            </section>

            <section className="panel" aria-labelledby="compare-skills-heading">
              <div className="panel-header">
                <div>
                  <h2 id="compare-skills-heading">Skill share</h2>
                  <p>
                    A skill counts at most once per listing; each cohort's denominator is its active listing count.
                    Differences are percentage points (B − A), not ratios.
                  </p>
                </div>
              </div>
              <ComparisonBars
                ariaLabel="Skill share by cohort"
                aLabel="Cohort A"
                bLabel="Cohort B"
                items={comparison.skillDifferences.map((skill) => ({
                  label: skill.skillName,
                  aValue: skill.aCount,
                  bValue: skill.bCount,
                  aDisplay: `${skill.aCount.toLocaleString('en-NZ')} (${skill.aSharePercent}%)`,
                  bDisplay: `${skill.bCount.toLocaleString('en-NZ')} (${skill.bSharePercent}%)`,
                }))}
                emptyMessage="No extracted skill appears in either cohort."
              />
              <div className="table-scroll">
                <table className="compare-table">
                  <caption>Skill share by cohort · denominator is each cohort's active listing count</caption>
                  <thead>
                    <tr>
                      <th scope="col">Skill</th>
                      <th scope="col">Cohort A count</th>
                      <th scope="col">Cohort A share</th>
                      <th scope="col">Cohort B count</th>
                      <th scope="col">Cohort B share</th>
                      <th scope="col">Difference (pp)</th>
                    </tr>
                  </thead>
                  <tbody>
                    {comparison.skillDifferences.map((skill) => (
                      <tr key={skill.skillName}>
                        <th scope="row">{skill.skillName}</th>
                        <td>{skill.aCount.toLocaleString('en-NZ')}</td>
                        <td>{skill.aSharePercent}%</td>
                        <td>{skill.bCount.toLocaleString('en-NZ')}</td>
                        <td>{skill.bSharePercent}%</td>
                        <td>
                          {skill.differencePercentagePoints > 0 ? '+' : ''}
                          {skill.differencePercentagePoints} pp
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <p className="muted chart-caption">
                Skill-share percentages can sum above 100% because a listing can carry several skills. Differences are
                descriptive and do not establish that one cohort causes a skill requirement.
              </p>
            </section>

            <div className="compare-grid">
              <section className="panel" aria-labelledby="compare-salary-heading">
                <div className="panel-header">
                  <div>
                    <h2 id="compare-salary-heading">Salary sample and coverage</h2>
                    <p>Comparable permanent annual NZD midpoints only. Groups below five are suppressed; 5–9 are flagged.</p>
                  </div>
                </div>
                <div className="table-scroll">
                  <table className="compare-table">
                    <caption>Descriptive salary summary · no causal salary-premium claim is made</caption>
                    <thead>
                      <tr>
                        <th scope="col">Cohort</th>
                        <th scope="col">Sample / cohort</th>
                        <th scope="col">Coverage</th>
                        <th scope="col">Median (NZD/year)</th>
                      </tr>
                    </thead>
                    <tbody>
                      {[comparison.a, comparison.b].map((cohort) => (
                        <tr key={cohort.key}>
                          <th scope="row">Cohort {cohort.key}</th>
                          <td>
                            {cohort.salary.eligibleCount.toLocaleString('en-NZ')} /{' '}
                            {cohort.salary.cohortCount.toLocaleString('en-NZ')}
                          </td>
                          <td>{cohort.salary.coveragePercent}%</td>
                          <td>{salaryLabel(cohort.salary)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                <p className="muted chart-caption">
                  Missing and incomparable salaries stay in the cohort denominator and are never treated as zero.
                </p>
              </section>

              <section className="panel" aria-labelledby="compare-period-heading">
                <div className="panel-header">
                  <div>
                    <h2 id="compare-period-heading">New listings in the period</h2>
                    <p>Complete Pacific/Auckland windows only; the current and previous periods are equal length.</p>
                  </div>
                </div>
                <div className="table-scroll">
                  <table className="compare-table">
                    <caption>Period counts follow the same completeness gate for both cohorts</caption>
                    <thead>
                      <tr>
                        <th scope="col">Cohort</th>
                        <th scope="col">Current</th>
                        <th scope="col">Previous</th>
                        <th scope="col">Change</th>
                      </tr>
                    </thead>
                    <tbody>
                      {[comparison.a, comparison.b].map((cohort) => (
                        <tr key={cohort.key}>
                          <th scope="row">Cohort {cohort.key}</th>
                          <td>{cohort.newListings.available ? cohort.newListings.currentNewListings?.toLocaleString('en-NZ') ?? 'Unavailable' : 'Unavailable'}</td>
                          <td>{cohort.newListings.available ? cohort.newListings.previousNewListings?.toLocaleString('en-NZ') ?? 'Unavailable' : 'Unavailable'}</td>
                          <td>{cohort.newListings.available ? periodLabel(cohort.newListings.change) : 'Unavailable'}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                <p className="muted chart-caption">
                  {comparison.a.newListings.available
                    ? comparison.a.newListings.dateBasis
                    : comparison.a.newListings.unavailableReason}
                </p>
                {!comparison.b.newListings.available ? (
                  <p className="muted chart-caption">{comparison.b.newListings.unavailableReason}</p>
                ) : null}
              </section>
            </div>

            <section className="panel" aria-labelledby="compare-history-heading">
              <div className="panel-header">
                <div>
                  <h2 id="compare-history-heading">Skill-share history</h2>
                  <p>
                    Uses only snapshot days that actually captured skills. Both complete windows must be covered before
                    any historical skill share is computed.
                  </p>
                </div>
              </div>
              {!comparison.skillHistory.available ? (
                <div className="compare-history-unavailable" role="status">
                  <strong>Skill-share history unavailable</strong>
                  {comparison.skillHistory.reason}
                  <span className="muted">
                    Captured skill days: {comparison.skillHistory.skillsCapturedSnapshotDays} of{' '}
                    {comparison.skillHistory.requiredCompleteDays} required
                    {comparison.skillHistory.firstSkillsCapturedDate
                      ? ` · first ${comparison.skillHistory.firstSkillsCapturedDate}`
                      : ''}
                    {comparison.skillHistory.lastSkillsCapturedDate
                      ? ` · latest ${comparison.skillHistory.lastSkillsCapturedDate}`
                      : ''}
                    .
                  </span>
                </div>
              ) : (
                <>
                  <div className="table-scroll">
                    <table className="compare-table">
                      <caption>
                        {comparison.skillHistory.currentPeriodLabel} · previous {comparison.skillHistory.previousPeriodLabel}
                      </caption>
                      <thead>
                        <tr>
                          <th scope="col">Skill</th>
                          <th scope="col">A current</th>
                          <th scope="col">A previous</th>
                          <th scope="col">A change</th>
                          <th scope="col">B current</th>
                          <th scope="col">B previous</th>
                          <th scope="col">B change</th>
                        </tr>
                      </thead>
                      <tbody>
                        {comparison.skillHistory.skills.map((skill) => (
                          <tr key={skill.skillName}>
                            <th scope="row">{skill.skillName}</th>
                            <td>{skill.aCurrentSharePercent}%</td>
                            <td>{skill.aPreviousSharePercent == null ? 'Unavailable' : `${skill.aPreviousSharePercent}%`}</td>
                            <td>{skill.aChangePoints == null ? 'Unavailable' : `${skill.aChangePoints > 0 ? '+' : ''}${skill.aChangePoints} pp`}</td>
                            <td>{skill.bCurrentSharePercent}%</td>
                            <td>{skill.bPreviousSharePercent == null ? 'Unavailable' : `${skill.bPreviousSharePercent}%`}</td>
                            <td>{skill.bChangePoints == null ? 'Unavailable' : `${skill.bChangePoints > 0 ? '+' : ''}${skill.bChangePoints} pp`}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <p className="muted chart-caption">{comparison.skillHistory.basis}</p>
                </>
              )}
            </section>

            <section className="panel" aria-label="Comparison navigation and notes">
              <div className="panel-header">
                <div>
                  <h2>Open either cohort</h2>
                  <p>Both links keep the comparison definition in the URL so browser Back returns to this view.</p>
                </div>
              </div>
              <div className="explorer-actions">
                <a className="refresh-button" href={aHref}>
                  Open cohort A listings <ArrowRight aria-hidden="true" />
                </a>
                <a className="refresh-button" href={bHref}>
                  Open cohort B listings <ArrowRight aria-hidden="true" />
                </a>
              </div>
              <p className="muted chart-caption">{comparison.dateRule}</p>
              <p className="muted chart-caption">{comparison.scopeNote}</p>
              {notes.length > 0 ? (
                <ul className="compare-notes">
                  {notes.map((note) => (
                    <li key={note}>{note}</li>
                  ))}
                </ul>
              ) : null}
            </section>
          </>
        )}
      </main>
    </div>
  )
}
