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
