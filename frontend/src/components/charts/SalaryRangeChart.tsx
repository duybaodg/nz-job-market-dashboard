import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { isComparableAnnual, salaryMidpoint } from '../../lib/salary'
import type { Job } from '../../types/api'

type SalaryRangeChartProps = {
  jobs: Job[]
}

/** Product guardrail mirroring the backend salary metric. */
const MINIMUM_SAMPLE_SIZE = 5

export function SalaryRangeChart({ jobs }: SalaryRangeChartProps) {
  const roles = new Map<string, { title: string; salaries: number[] }>()

  for (const job of jobs) {
    // Only comparable permanent annual NZD amounts; hourly/daily contract
    // rates and foreign currencies are never mixed into this chart.
    if (!isComparableAnnual(job)) continue
    const midpoint = salaryMidpoint(job)
    if (midpoint == null) continue
    const key = job.title.trim().toLowerCase()
    const role = roles.get(key) ?? { title: job.title.trim(), salaries: [] }
    role.salaries.push(midpoint)
    roles.set(key, role)
  }

  const data = [...roles.values()]
    .filter((role) => role.salaries.length >= MINIMUM_SAMPLE_SIZE)
    .toSorted((a, b) => b.salaries.length - a.salaries.length || a.title.localeCompare(b.title))
    .slice(0, 10)
    .map(({ title, salaries }) => {
      const sorted = salaries.toSorted((a, b) => a - b)
      const middle = Math.floor(sorted.length / 2)
      const midpoint = sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2
      return {
        title: title.length > 28 ? `${title.slice(0, 27)}…` : title,
        midpoint,
        jobs: salaries.length,
        smallSample: salaries.length <= 9,
      }
    })
    .toReversed()

  if (data.length === 0) {
    return (
      <div className="chart-empty">
        No role has {MINIMUM_SAMPLE_SIZE}+ comparable permanent annual NZD salaries yet, so no group is shown.
      </div>
    )
  }

  return (
    <div className="chart-frame salary-chart" role="img" aria-label="Horizontal bar chart ranking median annual NZD salary midpoint by role, minimum five comparable listings">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ top: 6, right: 20, left: 24, bottom: 0 }}>
          <CartesianGrid stroke="#e5ebe8" horizontal={false} />
          <XAxis type="number" tickLine={false} axisLine={false} tickFormatter={(value) => `$${Number(value) / 1000}k`} />
          <YAxis type="category" dataKey="title" width={150} tickLine={false} axisLine={false} />
          <Tooltip
            cursor={{ fill: '#f3f7f5' }}
            formatter={(value, name, item) => name === 'midpoint'
              ? [
                  `$${Number(value).toLocaleString('en-NZ')}`,
                  `Median annual NZD midpoint (${item.payload.jobs} listings${item.payload.smallSample ? ', small sample' : ''})`,
                ]
              : [value, name]}
          />
          <Bar dataKey="midpoint" fill="#b7791f" radius={[0, 6, 6, 0]} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
