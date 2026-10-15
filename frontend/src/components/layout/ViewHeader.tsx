import type { ReactNode } from 'react'
import { HeartPulse } from 'lucide-react'
import { formatNzDateTime } from '../../lib/format'
import type { DataStatus } from '../../types/api'

type ViewHeaderProps = {
  eyebrow: string
  title: string
  description: string
  status?: DataStatus
  actions?: ReactNode
}

/**
 * Compact view header: the title and a shared freshness indicator are the first
 * thing on every view so the reader knows what they are looking at and how old
 * it is before touching a filter.
 */
export function ViewHeader({ eyebrow, title, description, status, actions }: ViewHeaderProps) {
  const tone = status ? status.state.toLowerCase() : 'unknown'

  return (
    <header className="view-header">
      <div className="view-header-main">
        <span className="eyebrow">{eyebrow}</span>
        <h1>{title}</h1>
        <p>{description}</p>
      </div>
      <div className="view-header-side">
        <div className={`freshness-pill freshness-${tone}`} role="status" aria-label="Data freshness">
          <span className="live-dot" aria-hidden="true" />
          <div>
            <strong>{status ? `${status.state} data` : 'Freshness unavailable'}</strong>
            <small>
              {status
                ? `Updated ${formatNzDateTime(status.lastSuccessfulUpdateUtc)} · ${status.enabledSourcesWithRecentSuccess}/${status.enabledSources} sources`
                : 'Metrics have not loaded'}
            </small>
          </div>
        </div>
        <a className="health-link" href="/health">
          <HeartPulse aria-hidden="true" /> Crawler health
        </a>
        {actions}
      </div>
    </header>
  )
}
