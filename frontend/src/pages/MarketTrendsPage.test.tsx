// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act } from 'react'
import { MarketTrendsPage } from './MarketTrendsPage'
import { navigate } from '../hooks/useAppLocation'
import { historyFixture, jobFixture, overviewFixture, qualityFixture } from '../types/contract.fixtures'
import type { History } from '../types/api'

const { getHistoryMock, getJobsMock, getOverviewMock, getQualityMock } = vi.hoisted(() => ({
  getHistoryMock: vi.fn(),
  getJobsMock: vi.fn(),
  getOverviewMock: vi.fn(),
  getQualityMock: vi.fn(),
}))

const { location } = vi.hoisted(() => ({ location: { pathname: '/trends', search: '?period=15' } }))

vi.mock('../services/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../services/api')>()
  return { ...actual, getHistory: getHistoryMock, getJobs: getJobsMock, getOverview: getOverviewMock, getQuality: getQualityMock }
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
    getQualityMock.mockReset().mockResolvedValue(qualityFixture)
  })

  afterEach(cleanup)

  it('never presents a measured zero while the initial requests are pending', async () => {
    location.search = '?period=7'
    const pending = new Promise<never>(() => {})
    getJobsMock.mockReturnValue(pending)
    getOverviewMock.mockReturnValue(pending)
    getHistoryMock.mockReturnValue(pending)

    renderPage()

    expect((await screen.findAllByText('Loading…')).length).toBeGreaterThan(0)
    expect(screen.queryByText('0')).not.toBeInTheDocument()
    expect(screen.getAllByRole('status').some((status) => /Loading current market metrics/.test(status.textContent ?? ''))).toBe(true)
  })

  it('shows a load failure instead of zero when the current metrics request fails', async () => {
    location.search = '?period=7'
    getJobsMock.mockRejectedValue(new Error('jobs unavailable'))
    getOverviewMock.mockRejectedValue(new Error('overview unavailable'))
    getHistoryMock.mockResolvedValue(historyFixture)

    renderPage()

    expect(await screen.findByText(/not zero demand/i)).toBeInTheDocument()
    expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(0)
    expect(screen.queryByText('0')).not.toBeInTheDocument()
  })

  it('does not let a late response for an old filter overwrite the current cohort', async () => {
    location.search = '?period=7&region=Auckland'
    let resolveOld: (value: History) => void = () => {}
    const oldRequest = new Promise<History>((resolve) => {
      resolveOld = resolve
    })
    const wellingtonHistory = {
      ...historyFixture,
      latestActiveCount: 222,
      latestSnapshotDate: '2026-10-17',
      filter: { ...historyFixture.filter, region: 'Wellington', hasFilters: true },
    }
    getJobsMock.mockResolvedValue([jobFixture])
    getOverviewMock.mockResolvedValue(overviewFixture)
    getHistoryMock.mockImplementation((_days: number, filters: { region?: string }) =>
      filters?.region === 'Auckland' ? oldRequest : Promise.resolve(wellingtonHistory),
    )

    const { rerender } = renderPage()
    location.search = '?period=7&region=Wellington'
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    rerender(
      <QueryClientProvider client={queryClient}>
        <MarketTrendsPage />
      </QueryClientProvider>,
    )

    expect(await screen.findByText(/222 listings on the 2026-10-17 snapshot/)).toBeInTheDocument()

    await act(async () => {
      resolveOld({ ...historyFixture, latestActiveCount: 111, latestSnapshotDate: '2026-10-17' })
    })

    await waitFor(() => expect(screen.getByText(/222 listings on the 2026-10-17 snapshot/)).toBeInTheDocument())
    expect(screen.queryByText(/111 listings on the 2026-10-17 snapshot/)).not.toBeInTheDocument()
  })

  it('clears cohort filters while keeping the selected reporting period', async () => {
    location.search = '?period=15&region=Auckland&roleFamily=Data+%26+Analytics'
    getJobsMock.mockResolvedValue([jobFixture])
    getOverviewMock.mockResolvedValue(overviewFixture)
    getHistoryMock.mockResolvedValue(historyFixture)

    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Clear all' }))
    expect(vi.mocked(navigate)).toHaveBeenCalledWith('/trends?period=15')
  })

  it('hides the global unique-vacancy headline when filters are active', async () => {
    location.search = '?period=7&region=Auckland'
    getJobsMock.mockResolvedValue([jobFixture])
    getOverviewMock.mockResolvedValue(overviewFixture)
    getHistoryMock.mockResolvedValue(historyFixture)

    renderPage()

    expect((await screen.findAllByText(/Active tracked listings/)).length).toBeGreaterThan(0)
    expect(screen.queryByText('Unique vacancies (all active listings, as of now)')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Remove filter Region: Auckland/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Clear all' })).toBeInTheDocument()
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

  it('renders every public quality measure with its denominator and never invents accuracy', async () => {
    getJobsMock.mockResolvedValue([jobFixture])
    getOverviewMock.mockResolvedValue(overviewFixture)
    getHistoryMock.mockResolvedValue(historyFixture)

    renderPage()

    expect(await screen.findByText('Data quality summary')).toBeInTheDocument()
    expect(await screen.findByText('41 of 44 (93.2%)')).toBeInTheDocument()
    expect(await screen.findByText('41 / 44')).toBeInTheDocument()
    expect(await screen.findByText(/Not published/)).toBeInTheDocument()
    expect(screen.queryByText(/100\.0% correct/)).not.toBeInTheDocument()
  })
})
