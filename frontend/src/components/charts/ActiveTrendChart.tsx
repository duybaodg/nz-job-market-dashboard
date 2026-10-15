import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { ActiveTrendPoint, SourceCoverageChange } from '../../types/api'

type ActiveTrendChartProps = {
  points: ActiveTrendPoint[]
  coverageChanges: SourceCoverageChange[]
}

function coverageNote(point: ActiveTrendPoint): string {
  if (!point.coverageChanged) return '—'
  const parts: string[] = []
  if (point.sourcesAdded.length > 0) parts.push(`added ${point.sourcesAdded.join(', ')}`)
  if (point.sourcesRemoved.length > 0) parts.push(`removed ${point.sourcesRemoved.join(', ')}`)
  return `Source coverage changed: ${parts.join('; ')}`
}

/**
 * Historical active-listing counts from retained snapshots. Source-coverage
 * changes are annotated on the line and spelled out in the table, so a new
 * source can never be read as demand growth on its own.
 */
export function ActiveTrendChart({ points, coverageChanges }: ActiveTrendChartProps) {
  if (points.length < 2) {
    return (
      <div className="chart-empty">
        Active-listing history needs at least two daily snapshots. {points.length === 1 ? 'One has been retained so far.' : 'None are retained yet.'}
      </div>
    )
  }

  return (
    <div className="trend-stack">
      <div className="chart-frame" role="img" aria-label="Historical active tracked listings from retained daily snapshots">
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={points} margin={{ top: 10, right: 12, left: 0, bottom: 0 }}>
            <CartesianGrid stroke="#e5ebe8" vertical={false} />
            <XAxis dataKey="snapshotDate" tickLine={false} axisLine={false} />
            <YAxis allowDecimals={false} tickLine={false} axisLine={false} width={38} />
            <Tooltip
              formatter={(value, _name, item) => [
                `${Number(value).toLocaleString('en-NZ')} active`,
                item.payload.coverageChanged ? 'Active (coverage changed)' : 'Active',
              ]}
            />
            <Line
              type="monotone"
              dataKey="activeCount"
              name="Active listings"
              stroke="#0f766e"
              strokeWidth={2}
              dot={(props) => {
                const { cx, cy, payload, index } = props as { cx: number; cy: number; payload: ActiveTrendPoint; index: number }
                return (
                  <circle
                    key={index}
                    cx={cx}
                    cy={cy}
                    r={payload.coverageChanged ? 5 : 3}
                    fill={payload.coverageChanged ? '#d69e4c' : '#0f766e'}
                  />
                )
              }}
            />
          </LineChart>
        </ResponsiveContainer>
      </div>

      <p className="chart-legend">
        <span className="legend-swatch complete" aria-hidden="true" /> Active listings
        <span className="legend-swatch partial" aria-hidden="true" /> Snapshot where source coverage changed
      </p>

      {coverageChanges.length > 0 ? (
        <div className="coverage-notes">
          <h3>Source-coverage changes</h3>
          <ul>
            {coverageChanges.map((change) => (
              <li key={change.snapshotDate}>
                <strong>{change.snapshotDate}</strong>{' '}
                {change.sourcesAdded.length > 0 ? `added ${change.sourcesAdded.join(', ')}` : null}
                {change.sourcesAdded.length > 0 && change.sourcesRemoved.length > 0 ? '; ' : ''}
                {change.sourcesRemoved.length > 0 ? `removed ${change.sourcesRemoved.join(', ')}` : null}
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      <details className="chart-table">
        <summary>View active-listing history as a table</summary>
        <table>
          <thead>
            <tr>
              <th scope="col">Snapshot date (NZ)</th>
              <th scope="col">Active listings</th>
              <th scope="col">Sources</th>
              <th scope="col">Coverage</th>
            </tr>
          </thead>
          <tbody>
            {points.map((point) => (
              <tr key={point.snapshotDate}>
                <th scope="row">{point.snapshotDate}</th>
                <td>{point.activeCount}</td>
                <td>{point.sourceCount}</td>
                <td>{coverageNote(point)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </div>
  )
}
