import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { JobsByRegion } from '../../types/api'

type JobsByRegionChartProps = {
  data: JobsByRegion[]
}

export function JobsByRegionChart({ data }: JobsByRegionChartProps) {
  if (data.length === 0) return <div className="chart-empty">No regional data yet</div>

  return (
    <div className="chart-frame">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} margin={{ top: 10, right: 8, left: -20, bottom: 0 }}>
          <CartesianGrid stroke="#e5ebe8" vertical={false} />
          <XAxis dataKey="region" tickLine={false} axisLine={false} />
          <YAxis allowDecimals={false} tickLine={false} axisLine={false} />
          <Tooltip cursor={{ fill: '#f3f7f5' }} />
          <Bar dataKey="jobCount" name="Jobs" fill="#17796f" radius={[6, 6, 0, 0]} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
