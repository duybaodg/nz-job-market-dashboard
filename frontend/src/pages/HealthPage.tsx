import { useEffect, useState, type FormEvent } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Archive, CircleSlash, Clock, HeartPulse, Lock, PackageOpen, ShieldAlert } from 'lucide-react'
import { AppSidebar } from '../components/layout/AppSidebar'
import { StatCard } from '../components/dashboard/StatCard'
import { SourceHealthTable } from '../components/health/SourceHealthTable'
import { formatRelativeTime } from '../lib/format'
import {
  ApiError,
  clearStoredHealthPassword,
  getHealth,
  getStoredHealthPassword,
  storeHealthPassword,
} from '../services/api'

export function HealthPage() {
  const queryClient = useQueryClient()
  // sessionStorage only: the password is forgotten when the tab closes.
  const [password, setPassword] = useState<string | null>(() => getStoredHealthPassword())
  const [unlockError, setUnlockError] = useState<string | null>(null)
  const [isUnlocking, setIsUnlocking] = useState(false)

  const healthQuery = useQuery({
    queryKey: ['health', password],
    queryFn: () => getHealth(password as string),
    enabled: password !== null,
    refetchInterval: 30_000,
    staleTime: 30_000,
    retry: false,
  })

  // A 401 mid-session means the shared password changed (or was removed):
  // forget it and show the prompt again without a redirect.
  useEffect(() => {
    if (password && healthQuery.error instanceof ApiError && healthQuery.error.status === 401) {
      clearStoredHealthPassword()
      setPassword(null)
      setUnlockError('Your session expired. Enter the password to unlock crawler health again.')
    }
  }, [healthQuery.error, password])

  async function handleUnlock(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const candidate = String(new FormData(event.currentTarget).get('password') ?? '').trim()
    if (!candidate) {
      setUnlockError('Enter the health password.')
      return
    }

    setUnlockError(null)
    setIsUnlocking(true)
    try {
      const report = await getHealth(candidate)
      storeHealthPassword(candidate)
      queryClient.setQueryData(['health', candidate], report)
      setPassword(candidate)
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        setUnlockError('Incorrect password.')
      } else if (error instanceof ApiError && error.status === 429) {
        setUnlockError('Too many failed attempts. Wait five minutes and try again.')
      } else {
        setUnlockError('Could not reach the crawler health API.')
      }
    } finally {
      setIsUnlocking(false)
    }
  }

  if (password === null) {
    return (
      <div className="app-shell" id="health">
        <AppSidebar />
        <main>
          <header className="page-header">
            <div>
              <span className="eyebrow">Crawler operations</span>
              <h1>Crawler Health</h1>
              <p>Protected operational view. Enter the shared health password to continue.</p>
            </div>
          </header>

          <section className="panel health-lock" aria-label="Crawler health is locked">
            <div className="health-lock-icon" aria-hidden="true">
              <Lock />
            </div>
            <h2>Crawler health is locked</h2>
            <p>
              Source states, error strings and infrastructure hints are operational detail, so this view
              requires the shared health password.
            </p>
            <form className="health-lock-form" onSubmit={handleUnlock}>
              <label htmlFor="health-password">Health password</label>
              <div className="health-lock-row">
                <input
                  id="health-password"
                  name="password"
                  type="password"
                  autoComplete="current-password"
                  placeholder="Shared health password"
                  autoFocus
                />
                <button type="submit" disabled={isUnlocking}>
                  {isUnlocking ? 'Unlocking…' : 'Unlock'}
                </button>
              </div>
              {unlockError ? (
                <p className="health-lock-error" role="alert">
                  {unlockError}
                </p>
              ) : null}
            </form>
            <p className="health-lock-hint">
              Stored for this browser tab only, and sent as an Authorization header — never in the URL.
            </p>
          </section>
        </main>
      </div>
    )
  }

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

        {healthQuery.isError && !(healthQuery.error instanceof ApiError && healthQuery.error.status === 401) ? (
          <section className="empty-state">
            Could not load crawler health from the backend API (<code>/api/admin/health</code>).
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
