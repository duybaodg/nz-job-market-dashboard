import { ArrowUpRight, CalendarClock, MapPin } from 'lucide-react'
import { formatDate } from '../../lib/format'
import { employerLines, locationLabel, postedBasisLabel, workModeLabel } from '../../lib/jobDisplay'
import { formatAdvertisedSalary } from '../../lib/salary'
import type { Job } from '../../types/api'

type JobCardProps = {
  job: Job
}

export function JobCard({ job }: JobCardProps) {
  const employer = employerLines(job)
  const posted = postedBasisLabel(job)
  const tags = [...new Set([...job.skills, ...job.programmingLanguages])]
  const visibleTags = tags.slice(0, 5)
  const hiddenTags = tags.slice(5)

  return (
    <article className="job-card">
      <div className="job-card-head">
        <h3>
          <a href={job.url} target="_blank" rel="noreferrer">
            {job.title}
            <ArrowUpRight aria-hidden="true" />
          </a>
        </h3>
        <span className={`job-salary${job.salaryMin == null && job.salaryMax == null ? ' muted-salary' : ''}`}>
          {formatAdvertisedSalary(job)}
          {job.salaryQuestionable ? (
            <span className="salary-flag" title={job.salaryQuestionableReason ?? 'Flagged for review'}>
              Check parse
            </span>
          ) : null}
        </span>
      </div>

      <p className="job-employer">
        <strong>{employer.primary}</strong>
        {employer.secondary ? <span>{employer.secondary}</span> : null}
      </p>

      {job.alsoOnSources.length > 0 ? (
        <p className="job-duplicate" title="The same vacancy is advertised on another tracked source">
          Also on {job.alsoOnSources.join(', ')}
          {job.duplicateCount > job.alsoOnSources.length ? ` and ${job.duplicateCount - job.alsoOnSources.length} more` : ''}
        </p>
      ) : null}

      <dl className="job-meta">
        <div>
          <dt>
            <MapPin aria-hidden="true" /> Location
          </dt>
          <dd>{locationLabel(job)}</dd>
        </div>
        <div>
          <dt>Work mode</dt>
          <dd>{workModeLabel(job)}</dd>
        </div>
        <div>
          <dt>Employment</dt>
          <dd>{job.employmentType === 'Unknown' ? 'Not stated' : job.employmentType}</dd>
        </div>
        <div>
          <dt>Seniority</dt>
          <dd>{job.seniority === 'Unknown' ? 'Not stated' : job.seniority}</dd>
        </div>
        <div>
          <dt>
            <CalendarClock aria-hidden="true" /> {posted.basis}
          </dt>
          <dd>
            {formatDate(posted.date)}
            {job.closingDate ? <small> · closes {formatDate(job.closingDate)}</small> : null}
          </dd>
        </div>
        <div>
          <dt>Source</dt>
          <dd>
            {job.source} · {job.roleFamily}
          </dd>
        </div>
      </dl>

      {tags.length > 0 ? (
        <div className="job-skills">
          {visibleTags.map((tag) => (
            <span key={tag} className="skill-tag">
              {tag}
            </span>
          ))}
          {hiddenTags.length > 0 ? (
            <details className="skill-overflow">
              <summary>+{hiddenTags.length} more skills</summary>
              <div>
                {hiddenTags.map((tag) => (
                  <span key={tag} className="skill-tag">
                    {tag}
                  </span>
                ))}
              </div>
            </details>
          ) : null}
        </div>
      ) : (
        <p className="muted skill-none">No skills extracted from this listing yet.</p>
      )}
    </article>
  )
}
