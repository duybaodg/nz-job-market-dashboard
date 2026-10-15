import { formatNzDateTime } from '../../lib/format'
import type { DataStatus } from '../../types/api'

export function DataStatusBanner({ status }: { status: DataStatus }) {
  const tone = status.state === 'Fresh' ? 'ok' : status.state === 'Partial' ? 'warn' : 'alert'

  return (
    <section className={`data-status data-status-${tone}`} role="status" aria-label="Data freshness detail">
      <strong>{status.state} data</strong>
      <span>{status.message}</span>
      <span className="muted">
        Last successful update {formatNzDateTime(status.lastSuccessfulUpdateUtc)} ·{' '}
        {status.enabledSourcesWithRecentSuccess}/{status.enabledSources} enabled sources refreshed
      </span>
    </section>
  )
}
