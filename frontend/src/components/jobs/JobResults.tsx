import { JobCard } from './JobCard'
import type { Job } from '../../types/api'
import { JOB_SORTS, type JobSort } from '../../lib/urlState'

const SORT_LABELS: Record<JobSort, string> = {
  newest: 'Newest first',
  'salary-desc': 'Salary: high to low',
  'salary-asc': 'Salary: low to high',
  closing: 'Closing soonest',
}

type JobResultsProps = {
  jobs: Job[]
  total: number
  page: number
  pageSize: number
  sort: JobSort
  onSortChange: (sort: JobSort) => void
  onPageChange: (page: number) => void
}

export function JobResults({ jobs, total, page, pageSize, sort, onSortChange, onPageChange }: JobResultsProps) {
  const pageCount = Math.max(1, Math.ceil(total / pageSize))

  if (total === 0) {
    return (
      <div className="table-empty" role="status">
        No roles match the current filters. Adjust or clear a filter to widen the search.
      </div>
    )
  }

  return (
    <>
      <div className="results-toolbar">
        <span>
          Showing <strong>{jobs.length}</strong> of <strong>{total.toLocaleString('en-NZ')}</strong> matching roles
        </span>
        <label>
          <span>Sort</span>
          <select value={sort} onChange={(event) => onSortChange(event.target.value as JobSort)}>
            {JOB_SORTS.map((option) => (
              <option key={option} value={option}>
                {SORT_LABELS[option]}
              </option>
            ))}
          </select>
        </label>
      </div>

      <ul className="job-list">
        {jobs.map((job) => (
          <li key={job.id}>
            <JobCard job={job} />
          </li>
        ))}
      </ul>

      <nav className="table-pagination" aria-label="Job results pages">
        <span>
          Page {page} of {pageCount}
        </span>
        <div>
          <button type="button" onClick={() => onPageChange(page - 1)} disabled={page <= 1}>
            Previous
          </button>
          <button type="button" onClick={() => onPageChange(page + 1)} disabled={page >= pageCount}>
            Next
          </button>
        </div>
      </nav>
    </>
  )
}
