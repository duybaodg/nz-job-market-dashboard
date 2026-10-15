type PageHeaderProps = {
  jobCount: number | null
  sourceCount: number | null
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
          <span><i className="live-dot" aria-hidden="true" /> Tracked NZ tech listings</span>
          <strong>
            {jobCount == null
              ? 'Metrics unavailable'
              : `${jobCount} jobs · ${sourceCount ?? 0} sources`}
          </strong>
        </div>
      </div>
    </header>
  )
}
