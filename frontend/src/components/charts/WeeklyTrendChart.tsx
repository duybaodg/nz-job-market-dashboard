import { Bar, BarChart, CartesianGrid, Cell, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { WeeklyNewListingPoint } from '../../types/api'

type WeeklyTrendChartProps = {
  points: WeeklyNewListingPoint[]
}

/** Weekly new-listing columns. Incomplete weeks are a different colour and are
 *  also listed with their status in the table alternative below the chart. */
export function WeeklyTrendChart({ points }: WeeklyTrendChartProps) {
  if (points.length === 0) {
    return (
      <div className="chart-empty">
        No retained history yet. Weekly new-listing trends appear once daily snapshots accumulate — no trend is estimated
        from the current rows alone.
      </div>
    )
  }

  return (
    <div className="trend-stack">
      <div className="chart-frame" role="img" aria-label="Weekly new tracked listings from retained daily snapshots">
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={points} margin={{ top: 10, right: 12, left: 0, bottom: 0 }}>
            <CartesianGrid stroke="#e5ebe8" vertical={false} />
            <XAxis dataKey="weekStartLocal" tickLine={false} axisLine={false} />
            <YAxis allowDecimals={false} tickLine={false} axisLine={false} width={34} />
            <Tooltip
              cursor={{ fill: '#f3f7f5' }}
              formatter={(value, _name, item) => [
                `${Number(value).toLocaleString('en-NZ')} listings`,
                item.payload.isPartial ? 'Incomplete week' : 'Complete week',
              ]}
            />
            <Bar dataKey="count" name="New listings">
              {points.map((point) => (
                <Cell key={point.weekStartLocal} fill={point.isPartial ? '#d69e4c' : '#0f766e'} />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      </div>

      <p className="chart-legend">
        <span className="legend-swatch complete" aria-hidden="true" /> Complete week
        <span className="legend-swatch partial" aria-hidden="true" /> Incomplete week (collection or comparison still in progress)
      </p>

      <details className="chart-table">
        <summary>View weekly new listings as a table</summary>
        <table>
          <thead>
            <tr>
              <th scope="col">Week starting (NZ)</th>
              <th scope="col">New listings</th>
              <th scope="col">Week status</th>
              <th scope="col">Note</th>
            </tr>
          </thead>
          <tbody>
            {points.map((point) => (
              <tr key={point.weekStartLocal}>
                <th scope="row">{point.weekStartLocal}</th>
                <td>{point.count}</td>
                <td>{point.isPartial ? 'Incomplete' : 'Complete'}</td>
                <td>{point.note ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </div>
  )
}
