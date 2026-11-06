// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CompareCohortsPage } from './CompareCohortsPage'
import { navigate } from '../hooks/useAppLocation'
import { comparisonFixture, jobFixture, overviewFixture } from '../types/contract.fixtures'

const { getComparisonMock, getJobsMock, getOverviewMock } = vi.hoisted(() => ({
  getComparisonMock: vi.fn(),
  getJobsMock: vi.fn(),
  getOverviewMock: vi.fn(),
}))

vi.mock('../services/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../services/api')>()
  return { ...actual, getComparison: getComparisonMock, getJobs: getJobsMock, getOverview: getOverviewMock }
})

const { location } = vi.hoisted(() => ({ location: { pathname: '/compare', search: '' } }))

vi.mock('../hooks/useAppLocation', () => ({
  useAppLocation: () => location,
  navigate: vi.fn(),
}))

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <CompareCohortsPage />
    </QueryClientProvider>,
  )
}

describe('CompareCohortsPage', () => {
  beforeEach(() => {
    location.pathname = '/compare'
    location.search = '?days=7&aRole=data-analyst&bRole=data-engineer'
    getComparisonMock.mockReset().mockResolvedValue(comparisonFixture)
    getJobsMock.mockReset().mockResolvedValue([jobFixture])
    getOverviewMock.mockReset().mockResolvedValue(overviewFixture)
    vi.mocked(navigate).mockClear()
  })

  afterEach(cleanup)

  it('shows a loading state instead of measured zeros before both cohorts arrive', async () => {
    getComparisonMock.mockReturnValue(new Promise(() => {}))
    renderPage()

    expect(await screen.findByText('Loading both cohorts from the same capture…')).toBeInTheDocument()
    expect(screen.getByText('Loading counts, skill shares and salary samples for both cohorts…')).toBeInTheDocument()
    expect(screen.queryByText('0')).not.toBeInTheDocument()
  })

  it('distinguishes a request failure from an empty cohort', async () => {
    getComparisonMock.mockRejectedValue(new Error('comparison unavailable'))
    renderPage()

    expect(await screen.findByText(/comparison could not be loaded/i)).toBeInTheDocument()
    expect(screen.getByText(/not zero demand or an empty cohort/i)).toBeInTheDocument()
  })

  it('renders aligned counts, percentage-point skill differences and descriptive salary suppression', async () => {
    renderPage()

    expect(await screen.findByRole('heading', { name: 'Compare cohorts' })).toBeInTheDocument()
    expect(await screen.findByText('Cohort A · Data Analyst')).toBeInTheDocument()
    expect(await screen.findByText('Cohort B · Data Engineer')).toBeInTheDocument()
    expect(screen.getAllByText('13').length).toBeGreaterThan(0)
    expect(screen.getAllByText('20').length).toBeGreaterThan(0)
    expect(screen.getAllByText('-9.2 pp').length).toBeGreaterThan(0)
    expect(screen.getAllByText('+24.6 pp').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Suppressed (<5 comparable samples)').length).toBeGreaterThan(0)
    expect(screen.getAllByText(/small sample \(5–9\)/).length).toBeGreaterThan(0)
    expect(screen.getByText(/Skill-share history unavailable/i)).toBeInTheDocument()
    expect(screen.getByText(/14 complete skill-captured days/i)).toBeInTheDocument()
    expect(screen.getAllByText(/no causal salary-premium claim/i).length).toBeGreaterThan(0)
    expect(screen.queryByText(/salary premium/i)).not.toBeInTheDocument()
  })

  it('opens either cohort with its context preserved', async () => {
    renderPage()

    const links = await screen.findAllByRole('link', { name: /Open cohort A listings/i })
    expect(links.length).toBeGreaterThan(0)
    expect(links[0]).toHaveAttribute('href', expect.stringContaining('dataSubRole=data-analyst'))
    expect(links[0]).toHaveAttribute('href', expect.stringContaining('roleFamily=Data+%26+Analytics'))

    const linkB = (await screen.findAllByRole('link', { name: /Open cohort B listings/i }))[0]
    expect(linkB).toHaveAttribute('href', expect.stringContaining('dataSubRole=data-engineer'))
  })

  it('persists the reporting period and cohort definitions in the URL', async () => {
    location.search = '?days=7&aRole=data-scientist&bRole=leadership'
    renderPage()

    const period = await screen.findByLabelText('Comparison reporting period')
    fireEvent.change(period, { target: { value: '15' } })

    expect(vi.mocked(navigate)).toHaveBeenCalledWith(expect.stringContaining('days=15'))
    expect(vi.mocked(navigate)).toHaveBeenCalledWith(expect.stringContaining('aRole=data-scientist'))
    expect(vi.mocked(navigate)).toHaveBeenCalledWith(expect.stringContaining('bRole=leadership'))
  })

  it('swaps the two cohorts without losing the selected definitions', async () => {
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Swap A and B' }))
    expect(vi.mocked(navigate)).toHaveBeenCalledWith(expect.stringContaining('aRole=data-engineer'))
    expect(vi.mocked(navigate)).toHaveBeenCalledWith(expect.stringContaining('bRole=data-analyst'))
  })
})
