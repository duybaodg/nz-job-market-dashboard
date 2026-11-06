/**
 * Round 22 (C1): the URL/UI vocabulary for the versioned Data & Analytics
 * sub-role taxonomy. The labels are display fallbacks; the API remains the
 * authority and publishes the taxonomy version beside every result. Keeping the
 * keys here lets URL parsing validate a filter without guessing.
 */
export const DATA_SUB_ROLE_FAMILY = 'Data & Analytics'
export const DATA_SUB_ROLE_VERSION_FALLBACK = 'data-subrole-v1'

export const DATA_SUB_ROLE_OPTIONS = [
  { key: 'data-analyst', label: 'Data Analyst' },
  { key: 'bi-analyst-developer', label: 'BI Analyst/Developer' },
  { key: 'analytics-engineer', label: 'Analytics Engineer' },
  { key: 'data-engineer', label: 'Data Engineer' },
  { key: 'data-scientist', label: 'Data Scientist' },
  { key: 'leadership', label: 'Data & Analytics Leadership' },
  { key: 'uncertain', label: 'Uncertain / multiple or unspecified' },
] as const

export type DataSubRoleKey = (typeof DATA_SUB_ROLE_OPTIONS)[number]['key']

export const DATA_SUB_ROLE_KEYS: readonly string[] = DATA_SUB_ROLE_OPTIONS.map((option) => option.key)

export function dataSubRoleLabel(key: string | null | undefined): string {
  if (!key) return ''
  return DATA_SUB_ROLE_OPTIONS.find((option) => option.key === key)?.label ?? key
}
