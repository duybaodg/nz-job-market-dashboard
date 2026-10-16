import { afterEach, describe, expect, it, vi } from 'vitest'
import { getHealth, getHistory, getJobs } from './api'

describe('getJobs', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('passes every supported filter through the query string unmangled', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => [] })
    vi.stubGlobal('fetch', fetchMock)

    await getJobs({ region: 'Auckland', skill: 'Azure', search: 'data engineer', status: 'active', days: 7 })

    const [url] = fetchMock.mock.calls[0] as [string]
    expect(url).toBe(
      'http://localhost:5050/api/jobs?region=Auckland&skill=Azure&search=data+engineer&status=active&days=7',
    )
  })

  it('omits empty filters rather than sending blank parameters', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => [] })
    vi.stubGlobal('fetch', fetchMock)

    await getJobs({ status: 'all' })

    expect((fetchMock.mock.calls[0] as [string])[0]).toBe('http://localhost:5050/api/jobs?status=all')
  })
})

describe('getHistory', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('requests the selected reporting period so the label matches the count', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => ({}) })
    vi.stubGlobal('fetch', fetchMock)

    await getHistory(15)

    expect((fetchMock.mock.calls[0] as [string])[0]).toBe('http://localhost:5050/api/analytics/history?days=15')
  })
})

describe('getHealth', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('sends the password as a Bearer header and never in the URL', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ summary: {} }),
    })
    vi.stubGlobal('fetch', fetchMock)

    await getHealth('hunter2')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('http://localhost:5050/api/admin/health')
    expect(url).not.toContain('hunter2')
    expect(url).not.toContain('password')
    expect(init.headers).toEqual({ Authorization: 'Bearer hunter2' })
  })

  it('throws an ApiError carrying the HTTP status', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({ ok: false, status: 401, json: async () => ({}) }),
    )

    await expect(getHealth('nope')).rejects.toMatchObject({ status: 401 })
  })
})
