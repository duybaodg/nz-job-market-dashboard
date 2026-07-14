import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { Job } from '../../types/api'

type SalaryRangeChartProps = {
  jobs: Job[]
}

export function SalaryRangeChart({ jobs }: SalaryRangeChartProps) {
  const data = jobs
    .filter((job) => job.salaryMin != null || job.salaryMax != null)
    .map((job) => ({
      title: job.title.length > 20 ? `${job.title.slice(0, 20)}...` : job.title,
      midpoint: (((job.salaryMin ?? job.salaryMax) ?? 0) + ((job.salaryMax ?? job.salaryMin) ?? 0)) / 2,
    }))

  if (data.length === 0) return <div className="chart-empty">No salary data in this view</div>

  return (
    <div className="chart-frame">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} margin={{ top: 10, right: 12, left: 6, bottom: 0 }}>
          <CartesianGrid stroke="#e5ebe8" vertical={false} />
          <XAxis dataKey="title" tickLine={false} axisLine={false} />
          <YAxis tickLine={false} axisLine={false} tickFormatter={(value) => `$${Number(value) / 1000}k`} />
          <Tooltip
            cursor={{ fill: '#f3f7f5' }}
            formatter={(value) => [`$${Number(value).toLocaleString('en-NZ')}`, 'Salary midpoint']}
          />
          <Bar dataKey="midpoint" fill="#b7791f" radius={[6, 6, 0, 0]} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
