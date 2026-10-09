import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { Job } from '../../types/api'

type SalaryRangeChartProps = {
  jobs: Job[]
}

export function SalaryRangeChart({ jobs }: SalaryRangeChartProps) {
  const roles = new Map<string, { title: string; salaries: number[] }>()

  for (const job of jobs) {
    if (job.salaryMin == null && job.salaryMax == null) continue
    const key = job.title.trim().toLowerCase()
    const role = roles.get(key) ?? { title: job.title.trim(), salaries: [] }
    role.salaries.push((((job.salaryMin ?? job.salaryMax) ?? 0) + ((job.salaryMax ?? job.salaryMin) ?? 0)) / 2)
    roles.set(key, role)
  }

  const data = [...roles.values()]
    .toSorted((a, b) => b.salaries.length - a.salaries.length || a.title.localeCompare(b.title))
    .slice(0, 10)
    .map(({ title, salaries }) => {
      const sorted = salaries.toSorted((a, b) => a - b)
      const middle = Math.floor(sorted.length / 2)
      const midpoint = sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2
      return { title: title.length > 28 ? `${title.slice(0, 27)}…` : title, midpoint, jobs: salaries.length }
    })
    .toReversed()

  if (data.length === 0) return <div className="chart-empty">No salary data in this view</div>

  return (
    <div className="chart-frame salary-chart" role="img" aria-label="Horizontal bar chart ranking median salary midpoint by role">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ top: 6, right: 20, left: 24, bottom: 0 }}>
          <CartesianGrid stroke="#e5ebe8" horizontal={false} />
          <XAxis type="number" tickLine={false} axisLine={false} tickFormatter={(value) => `$${Number(value) / 1000}k`} />
          <YAxis type="category" dataKey="title" width={150} tickLine={false} axisLine={false} />
          <Tooltip
            cursor={{ fill: '#f3f7f5' }}
            formatter={(value, name, item) => name === 'midpoint'
              ? [`$${Number(value).toLocaleString('en-NZ')}`, `Median midpoint (${item.payload.jobs} jobs)`]
              : [value, name]}
          />
          <Bar dataKey="midpoint" fill="#b7791f" radius={[0, 6, 6, 0]} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
