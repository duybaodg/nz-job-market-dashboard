// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MarketTrendsPage } from './MarketTrendsPage'

const { getHistoryMock, getJobsMock, getOverviewMock } = vi.hoisted(() => ({
  getHistoryMock: vi.fn(),
  getJobsMock: vi.fn(),
  getOverviewMock: vi.fn(),
}))

const { location } = vi.hoisted(() => ({ location: { pathname: '/trends', search: '?period=15' } }))

vi.mock('../services/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../services/api')>()
  return { ...actual, getHistory: getHistoryMock, getJobs: getJobsMock, getOverview: getOverviewMock }
})

vi.mock('../hooks/useAppLocation', () => ({
  useAppLocation: () => location,
  navigate: vi.fn(),
}))

// Recharts needs layout measurements that jsdom does not provide.
vi.mock('../components/charts/WeeklyTrendChart', () => ({ WeeklyTrendChart: () => null }))
vi.mock('../components/charts/ActiveTrendChart', () => ({ ActiveTrendChart: () => null }))
vi.mock('../components/charts/SalaryDistributionChart', () => ({ SalaryDistributionChart: () => null }))

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MarketTrendsPage />
    </QueryClientProvider>,
  )
}

describe('MarketTrendsPage reporting period', () => {
  beforeEach(() => {
    location.pathname = '/trends'
    location.search = '?period=15'
    getHistoryMock.mockReset().mockResolvedValue(null)
    getJobsMock.mockReset().mockResolvedValue([])
    getOverviewMock.mockReset().mockResolvedValue(null)
  })

  afterEach(cleanup)

  it('fetches retained history for the selected reporting period', async () => {
    const { rerender } = renderPage()

    await waitFor(() => expect(getHistoryMock).toHaveBeenCalledWith(15))

    location.search = '?period=7'
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    rerender(
      <QueryClientProvider client={queryClient}>
        <MarketTrendsPage />
      </QueryClientProvider>,
    )

    await waitFor(() => expect(getHistoryMock).toHaveBeenCalledWith(7))
  })
})
