import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowRight, Layers3 } from 'lucide-react'
import { FilterChips } from '../jobs/FilterChips'
import { RankedBarList } from '../charts/RankedBarList'
import { DATA_SUB_ROLE_FAMILY, DATA_SUB_ROLE_OPTIONS } from '../../lib/dataRoles'
import { formatCurrency } from '../../lib/format'
import { buildJobsHref, dataExplorerFilterChips, type TrendsFilterState } from '../../lib/urlState'
import { getDataRoles } from '../../services/api'
import type { Job } from '../../types/api'

type DataRoleExplorerProps = {
  jobs: Job[]
  filters: TrendsFilterState
  onChange: (name: keyof TrendsFilterState, value: string) => void
  onClear: () => void
}

function unique(values: Array<string | null | undefined>) {
  return [...new Set(values.filter((value): value is string => Boolean(value) && value !== 'Unknown'))].sort()
}

function salaryText(row: { salary: { suppressed: boolean; smallSample: boolean; medianAnnualNzd: number | null; eligibleCount: number; cohortCount: number; coveragePercent: number } }) {
  if (row.salary.suppressed) return `Suppressed (<5 samples)`
  const median = formatCurrency(row.salary.medianAnnualNzd)
  return row.salary.smallSample ? `${median} · small sample (5–9)` : median
}

/**
 * Round 22 (C1): focused Data & Analytics explorer. Every number comes from the
 * versioned `/api/analytics/data-roles` breakdown; the ranked bars and the table
 * render the same rows, so a value is never chart-only or hover-only. The
 * filters live in the `/trends` URL state and do not move the market panels.
 */
export function DataRoleExplorer({ jobs, filters, onChange, onClear }: DataRoleExplorerProps) {
  const familyJobs = useMemo(
    () => jobs.filter((job) => job.isActive && job.roleFamily === DATA_SUB_ROLE_FAMILY),
    [jobs],
  )
  const options = useMemo(
    () => ({
      regions: unique(familyJobs.map((job) => job.region ?? job.location)),
      seniorities: unique(familyJobs.map((job) => job.seniority)),
      workModes: unique(familyJobs.map((job) => job.workMode)),
      skills: unique(familyJobs.flatMap((job) => job.skills)),
    }),
    [familyJobs],
  )

  const queryFilters = useMemo(
    () => ({
      region: filters.exploreRegion || undefined,
      seniority: filters.exploreSeniority || undefined,
      workMode: filters.exploreWorkMode || undefined,
      skill: filters.exploreSkill || undefined,
    }),
    [filters.exploreRegion, filters.exploreSeniority, filters.exploreWorkMode, filters.exploreSkill],
  )
  const rolesQuery = useQuery({
    queryKey: ['data-roles', queryFilters],
    queryFn: () => getDataRoles(queryFilters),
  })
  const data = rolesQuery.data
  const selectedRole = data?.roles.find((role) => role.key === filters.exploreRole) ?? null
  const chips = dataExplorerFilterChips(filters)

  const matchingHref = buildJobsHref({
    roleFamily: DATA_SUB_ROLE_FAMILY,
    dataSubRole: selectedRole?.key ?? '',
    location: filters.exploreRegion,
    seniority: filters.exploreSeniority,
    workMode: filters.exploreWorkMode,
    skill: filters.exploreSkill,
  })
  const allMatchingHref = buildJobsHref({
    roleFamily: DATA_SUB_ROLE_FAMILY,
    location: filters.exploreRegion,
    seniority: filters.exploreSeniority,
    workMode: filters.exploreWorkMode,
    skill: filters.exploreSkill,
  })

  const version = data?.taxonomyVersion ?? 'data-subrole-v1'

  return (
    <section className="panel role-explorer" id="data-role-explorer" aria-labelledby="role-explorer-heading">
      <div className="panel-header">
        <div>
          <span className="eyebrow">Release C1 · versioned taxonomy</span>
          <h2 id="role-explorer-heading">Data &amp; Analytics role explorer</h2>
          <p>
            Counts, salary samples and skill coverage for the Data &amp; Analytics family, split by the retained
            title. Original titles stay visible in Find jobs; ambiguous or multiple-role titles are never forced into
            one label.
          </p>
        </div>
        <Layers3 aria-hidden="true" />
      </div>

      <div className="explorer-taxonomy-bar">
        <span>
          Taxonomy <strong>{version}</strong>
        </span>
        {data ? (
          <>
            <span>
              Effective <strong>{data.taxonomyEffectiveDate}</strong>
            </span>
            <span>
              Scope <strong>{data.family} family only</strong>
            </span>
          </>
        ) : null}
        <a className="refresh-button" href={matchingHref}>
          View matching jobs <ArrowRight aria-hidden="true" />
        </a>
        <a className="clear-all" href="/compare">
          Compare two cohorts
        </a>
      </div>

      <div className="explorer-filters" aria-label="Data role explorer filters">
        <label>
          <span>Region</span>
          <select value={filters.exploreRegion} onChange={(event) => onChange('exploreRegion', event.target.value)}>
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
          <select value={filters.exploreSeniority} onChange={(event) => onChange('exploreSeniority', event.target.value)}>
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
          <select value={filters.exploreWorkMode} onChange={(event) => onChange('exploreWorkMode', event.target.value)}>
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
          <select value={filters.exploreSkill} onChange={(event) => onChange('exploreSkill', event.target.value)}>
            <option value="">All skills</option>
            {options.skills.map((skill) => (
              <option key={skill} value={skill}>
                {skill}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>Sub-role</span>
          <select value={filters.exploreRole} onChange={(event) => onChange('exploreRole', event.target.value)}>
            <option value="">All sub-roles</option>
            {DATA_SUB_ROLE_OPTIONS.map((option) => (
              <option key={option.key} value={option.key}>
                {option.label}
              </option>
            ))}
          </select>
        </label>
      </div>

      <FilterChips chips={chips} onRemove={(name) => onChange(name, '')} onClearAll={onClear} />

      <p className="metric-status" role="status" aria-live="polite">
        {rolesQuery.isLoading
          ? 'Loading the versioned Data & Analytics breakdown…'
          : rolesQuery.isFetching
            ? 'Refreshing the Data & Analytics breakdown; the previous response is still shown.'
            : ''}
      </p>

      {rolesQuery.isError ? (
        <div className="empty-state error-state" role="alert">
          The Data &amp; Analytics breakdown could not be loaded. This is a data-loading failure, not zero demand.
        </div>
      ) : rolesQuery.isLoading ? (
        <div className="chart-empty" aria-busy="true">
          Loading sub-role counts and salary samples…
        </div>
      ) : !data || data.cohortCount === 0 ? (
        <div className="chart-empty">
          No active Data &amp; Analytics listing matches these explorer filters. This is a successful empty result,
          not evidence of zero market demand.
        </div>
      ) : (
        <div className="role-explorer-body">
          <div className="explorer-stats" aria-label="Explorer cohort summary">
            <div>
              <span>Active Data &amp; Analytics listings</span>
              <strong>{data.cohortCount.toLocaleString('en-NZ')}</strong>
              <small>Same active capture as the rest of the page</small>
            </div>
            <div>
              <span>Specifically classified</span>
              <strong>
                {data.classifiedCount.toLocaleString('en-NZ')} <small>({data.classifiedSharePercent}%)</small>
              </strong>
              <small>One clear sub-role signal</small>
            </div>
            <div>
              <span>Uncertain / unspecified</span>
              <strong>{data.uncertainCount.toLocaleString('en-NZ')}</strong>
              <small>Never forced into a label</small>
            </div>
            <div>
              <span>Taxonomy</span>
              <strong>{data.taxonomyVersion}</strong>
              <small>Effective {data.taxonomyEffectiveDate}</small>
            </div>
          </div>

          <div className="explorer-grid">
            <div>
              <h3>Listings by sub-role</h3>
              <RankedBarList
                ariaLabel={`Listings by Data & Analytics sub-role (taxonomy ${data.taxonomyVersion})`}
                items={data.roles.map((role) => ({
                  label: role.label,
                  count: role.listingCount,
                  share: role.sharePercent,
                  note: role.uncertain ? 'not forced' : undefined,
                }))}
                selected={filters.exploreRole}
                onSelect={(label) => {
                  const role = data.roles.find((entry) => entry.label === label)
                  onChange('exploreRole', filters.exploreRole === role?.key ? '' : role?.key ?? '')
                }}
                selectLabel="Focus sub-role"
              />
              <p className="muted chart-caption">
                Active now · denominator {data.cohortCount.toLocaleString('en-NZ')} active Data &amp; Analytics
                listings. Selecting a bar only focuses the detail below and the matching-jobs link; it does not
                reweight the other sub-roles.
              </p>
            </div>

            <div className="explorer-detail" aria-live="polite">
              <h3>{selectedRole ? selectedRole.label : 'All Data & Analytics sub-roles'}</h3>
              {selectedRole ? (
                <>
                  <dl>
                    <div>
                      <dt>Listings</dt>
                      <dd>
                        {selectedRole.listingCount.toLocaleString('en-NZ')} ({selectedRole.sharePercent}% of the
                        filtered family)
                      </dd>
                    </div>
                    <div>
                      <dt>Salary sample and coverage</dt>
                      <dd>
                        {selectedRole.salary.eligibleCount.toLocaleString('en-NZ')} of{' '}
                        {selectedRole.salary.cohortCount.toLocaleString('en-NZ')} listings (
                        {selectedRole.salary.coveragePercent}%)
                      </dd>
                    </div>
                    <div>
                      <dt>Median advertised salary</dt>
                      <dd>{salaryText(selectedRole)}</dd>
                    </div>
                    <div>
                      <dt>Skill extraction coverage</dt>
                      <dd>
                        {selectedRole.listingsWithSkills.toLocaleString('en-NZ')} of{' '}
                        {selectedRole.listingCount.toLocaleString('en-NZ')} listings (
                        {selectedRole.skillCoveragePercent}%)
                      </dd>
                    </div>
                    {selectedRole.uncertain ? (
                      <div>
                        <dt>Why uncertain</dt>
                        <dd>
                          {selectedRole.candidateLabels.length > 0
                            ? `Signals matched more than one definition: ${selectedRole.candidateLabels.join(', ')}.`
                            : 'No specific sub-role definition matched this title.'}{' '}
                          The listing is kept in the uncertain row rather than forced into one role.
                        </dd>
                      </div>
                    ) : null}
                  </dl>
                  {selectedRole.topSkills.length > 0 ? (
                    <>
                      <p className="muted">
                        Top extracted skills · denominator {selectedRole.listingCount.toLocaleString('en-NZ')} listings
                        (a skill counts at most once per listing).
                      </p>
                      <ul className="explorer-skills">
                        {selectedRole.topSkills.map((skill) => (
                          <li key={skill.skillName}>
                            <strong>{skill.skillName}</strong> {skill.listingCount.toLocaleString('en-NZ')} (
                            {skill.sharePercent}%)
                          </li>
                        ))}
                      </ul>
                    </>
                  ) : (
                    <p className="muted">No extracted skill is available for this sub-role.</p>
                  )}
                  <div className="explorer-actions">
                    <a className="refresh-button" href={matchingHref}>
                      View matching {selectedRole.uncertain ? 'uncertain ' : ''}advertisements{' '}
                      <ArrowRight aria-hidden="true" />
                    </a>
                    <a className="clear-all" href={allMatchingHref}>
                      View all Data &amp; Analytics jobs
                    </a>
                  </div>
                </>
              ) : (
                <p className="muted">
                  Select a sub-role bar or use the Sub-role control to inspect its salary sample, coverage and top
                  skills. The table below always shows every sub-role side by side.
                </p>
              )}
            </div>
          </div>

          <div className="table-scroll" role="region" aria-label="Data and Analytics sub-role breakdown table" tabIndex={0}>
            <table className="explorer-table">
              <caption>
                Data &amp; Analytics sub-role breakdown · taxonomy {data.taxonomyVersion} · effective{' '}
                {data.taxonomyEffectiveDate} · captured{' '}
                {new Date(data.generatedAtUtc).toLocaleString('en-NZ', { timeZone: 'Pacific/Auckland' })} NZ
              </caption>
              <thead>
                <tr>
                  <th scope="col">Sub-role</th>
                  <th scope="col">Listings</th>
                  <th scope="col">Share</th>
                  <th scope="col">Salary sample / cohort</th>
                  <th scope="col">Salary coverage</th>
                  <th scope="col">Median (NZD/year)</th>
                  <th scope="col">Skill coverage</th>
                </tr>
              </thead>
              <tbody>
                {data.roles.map((role) => (
                  <tr key={role.key}>
                    <th scope="row">
                      {role.label}
                      {role.uncertain ? <small className="muted"> · not forced</small> : null}
                    </th>
                    <td>{role.listingCount.toLocaleString('en-NZ')}</td>
                    <td>{role.sharePercent}%</td>
                    <td>
                      {role.salary.eligibleCount.toLocaleString('en-NZ')} /{' '}
                      {role.salary.cohortCount.toLocaleString('en-NZ')}
                    </td>
                    <td>{role.salary.coveragePercent}%</td>
                    <td>{salaryText(role)}</td>
                    <td>
                      {role.listingsWithSkills.toLocaleString('en-NZ')} /{' '}
                      {role.listingCount.toLocaleString('en-NZ')} ({role.skillCoveragePercent}%)
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="muted chart-caption">{data.salaryBasis}</p>
          <p className="muted chart-caption">{data.skillBasis}</p>
          <p className="muted chart-caption">{data.scopeNote}</p>
          <p className="muted chart-caption">{data.taxonomyScope}</p>
        </div>
      )}
    </section>
  )
}
