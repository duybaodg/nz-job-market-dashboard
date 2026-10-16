import type { Freshness, HealthReport, History, Job, JobsByRegion, Overview, SkillDemand } from '../types/api'

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5050'

/** Error carrying the HTTP status so callers can react to 401/429 specifically. */
export class ApiError extends Error {
  readonly status: number

  constructor(status: number) {
    super(`API request failed: ${status}`)
    this.name = 'ApiError'
    this.status = status
  }
}

async function getJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiBaseUrl}${path}`, init)

  if (!response.ok) {
    throw new ApiError(response.status)
  }

  return response.json() as Promise<T>
}

export function getJobs(params: {
  region?: string
  skill?: string
  search?: string
  status?: string
  days?: number | string
}) {
  const query = new URLSearchParams()

  if (params.region) query.set('region', params.region)
  if (params.skill) query.set('skill', params.skill)
  if (params.search) query.set('search', params.search)
  if (params.status) query.set('status', params.status)
  if (params.days) query.set('days', String(params.days))

  const suffix = query.toString() ? `?${query.toString()}` : ''
  return getJson<Job[]>(`/api/jobs${suffix}`)
}

export function getOverview() {
  return getJson<Overview>('/api/analytics/overview')
}

export function getTopSkills() {
  return getJson<SkillDemand[]>('/api/analytics/top-skills')
}

export function getJobsByRegion() {
  return getJson<JobsByRegion[]>('/api/analytics/jobs-by-region')
}

export function getFreshness() {
  return getJson<Freshness>('/api/analytics/freshness')
}

export function getHistory(days?: number) {
  const suffix = days ? `?days=${encodeURIComponent(String(days))}` : ''
  return getJson<History>(`/api/analytics/history${suffix}`)
}

// --- Round 13: crawler health shared-secret gate ---------------------------

/** sessionStorage key: the password lives only for the lifetime of the tab. */
export const HEALTH_PASSWORD_STORAGE_KEY = 'nzjobs.healthPassword'

export function getStoredHealthPassword(): string | null {
  try {
    return window.sessionStorage.getItem(HEALTH_PASSWORD_STORAGE_KEY)
  } catch {
    // sessionStorage can be unavailable (private mode / disabled storage).
    return null
  }
}

export function storeHealthPassword(password: string): void {
  try {
    window.sessionStorage.setItem(HEALTH_PASSWORD_STORAGE_KEY, password)
  } catch {
    // Ignore: the in-memory state still unlocks the current page.
  }
}

export function clearStoredHealthPassword(): void {
  try {
    window.sessionStorage.removeItem(HEALTH_PASSWORD_STORAGE_KEY)
  } catch {
    // Nothing to clear.
  }
}

/**
 * The password is sent as the Bearer header only — never as a query parameter,
 * so it can't leak into URLs, logs or history.
 */
export function getHealth(password: string) {
  return getJson<HealthReport>('/api/admin/health', {
    headers: { Authorization: `Bearer ${password}` },
  })
}
