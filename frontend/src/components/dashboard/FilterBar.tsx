import { Search } from 'lucide-react'

export type JobFilters = {
  timeRange: string
  region: string
  skill: string
  company: string
  industry: string
  source: string
  seniority: string
  workMode: string
  employmentType: string
  salaryBand: string
  search: string
}

type FilterBarProps = {
  regions: string[]
  skills: string[]
  companies: string[]
  industries: string[]
  sources: string[]
  seniorities: string[]
  workModes: string[]
  employmentTypes: string[]
  filters: JobFilters
  onFilterChange: (name: keyof JobFilters, value: string) => void
}

export function FilterBar({
  regions,
  skills,
  companies,
  industries,
  sources,
  seniorities,
  workModes,
  employmentTypes,
  filters,
  onFilterChange,
}: FilterBarProps) {
  return (
    <section className="filter-bar">
      <label>
        <span>Time range</span>
        <select value={filters.timeRange} onChange={(event) => onFilterChange('timeRange', event.target.value)}>
          <option value="">All time</option>
          <option value="7">Last 7 days</option>
          <option value="30">Last 30 days</option>
          <option value="90">Last 90 days</option>
        </select>
      </label>
      <label>
        <span>Region</span>
        <select value={filters.region} onChange={(event) => onFilterChange('region', event.target.value)}>
          <option value="">All regions</option>
          {regions.map((region) => (
            <option key={region} value={region}>
              {region}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Skill</span>
        <select value={filters.skill} onChange={(event) => onFilterChange('skill', event.target.value)}>
          <option value="">All skills</option>
          {skills.map((skill) => (
            <option key={skill} value={skill}>
              {skill}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Company</span>
        <select value={filters.company} onChange={(event) => onFilterChange('company', event.target.value)}>
          <option value="">All companies</option>
          {companies.map((company) => (
            <option key={company} value={company}>
              {company}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Industry</span>
        <select value={filters.industry} onChange={(event) => onFilterChange('industry', event.target.value)}>
          <option value="">All industries</option>
          {industries.map((industry) => (
            <option key={industry} value={industry}>
              {industry}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Source</span>
        <select value={filters.source} onChange={(event) => onFilterChange('source', event.target.value)}>
          <option value="">All sources</option>
          {sources.map((source) => (
            <option key={source} value={source}>
              {source}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Seniority</span>
        <select value={filters.seniority} onChange={(event) => onFilterChange('seniority', event.target.value)}>
          <option value="">All seniority</option>
          {seniorities.map((seniority) => (
            <option key={seniority} value={seniority}>
              {seniority}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Work mode</span>
        <select value={filters.workMode} onChange={(event) => onFilterChange('workMode', event.target.value)}>
          <option value="">All modes</option>
          {workModes.map((workMode) => (
            <option key={workMode} value={workMode}>
              {workMode}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Employment</span>
        <select value={filters.employmentType} onChange={(event) => onFilterChange('employmentType', event.target.value)}>
          <option value="">All types</option>
          {employmentTypes.map((employmentType) => (
            <option key={employmentType} value={employmentType}>
              {employmentType}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>Salary band</span>
        <select value={filters.salaryBand} onChange={(event) => onFilterChange('salaryBand', event.target.value)}>
          <option value="">All salaries</option>
          <option value="lt70">Under $70k</option>
          <option value="70-100">$70k - $100k</option>
          <option value="100-130">$100k - $130k</option>
          <option value="gte130">$130k+</option>
          <option value="missing">Salary missing</option>
        </select>
      </label>
      <label>
        <span>Search</span>
        <div className="search-input">
          <Search aria-hidden="true" />
          <input
            value={filters.search}
            onChange={(event) => onFilterChange('search', event.target.value)}
            placeholder="Title, company, skill"
          />
        </div>
      </label>
    </section>
  )
}
