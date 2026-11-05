// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FindJobsPage } from './FindJobsPage'
import { overviewFixture, jobFixture } from '../types/contract.fixtures'

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

  it('states that CSV export covers all matching records, not only the visible page', async () => {
    const jobs = Array.from({ length: 15 }, (_, index) => ({
      ...jobFixture,
      id: `listing-${index}`,
      title: `Data Engineer ${index}`,
      url: `https://example.com/${index}`,
    }))
    getJobsMock.mockResolvedValue(jobs)
    getOverviewMock.mockResolvedValue(overviewFixture)

    renderPage()

    expect(await screen.findByText('Exports all 15 matching records, not just this page.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Export CSV/ })).toHaveAttribute('aria-describedby', 'csv-export-scope')
    expect(screen.getByText(/Showing/)).toHaveTextContent('Showing 10 of 15 matching roles')
  })
})
