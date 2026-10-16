import { useQuery } from '@tanstack/react-query'
import { Archive, CircleSlash, Clock, HeartPulse, PackageOpen, ShieldAlert } from 'lucide-react'
import { AppSidebar } from '../components/layout/AppSidebar'
import { StatCard } from '../components/dashboard/StatCard'
import { SourceHealthTable } from '../components/health/SourceHealthTable'
import { formatRelativeTime } from '../lib/format'
import { getHealth } from '../services/api'

export function HealthPage() {
  const healthQuery = useQuery({ queryKey: ['health'], queryFn: getHealth, refetchInterval: 30_000 })
  const report = healthQuery.data
  const summary = report?.summary
  const sources = report?.sources ?? []

  return (
    <div className="app-shell" id="health">
      <AppSidebar />
      <main>
        <header className="page-header">
          <div>
            <span className="eyebrow">Crawler operations</span>
            <h1>Crawler Health</h1>
            <p>Per-source crawl status computed from existing crawl history. Read-only, cached for 30 seconds.</p>
          </div>
          <div className="header-actions">
            <div className="header-status" aria-label="Health report status">
              <span><i className="live-dot" aria-hidden="true" /> Live dataset</span>
              <strong>{summary?.totalSources ?? 0} sources · updated {formatRelativeTime(report?.generatedAt ?? null)}</strong>
            </div>
          </div>
        </header>

        {healthQuery.isError ? (
          <section className="empty-state">
            Could not load crawler health from the backend API on <code>http://localhost:5050</code>.
          </section>
        ) : null}

        <section className="stats-grid health-stats" aria-busy={healthQuery.isLoading} aria-label="Source health summary">
          <StatCard icon={HeartPulse} label="Healthy" value={summary?.healthy ?? 0} detail="Recent success with jobs" />
          <StatCard icon={ShieldAlert} label="Degraded" value={summary?.degraded ?? 0} detail="3+ failures or zero-yields" />
          <StatCard icon={Clock} label="Stale" value={summary?.stale ?? 0} detail="Overdue by 3x frequency" />
          <StatCard icon={PackageOpen} label="Empty" value={summary?.empty ?? 0} detail="Healthy but no IT roles" />
          <StatCard icon={CircleSlash} label="Disabled" value={summary?.disabled ?? 0} detail="Off in the registry" />
          <StatCard icon={Archive} label="Retired" value={summary?.retired ?? 0} detail="Removed from registry" />
        </section>

        <section className="panel jobs-panel" id="sources">
          <div className="panel-header">
            <div>
              <h2>All sources</h2>
              <p>
                {summary?.neverRun ? `${summary.neverRun} never run · ` : ''}
                Sort by any column; degraded and stale sources sort first by default.
              </p>
            </div>
            <span>{sources.length} sources</span>
          </div>
          <SourceHealthTable sources={sources} />
        </section>
      </main>
    </div>
  )
}
