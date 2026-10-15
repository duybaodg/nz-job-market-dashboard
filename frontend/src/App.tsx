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

  if (pathname.startsWith('/health')) return <HealthPage />
  if (pathname.startsWith('/trends')) return <MarketTrendsPage />
  return <FindJobsPage />
}

export default App
