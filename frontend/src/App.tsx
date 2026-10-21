import { useAppLocation } from './hooks/useAppLocation'
import { FindJobsPage } from './pages/FindJobsPage'
import { HealthPage } from './pages/HealthPage'
import { MarketTrendsPage } from './pages/MarketTrendsPage'

/**
 * Existing pathname-based routing, extended with the two audience views:
 * `/jobs` (default), `/trends` and `/health`.
 */
function App() {
  const { pathname } = useAppLocation()

  const page = pathname.startsWith('/health')
    ? <HealthPage />
    : pathname.startsWith('/trends')
      ? <MarketTrendsPage />
      : <FindJobsPage />

  return (
    <>
      <a className="skip-link" href="#main-content">Skip to content</a>
      {page}
    </>
  )
}

export default App
