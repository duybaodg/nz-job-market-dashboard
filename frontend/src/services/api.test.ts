import { afterEach, describe, expect, it, vi } from 'vitest'
import { getHealth } from './api'

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
