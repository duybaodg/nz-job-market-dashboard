import type { ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'

type StatCardProps = {
  label: string
  value: ReactNode
  detail?: string
  icon?: LucideIcon
}

export function StatCard({ label, value, detail, icon: Icon }: StatCardProps) {
  return (
    <section className="stat-card">
      <div className="stat-card-top">
        <span>{label}</span>
        {Icon ? <Icon aria-hidden="true" /> : null}
      </div>
      <strong>{value}</strong>
      {detail ? <small>{detail}</small> : null}
    </section>
  )
}
