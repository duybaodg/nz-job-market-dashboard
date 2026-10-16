import { describe, expect, it } from 'vitest'
import { formatDate, formatNzDateTime } from './format'

describe('formatDate', () => {
  it('renders a UTC-midnight source date on the Pacific/Auckland calendar day', () => {
    expect(formatDate('2026-10-15T00:00:00Z')).toBe('15 Oct 2026')
  })

  it('never shifts the calendar day for a viewer behind UTC', () => {
    // The label is pinned to the reporting timezone, not the viewer's zone, so
    // a UTC-midnight posted/closing date cannot read as the previous day.
    expect(formatDate('2026-10-15T00:00:00Z')).not.toContain('14 Oct')
    expect(formatDate(null)).toBe('Unknown')
  })
})

describe('formatNzDateTime', () => {
  it('labels the timestamp as NZ', () => {
    expect(formatNzDateTime('2026-10-16T17:56:58Z')).toMatch(/NZ$/)
    expect(formatNzDateTime(null)).toBe('never')
  })
})
