import { RotateCcw, Search, SlidersHorizontal } from 'lucide-react'

export type JobFilters = {
  timeRange: string
  location: string
  skill: string
  company: string
  industry: string
  source: string
  seniority: string
  workMode: string
  employmentType: string
  salaryBand: string
  experienceBand: string
  programmingLanguage: string
  status: string
  search: string
}

type FilterBarProps = {
  locations: string[]
  skills: string[]
  companies: string[]
  industries: string[]
  sources: string[]
  seniorities: string[]
  workModes: string[]
  employmentTypes: string[]
  programmingLanguages: string[]
  filters: JobFilters
  onFilterChange: (name: keyof JobFilters, value: string) => void
  onReset: () => void
}

export function FilterBar({
  locations,
  skills,
  companies,
  industries,
  sources,
  seniorities,
  workModes,
  employmentTypes,
  programmingLanguages,
  filters,
  onFilterChange,
  onReset,
}: FilterBarProps) {
  const activeCount = Object.entries(filters).filter(([name, value]) => value && !(name === 'status' && value === 'active')).length

  return (
    <section className="filter-bar">
      <div className="filter-heading">
        <div>
          <span className="eyebrow">Explore the market</span>
          <h2>Find the signal in the listings</h2>
        </div>
        <button className="reset-button" type="button" onClick={onReset} disabled={activeCount === 0}>
          <RotateCcw aria-hidden="true" /> Reset {activeCount > 0 ? `(${activeCount})` : ''}
        </button>
      </div>
      <div className="primary-filters">
        <label className="search-filter">
          <span>Search roles</span>
        <div className="search-input">
          <Search aria-hidden="true" />
          <input
            value={filters.search}
            onChange={(event) => onFilterChange('search', event.target.value)}
            placeholder="Try ‘React’, ‘data engineer’, or ‘Halter’"
          />
        </div>
        </label>
        <label>
          <span>Status</span>
          <select value={filters.status} onChange={(event) => onFilterChange('status', event.target.value)}>
            <option value="active">Active roles</option>
            <option value="expired">Expired roles</option>
            <option value="all">All statuses</option>
          </select>
        </label>
        <label>
          <span>Location</span>
          <select value={filters.location} onChange={(event) => onFilterChange('location', event.target.value)}>
            <option value="">All locations</option>
            {locations.map((location) => <option key={location} value={location}>{location}</option>)}
          </select>
        </label>
        <label>
          <span>Source</span>
          <select value={filters.source} onChange={(event) => onFilterChange('source', event.target.value)}>
            <option value="">All sources</option>
            {sources.map((source) => <option key={source} value={source}>{source}</option>)}
          </select>
        </label>
      </div>
      <details className="advanced-filters">
        <summary><SlidersHorizontal aria-hidden="true" /> Advanced filters <span>{activeCount > 0 ? `${activeCount} active` : 'Optional'}</span></summary>
        <div className="advanced-filter-grid">
          <label><span>Time range</span><select value={filters.timeRange} onChange={(event) => onFilterChange('timeRange', event.target.value)}><option value="">All time</option><option value="7">Last 7 days</option><option value="30">Last 30 days</option><option value="90">Last 90 days</option></select></label>
          <label><span>Skill</span><select value={filters.skill} onChange={(event) => onFilterChange('skill', event.target.value)}><option value="">All skills</option>{skills.map((skill) => <option key={skill} value={skill}>{skill}</option>)}</select></label>
          <label><span>Company</span><select value={filters.company} onChange={(event) => onFilterChange('company', event.target.value)}><option value="">All companies</option>{companies.map((company) => <option key={company} value={company}>{company}</option>)}</select></label>
          <label><span>Industry</span><select value={filters.industry} onChange={(event) => onFilterChange('industry', event.target.value)}><option value="">All industries</option>{industries.map((industry) => <option key={industry} value={industry}>{industry}</option>)}</select></label>
          <label><span>Seniority</span><select value={filters.seniority} onChange={(event) => onFilterChange('seniority', event.target.value)}><option value="">All seniority</option>{seniorities.map((seniority) => <option key={seniority} value={seniority}>{seniority}</option>)}</select></label>
          <label><span>Work mode</span><select value={filters.workMode} onChange={(event) => onFilterChange('workMode', event.target.value)}><option value="">All modes</option>{workModes.map((workMode) => <option key={workMode} value={workMode}>{workMode}</option>)}</select></label>
          <label><span>Employment</span><select value={filters.employmentType} onChange={(event) => onFilterChange('employmentType', event.target.value)}><option value="">All types</option>{employmentTypes.map((employmentType) => <option key={employmentType} value={employmentType}>{employmentType}</option>)}</select></label>
          <label><span>Salary band</span><select value={filters.salaryBand} onChange={(event) => onFilterChange('salaryBand', event.target.value)}><option value="">All salaries</option><option value="lt70">Under $70k</option><option value="70-100">$70k – $100k</option><option value="100-130">$100k – $130k</option><option value="gte130">$130k+</option><option value="missing">Salary missing</option></select></label>
          <label><span>Experience required</span><select value={filters.experienceBand} onChange={(event) => onFilterChange('experienceBand', event.target.value)}><option value="">All experience</option><option value="0-2 years">0–2 years</option><option value="2-5 years">2–5 years</option><option value="More than 5 years">More than 5 years</option><option value="Unknown">Not specified</option></select></label>
          <label><span>Programming language</span><select value={filters.programmingLanguage} onChange={(event) => onFilterChange('programmingLanguage', event.target.value)}><option value="">All languages</option>{programmingLanguages.map((language) => <option key={language} value={language}>{language}</option>)}</select></label>
        </div>
      </details>
    </section>
  )
}
