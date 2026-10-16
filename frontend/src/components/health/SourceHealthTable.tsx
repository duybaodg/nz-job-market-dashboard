import { useMemo, useState } from 'react'
import { ArrowDown, ArrowUp, ChevronsUpDown } from 'lucide-react'
import { formatRelativeTime } from '../../lib/format'
import type { SourceHealth, SourceHealthState } from '../../types/api'

const stateRank: Record<SourceHealthState, number> = {
  degraded: 0,
  stale: 1,
  'never-run': 2,
  empty: 3,
  disabled: 4,
  retired: 5,
  healthy: 6,
}

const stateLabels: Record<SourceHealthState, string> = {
  healthy: 'Healthy',
  empty: 'Empty',
  degraded: 'Degraded',
  stale: 'Stale',
  disabled: 'Disabled',
  'never-run': 'Never run',
  retired: 'Retired',
}

// Retired rows live in their own bucket on the report, never in the active
// source list this table renders, so they are not a filter option here.
const activeStates = (Object.keys(stateLabels) as SourceHealthState[]).filter((state) => state !== 'retired')

type SortKey = 'state' | 'source' | 'jobs' | 'lastCrawl' | 'failures' | 'zeroYield'
type SortDirection = 'asc' | 'desc'

type SourceHealthTableProps = {
  sources: SourceHealth[]
}

type SortHeaderProps = {
  label: string
  sortKey: SortKey
  sort: { key: SortKey; direction: SortDirection }
  onSort: (key: SortKey) => void
}

function SortHeader({ label, sortKey, sort, onSort }: SortHeaderProps) {
  const isActive = sort.key === sortKey
  const Icon = !isActive ? ChevronsUpDown : sort.direction === 'asc' ? ArrowUp : ArrowDown

  return (
    <button type="button" className={`sort-button${isActive ? ' active' : ''}`} onClick={() => onSort(sortKey)}>
      {label}
      <Icon aria-hidden="true" />
    </button>
  )
}

function compare(a: SourceHealth, b: SourceHealth, key: SortKey) {
  switch (key) {
    case 'state':
      return stateRank[a.state] - stateRank[b.state]
    case 'source':
      return a.source.localeCompare(b.source)
    case 'jobs':
      return a.jobsYielded - b.jobsYielded
    case 'lastCrawl':
      return new Date(a.lastCrawledAt ?? 0).getTime() - new Date(b.lastCrawledAt ?? 0).getTime()
    case 'failures':
      return a.consecutiveFailures - b.consecutiveFailures
    case 'zeroYield':
      return a.consecutiveZeroYield - b.consecutiveZeroYield
  }
}

function errorText(source: SourceHealth) {
  return source.lastError ?? source.degradeReason ?? '—'
}

export function SourceHealthTable({ sources }: SourceHealthTableProps) {
  const [stateFilter, setStateFilter] = useState<'all' | SourceHealthState>('all')
  const [sort, setSort] = useState<{ key: SortKey; direction: SortDirection }>({ key: 'state', direction: 'asc' })

  const rows = useMemo(() => {
    const filtered = stateFilter === 'all' ? sources : sources.filter((source) => source.state === stateFilter)

    return [...filtered].sort((a, b) => {
      const primary = compare(a, b, sort.key)
      const adjusted = sort.direction === 'asc' ? primary : -primary
      return adjusted !== 0 ? adjusted : a.source.localeCompare(b.source)
    })
  }, [sources, stateFilter, sort])

  function toggleSort(key: SortKey) {
    setSort((current) =>
      current.key === key
        ? { key, direction: current.direction === 'asc' ? 'desc' : 'asc' }
        : { key, direction: 'asc' },
    )
  }

  return (
    <>
      <div className="health-toolbar">
        <label>
          <span>Filter by state</span>
          <select value={stateFilter} onChange={(event) => setStateFilter(event.target.value as 'all' | SourceHealthState)}>
            <option value="all">All states ({sources.length})</option>
            {activeStates.map((state) => (
              <option key={state} value={state}>
                {stateLabels[state]} ({sources.filter((source) => source.state === state).length})
              </option>
            ))}
          </select>
        </label>
        <span className="muted">Showing {rows.length} of {sources.length} sources · degraded and stale first</span>
      </div>

      {rows.length === 0 ? (
        <div className="table-empty">No sources match the selected state.</div>
      ) : (
        <div className="table-wrap">
          <table className="health-table">
            <thead>
              <tr>
                <th><SortHeader label="Source" sortKey="source" sort={sort} onSort={toggleSort} /></th>
                <th><SortHeader label="State" sortKey="state" sort={sort} onSort={toggleSort} /></th>
                <th><SortHeader label="Jobs" sortKey="jobs" sort={sort} onSort={toggleSort} /></th>
                <th><SortHeader label="Last crawl" sortKey="lastCrawl" sort={sort} onSort={toggleSort} /></th>
                <th><SortHeader label="Failures" sortKey="failures" sort={sort} onSort={toggleSort} /></th>
                <th><SortHeader label="Zero yield" sortKey="zeroYield" sort={sort} onSort={toggleSort} /></th>
                <th>Last error / signal</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((source) => (
                <tr key={source.source}>
                  <td data-label="Source">
                    <div className="role-cell">
                      <strong>{source.source}</strong>
                      <span>{source.parser} · {source.method}</span>
                    </div>
                  </td>
                  <td data-label="State">
                    <span className={`health-badge ${source.state}`} title={source.degradeReason ?? undefined}>
                      {stateLabels[source.state]}
                    </span>
                  </td>
                  <td data-label="Jobs">{source.jobsYielded}</td>
                  <td data-label="Last crawl">
                    <div className="role-cell">
                      <span>{formatRelativeTime(source.lastCrawledAt)}</span>
                      <span>{source.lastStatus ?? 'No runs'}</span>
                    </div>
                  </td>
                  <td data-label="Failures">
                    <span className={source.consecutiveFailures >= 3 ? 'health-count-alert' : undefined}>
                      {source.consecutiveFailures}
                    </span>
                  </td>
                  <td data-label="Zero yield">
                    <span className={source.consecutiveZeroYield >= 3 ? 'health-count-alert' : undefined}>
                      {source.consecutiveZeroYield}
                    </span>
                  </td>
                  <td data-label="Last error / signal">
                    <span className="health-error" title={errorText(source)}>{errorText(source)}</span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}
