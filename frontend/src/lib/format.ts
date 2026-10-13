export function formatCurrency(value: number | null | undefined) {
  if (value == null) return 'Unknown'

  return new Intl.NumberFormat('en-NZ', {
    style: 'currency',
    currency: 'NZD',
    maximumFractionDigits: 0,
  }).format(value)
}

export function formatSalaryRange(min: number | null, max: number | null) {
  if (min == null && max == null) return 'Unknown'
  if (min != null && max != null) return `${formatCurrency(min)} - ${formatCurrency(max)}`

  return formatCurrency(min ?? max)
}

export function formatDate(value: string | null) {
  return value ? new Intl.DateTimeFormat('en-NZ', { dateStyle: 'medium' }).format(new Date(value)) : 'Unknown'
}

export function formatRelativeTime(value: string | null) {
  if (!value) return 'Never'

  const elapsedMs = Date.now() - new Date(value).getTime()

  if (elapsedMs < 60_000) return 'Just now'

  const minutes = Math.floor(elapsedMs / 60_000)
  if (minutes < 60) return `${minutes}m ago`

  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`

  return `${Math.floor(hours / 24)}d ago`
}
