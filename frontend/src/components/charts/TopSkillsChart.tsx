import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { SkillDemand } from '../../types/api'

type TopSkillsChartProps = {
  data: SkillDemand[]
}

export function TopSkillsChart({ data }: TopSkillsChartProps) {
  if (data.length === 0) return <div className="chart-empty">No skill data yet</div>

  return (
    <div className="chart-frame">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ top: 10, right: 18, left: 28, bottom: 0 }}>
          <CartesianGrid stroke="#e5ebe8" horizontal={false} />
          <XAxis type="number" allowDecimals={false} tickLine={false} axisLine={false} />
          <YAxis
            dataKey="skillName"
            type="category"
            width={92}
            tickLine={false}
            axisLine={false}
          />
          <Tooltip cursor={{ fill: '#f3f7f5' }} />
          <Bar dataKey="jobCount" name="Jobs" fill="#3867a6" radius={[0, 6, 6, 0]} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
