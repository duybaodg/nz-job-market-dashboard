// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FindJobsPage } from './FindJobsPage'
import { overviewFixture } from '../types/contract.fixtures'

const { getJobsMock, getOverviewMock } = vi.hoisted(() => ({
  getJobsMock: vi.fn(),
  getOverviewMock: vi.fn(),
}))

vi.mock('../services/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../services/api')>()
  return { ...actual, getJobs: getJobsMock, getOverview: getOverviewMock }
})

const { location } = vi.hoisted(() => ({ location: { pathname: '/jobs', search: '' } }))

vi.mock('../hooks/useAppLocation', () => ({
  useAppLocation: () => location,
  navigate: vi.fn(),
}))

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <FindJobsPage />
    </QueryClientProvider>,
  )
}

describe('FindJobsPage asynchronous states', () => {
  beforeEach(() => {
    location.pathname = '/jobs'
    location.search = ''
    getJobsMock.mockReset()
    getOverviewMock.mockReset()
  })

  afterEach(cleanup)

  it('shows loading instead of a measured zero matching count', async () => {
    const pending = new Promise<never>(() => {})
    getJobsMock.mockReturnValue(pending)
    getOverviewMock.mockResolvedValue(overviewFixture)

    renderPage()

    expect(await screen.findByText('Loading matching roles…')).toBeInTheDocument()
    expect(screen.queryByText(/0 matching active roles/)).not.toBeInTheDocument()
    expect(screen.getAllByText('Loading…').length).toBeGreaterThan(0)
  })

  it('keeps a failed request distinct from a successful empty result', async () => {
    getJobsMock.mockRejectedValue(new Error('jobs unavailable'))
    getOverviewMock.mockResolvedValue(overviewFixture)

    renderPage()

    expect(await screen.findByText('Results unavailable')).toBeInTheDocument()
    expect(screen.getByText(/not an empty result/i)).toBeInTheDocument()
    expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(0)
  })
})
