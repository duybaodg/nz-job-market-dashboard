export function PageHeader() {
  return (
    <header className="page-header">
      <div>
        <h1>Job Market Overview</h1>
        <p>Market intelligence for New Zealand technology roles.</p>
      </div>
      <div className="header-status" aria-label="Dashboard status">
        <span>Live workspace</span>
        <strong>Seed data</strong>
      </div>
    </header>
  )
}
