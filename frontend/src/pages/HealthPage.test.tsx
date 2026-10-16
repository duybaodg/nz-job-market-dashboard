// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { HealthPage } from './HealthPage'
import { ApiError, HEALTH_PASSWORD_STORAGE_KEY } from '../services/api'
import type { HealthReport } from '../types/api'

const { getHealthMock } = vi.hoisted(() => ({ getHealthMock: vi.fn() }))

vi.mock('../services/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../services/api')>()
  return { ...actual, getHealth: getHealthMock }
})

function healthReport(): HealthReport {
  return {
    generatedAt: '2026-10-16T00:00:00Z',
    summary: {
      healthy: 0,
      degraded: 1,
      stale: 0,
      empty: 0,
      disabled: 0,
      neverRun: 0,
      retired: 0,
      totalSources: 1,
    },
    sources: [
      {
        source: 'Acme Analytics',
        enabled: true,
        parser: 'generic',
        method: 'http',
        lastCrawledAt: '2026-10-15T23:00:00Z',
        lastStatus: 'Failed',
        lastError: 'HTTP 403 from WAF',
        consecutiveFailures: 3,
        consecutiveZeroYield: 0,
        jobsYielded: 0,
        state: 'degraded',
        degradeReason: null,
        crawlFrequencyMinutes: 60,
      },
    ],
    retiredSources: [],
  }
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <HealthPage />
    </QueryClientProvider>,
  )
}

describe('HealthPage password gate', () => {
  beforeEach(() => {
    window.sessionStorage.clear()
    getHealthMock.mockReset()
  })

  afterEach(() => {
    cleanup()
  })

  it('shows the password prompt when locked and does not fetch', () => {
    renderPage()

    expect(screen.getByRole('heading', { name: /crawler health is locked/i })).toBeInTheDocument()
    expect(screen.getByLabelText(/health password/i)).toBeInTheDocument()
    expect(getHealthMock).not.toHaveBeenCalled()
  })

  it('unlocks and renders health on the correct password, storing it in sessionStorage only', async () => {
    const user = userEvent.setup()
    getHealthMock.mockResolvedValue(healthReport())
    renderPage()

    await user.type(screen.getByLabelText(/health password/i), 'letmein')
    await user.click(screen.getByRole('button', { name: /unlock/i }))

    expect(await screen.findByText('Acme Analytics')).toBeInTheDocument()
    expect(screen.getByText('All sources')).toBeInTheDocument()
    expect(getHealthMock).toHaveBeenCalledWith('letmein')
    expect(window.sessionStorage.getItem(HEALTH_PASSWORD_STORAGE_KEY)).toBe('letmein')
    expect(window.localStorage.getItem(HEALTH_PASSWORD_STORAGE_KEY)).toBeNull()
  })

  it('shows an inline error for a wrong password and stays locked', async () => {
    const user = userEvent.setup()
    getHealthMock.mockRejectedValue(new ApiError(401))
    renderPage()

    await user.type(screen.getByLabelText(/health password/i), 'wrong')
    await user.click(screen.getByRole('button', { name: /unlock/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/incorrect password/i)
    expect(screen.getByRole('heading', { name: /crawler health is locked/i })).toBeInTheDocument()
    expect(window.sessionStorage.getItem(HEALTH_PASSWORD_STORAGE_KEY)).toBeNull()
  })

  it('re-locks and clears storage on a 401 mid-session', async () => {
    window.sessionStorage.setItem(HEALTH_PASSWORD_STORAGE_KEY, 'stale-password')
    getHealthMock.mockRejectedValue(new ApiError(401))
    renderPage()

    expect(await screen.findByRole('alert')).toHaveTextContent(/session expired/i)
    expect(screen.getByRole('heading', { name: /crawler health is locked/i })).toBeInTheDocument()
    expect(window.sessionStorage.getItem(HEALTH_PASSWORD_STORAGE_KEY)).toBeNull()
  })

  it('surfaces the rate-limit message after too many failures', async () => {
    const user = userEvent.setup()
    getHealthMock.mockRejectedValue(new ApiError(429))
    renderPage()

    await user.type(screen.getByLabelText(/health password/i), 'wrong')
    await user.click(screen.getByRole('button', { name: /unlock/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/too many failed attempts/i)
  })
})
