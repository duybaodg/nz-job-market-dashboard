import { Bar, BarChart, CartesianGrid, Cell, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { SalaryDistribution } from '../../lib/analytics'

type SalaryDistributionChartProps = {
  distribution: SalaryDistribution
}

export function SalaryDistributionChart({ distribution }: SalaryDistributionChartProps) {
  if (distribution.suppressed) {
    return (
      <div className="chart-empty">
        {distribution.sampleSize} comparable permanent annual NZD midpoints in this cohort; at least 5 are needed before
        a distribution is shown. Hourly/daily, non-NZD, contract and missing salaries are excluded.
      </div>
    )
  }

  return (
    <div className="trend-stack">
      <div className="chart-frame" role="img" aria-label="Distribution of comparable permanent annual NZD salary midpoints">
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={distribution.bins} margin={{ top: 10, right: 12, left: 0, bottom: 0 }}>
            <CartesianGrid stroke="#e5ebe8" vertical={false} />
            <XAxis dataKey="label" tickLine={false} axisLine={false} interval={0} angle={-18} textAnchor="end" height={54} />
            <YAxis allowDecimals={false} tickLine={false} axisLine={false} width={34} />
            <Tooltip
              cursor={{ fill: '#f3f7f5' }}
              formatter={(value, _name, item) => [`${Number(value)} listings`, item.payload.label]}
            />
            <Bar dataKey="count" name="Listings">
              {distribution.bins.map((bin) => (
                <Cell key={bin.label} fill="#0f766e" />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      </div>

      <p className="chart-legend">
        {distribution.sampleSize} comparable midpoints{distribution.smallSample ? ' · small sample' : ''} · hourly/daily{' '}
        {distribution.excluded.hourlyOrDaily} · non-NZD {distribution.excluded.nonNzd} · contract {distribution.excluded.contractOrInternship} ·
        not advertised {distribution.excluded.missing}
      </p>

      <details className="chart-table">
        <summary>View salary distribution as a table</summary>
        <table>
          <thead>
            <tr>
              <th scope="col">Salary band (NZD/year)</th>
              <th scope="col">Listings</th>
            </tr>
          </thead>
          <tbody>
            {distribution.bins.map((bin) => (
              <tr key={bin.label}>
                <th scope="row">{bin.label}</th>
                <td>{bin.count}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </div>
  )
}
