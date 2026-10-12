type PageHeaderProps = {
  jobCount: number
  sourceCount: number
}

export function PageHeader({ jobCount, sourceCount }: PageHeaderProps) {
  return (
    <header className="page-header">
      <div>
        <span className="eyebrow">Aotearoa technology hiring</span>
        <h1>Job Market Overview</h1>
        <p>Track demand, salary signals, and hiring activity across live New Zealand listings.</p>
      </div>
      <div className="header-actions">
        <div className="header-status" aria-label="Dashboard status">
          <span><i className="live-dot" aria-hidden="true" /> Live dataset</span>
          <strong>{jobCount} jobs · {sourceCount} sources</strong>
        </div>
      </div>
    </header>
  )
}
