import type { Change } from '../types/api'

/**
 * Mirrors the backend change calculation: a zero previous value shows the
 * absolute change plus "No prior baseline", absent history shows "Comparison
 * unavailable", and a percentage is only produced when previous > 0.
 */
export function computeChange(current: number, previous: number | null): Change {
  if (previous == null) {
    return {
      current,
      previous: null,
      absoluteChange: null,
      percentChange: null,
      comparisonAvailable: false,
      noPriorBaseline: false,
      note: 'Comparison unavailable.',
    }
  }

  const absoluteChange = current - previous

  if (previous === 0) {
    return {
      current,
      previous,
      absoluteChange,
      percentChange: null,
      comparisonAvailable: true,
      noPriorBaseline: true,
      note: 'No prior baseline.',
    }
  }

  return {
    current,
    previous,
    absoluteChange,
    percentChange: Math.round((absoluteChange / previous) * 1000) / 10,
    comparisonAvailable: true,
    noPriorBaseline: false,
    note: null,
  }
}

export function describeChange(change: Change): string {
  if (!change.comparisonAvailable) return change.note ?? 'Comparison unavailable'

  const absolute = change.absoluteChange ?? 0
  const sign = absolute > 0 ? '+' : ''

  if (change.noPriorBaseline) {
    return `${sign}${absolute} vs prior period (no prior baseline)`
  }

  const percent = change.percentChange ?? 0
  return `${sign}${absolute} (${percent > 0 ? '+' : ''}${percent}%) vs prior equal period`
}
