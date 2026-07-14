import type { Job, JobsByRegion, Overview, SkillDemand } from '../types/api'

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5050'

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(`${apiBaseUrl}${path}`)

  if (!response.ok) {
    throw new Error(`API request failed: ${response.status}`)
  }

  return response.json() as Promise<T>
}

export function getJobs(params: { region?: string; skill?: string; search?: string }) {
  const query = new URLSearchParams()

  if (params.region) query.set('region', params.region)
  if (params.skill) query.set('skill', params.skill)
  if (params.search) query.set('search', params.search)

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
