import { Cell, Legend, Pie, PieChart, ResponsiveContainer, Tooltip } from 'recharts'
import type { JobsByRegion } from '../../types/api'

const colors = ['#17796f', '#d69e4c', '#487f99', '#7c6ba8', '#d06b58', '#739b5d', '#b6678f', '#66757f']

type JobsByRegionChartProps = {
  data: JobsByRegion[]
}

export function JobsByRegionChart({ data }: JobsByRegionChartProps) {
  if (data.length === 0) return <div className="chart-empty">No regional data yet</div>

  return (
    <div className="chart-frame" role="img" aria-label="Donut chart showing the share of jobs in each region">
      <ResponsiveContainer width="100%" height="100%">
        <PieChart>
          <Pie data={data} dataKey="jobCount" nameKey="region" innerRadius={58} outerRadius={92} paddingAngle={2} isAnimationActive={false}>
            {data.map(({ region }, index) => <Cell key={region} fill={colors[index % colors.length]} />)}
          </Pie>
          <Tooltip formatter={(value) => [Number(value).toLocaleString('en-NZ'), 'Jobs']} />
          <Legend iconType="circle" iconSize={8} />
        </PieChart>
      </ResponsiveContainer>
    </div>
  )
}
