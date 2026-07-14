import type { ReactNode } from 'react'

type ChartCardProps = {
  title: string
  detail?: string
  children: ReactNode
}

export function ChartCard({ title, detail, children }: ChartCardProps) {
  return (
    <section className="panel">
      <div className="panel-header">
        <div>
          <h2>{title}</h2>
          {detail ? <p>{detail}</p> : null}
        </div>
      </div>
      {children}
    </section>
  )
}
