import { Search, SlidersHorizontal } from 'lucide-react'
import type { JobFilterState } from '../../lib/urlState'

export type FilterOptions = {
  locations: string[]
  workModes: string[]
  seniorities: string[]
  roleFamilies: string[]
  skills: string[]
  companies: string[]
  sources: string[]
  employmentTypes: string[]
  experienceBands: string[]
  programmingLanguages: string[]
}

type JobFiltersProps = {
  options: FilterOptions
  filters: JobFilterState
  onChange: (name: keyof JobFilterState, value: string) => void
}

const SECONDARY_KEYS: Array<keyof JobFilterState> = [
  'company',
  'source',
  'employmentType',
  'days',
  'skill',
  'roleFamily',
  'experienceBand',
  'programmingLanguage',
]

/** Primary filters first; everything else lives under More filters. */
export function JobFilters({ options, filters, onChange }: JobFiltersProps) {
  const secondaryCount = SECONDARY_KEYS.filter((key) => filters[key]).length

  return (
    <section className="filter-bar" aria-label="Job filters">
      <div className="primary-filters">
        <label className="search-filter">
          <span>Search roles</span>
          <div className="search-input">
            <Search aria-hidden="true" />
            <input
              value={filters.q}
              onChange={(event) => onChange('q', event.target.value)}
              placeholder="Try ‘React’, ‘data engineer’ or ‘Xero’"
              aria-label="Search roles by title, employer or skill"
            />
          </div>
        </label>
        <label>
          <span>Location</span>
          <select value={filters.location} onChange={(event) => onChange('location', event.target.value)}>
            <option value="">All locations</option>
            {options.locations.map((location) => (
              <option key={location} value={location}>
                {location}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>Work mode</span>
          <select value={filters.workMode} onChange={(event) => onChange('workMode', event.target.value)}>
            <option value="">All modes</option>
            {options.workModes.map((mode) => (
              <option key={mode} value={mode}>
                {mode}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>Seniority</span>
          <select value={filters.seniority} onChange={(event) => onChange('seniority', event.target.value)}>
            <option value="">All seniority</option>
            {options.seniorities.map((seniority) => (
              <option key={seniority} value={seniority}>
                {seniority}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>Salary</span>
          <select value={filters.salaryBand} onChange={(event) => onChange('salaryBand', event.target.value)}>
            <option value="">All salaries</option>
            <option value="lt70">Under $70k</option>
            <option value="70-100">$70k – $100k</option>
            <option value="100-130">$100k – $130k</option>
            <option value="gte130">$130k+</option>
            <option value="missing">Salary not advertised</option>
          </select>
        </label>
      </div>

      <details className="advanced-filters">
        <summary>
          <SlidersHorizontal aria-hidden="true" /> More filters{' '}
          <span>{secondaryCount > 0 ? `${secondaryCount} active` : 'Optional'}</span>
        </summary>
        <div className="advanced-filter-grid">
          <label>
            <span>Posted within</span>
            <select value={filters.days} onChange={(event) => onChange('days', event.target.value)}>
              <option value="">All time</option>
              <option value="2">Last 2 days</option>
              <option value="7">Last 7 days</option>
              <option value="15">Last 15 days</option>
            </select>
          </label>
          <label>
            <span>Role family</span>
            <select value={filters.roleFamily} onChange={(event) => onChange('roleFamily', event.target.value)}>
              <option value="">All role families</option>
              {options.roleFamilies.map((family) => (
                <option key={family} value={family}>
                  {family}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Skill</span>
            <select value={filters.skill} onChange={(event) => onChange('skill', event.target.value)}>
              <option value="">All skills</option>
              {options.skills.map((skill) => (
                <option key={skill} value={skill}>
                  {skill}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Company</span>
            <select value={filters.company} onChange={(event) => onChange('company', event.target.value)}>
              <option value="">All companies</option>
              {options.companies.map((company) => (
                <option key={company} value={company}>
                  {company}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Source</span>
            <select value={filters.source} onChange={(event) => onChange('source', event.target.value)}>
              <option value="">All sources</option>
              {options.sources.map((source) => (
                <option key={source} value={source}>
                  {source}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Employment</span>
            <select value={filters.employmentType} onChange={(event) => onChange('employmentType', event.target.value)}>
              <option value="">All types</option>
              {options.employmentTypes.map((type) => (
                <option key={type} value={type}>
                  {type}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Experience required</span>
            <select value={filters.experienceBand} onChange={(event) => onChange('experienceBand', event.target.value)}>
              <option value="">All experience</option>
              {options.experienceBands.map((band) => (
                <option key={band} value={band}>
                  {band}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Programming language</span>
            <select
              value={filters.programmingLanguage}
              onChange={(event) => onChange('programmingLanguage', event.target.value)}
            >
              <option value="">All languages</option>
              {options.programmingLanguages.map((language) => (
                <option key={language} value={language}>
                  {language}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span>Status</span>
            <select value={filters.status} onChange={(event) => onChange('status', event.target.value)}>
              <option value="active">Active roles</option>
              <option value="expired">Expired roles</option>
              <option value="all">All statuses</option>
            </select>
          </label>
        </div>
      </details>
    </section>
  )
}
