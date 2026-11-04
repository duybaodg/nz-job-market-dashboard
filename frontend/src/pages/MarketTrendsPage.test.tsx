// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MarketTrendsPage } from './MarketTrendsPage'
import { historyFixture, jobFixture, overviewFixture } from '../types/contract.fixtures'

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

  it('hides the global unique-vacancy headline when filters are active', async () => {
    location.search = '?period=7&region=Auckland'
    getJobsMock.mockResolvedValue([jobFixture])
    getOverviewMock.mockResolvedValue(overviewFixture)
    getHistoryMock.mockResolvedValue(historyFixture)

    renderPage()

    expect((await screen.findAllByText('Active tracked listings')).length).toBeGreaterThan(0)
    expect(screen.queryByText('Unique vacancies (all listings)')).not.toBeInTheDocument()
  })

  it('shows the labelled global unique-vacancy headline without filters', async () => {
    location.search = '?period=7'
    getJobsMock.mockResolvedValue([jobFixture])
    getOverviewMock.mockResolvedValue(overviewFixture)
    getHistoryMock.mockResolvedValue(historyFixture)

    renderPage()

    expect(await screen.findByText('Unique vacancies (all active listings, as of now)')).toBeInTheDocument()
  })

  it('requests filtered history and withholds a comparison snapshots cannot support', async () => {
    location.search = '?period=7&skill=Python'
    getJobsMock.mockResolvedValue([jobFixture])
    getOverviewMock.mockResolvedValue(overviewFixture)
    getHistoryMock.mockResolvedValue({
      ...historyFixture,
      filter: {
        ...historyFixture.filter,
        skill: 'Python',
        hasFilters: true,
        historyFilterSupported: false,
        note: 'Historical snapshots do not retain skill tags, so a skill-filtered historical comparison is unavailable. Charts show all tracked listings.',
      },
      newListings: {
        ...historyFixture.newListings,
        currentPeriodCovered: false,
        change: {
          ...historyFixture.newListings.change,
          comparisonAvailable: false,
          percentChange: null,
          note: 'Comparison unavailable.',
        },
        note: 'Comparison unavailable: historical snapshots do not retain skill tags, so this cohort cannot be reconstructed. No substitute percentage is shown.',
      },
    })

    renderPage()

    await waitFor(() => expect(getHistoryMock).toHaveBeenCalledWith(7, expect.objectContaining({ skill: 'Python' })))
    expect((await screen.findAllByText(/do not retain skill tags/i)).length).toBeGreaterThan(0)
  })

  it('fetches retained history for the selected reporting period', async () => {
    const { rerender } = renderPage()

    await waitFor(() => expect(getHistoryMock).toHaveBeenCalledWith(15, expect.objectContaining({})))

    location.search = '?period=7'
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    rerender(
      <QueryClientProvider client={queryClient}>
        <MarketTrendsPage />
      </QueryClientProvider>,
    )

    await waitFor(() => expect(getHistoryMock).toHaveBeenCalledWith(7, expect.objectContaining({})))
  })
})
