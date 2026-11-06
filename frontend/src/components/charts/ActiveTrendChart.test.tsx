// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest'
import { cleanup, render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ActiveTrendChart } from './ActiveTrendChart'
import type { ActiveTrendPoint, SourceCoverageChange } from '../../types/api'

// Recharts measures its container, which jsdom does not implement. The chart
// markup is not what Round 23 tests; render its children so the surrounding
// legend/detail markup is still exercised.
vi.mock('recharts', () => {
  const Wrapper = ({ children }: { children?: ReactNode }) => <div>{children}</div>
  return {
    ResponsiveContainer: Wrapper,
    LineChart: Wrapper,
    CartesianGrid: () => null,
    XAxis: () => null,
    YAxis: () => null,
    Tooltip: () => null,
    Line: () => null,
  }
})

const points: ActiveTrendPoint[] = [
  {
    snapshotDate: '2026-10-15',
    activeCount: 409,
    sourceCount: 41,
    coverageChanged: false,
    sourcesAdded: [],
    sourcesRemoved: [],
  },
  {
    snapshotDate: '2026-10-17',
    activeCount: 439,
    sourceCount: 43,
    coverageChanged: true,
    sourcesAdded: ['PlaceMe IT'],
    sourcesRemoved: ['JobSeek'],
  },
]

const coverageChanges: SourceCoverageChange[] = [
  {
    snapshotDate: '2026-10-17',
    sourcesAdded: ['PlaceMe IT'],
    sourcesRemoved: ['JobSeek'],
    events: [
      {
        source: 'PlaceMe IT',
        direction: 'added',
        classification: 'Configuration added',
        evidence: 'The current source registry row was created around 2026-10-17.',
      },
    ],
  },
]

describe('ActiveTrendChart Round 23 hidden detail elements', () => {
  afterEach(cleanup)

  it('keeps the chart and coverage annotation legend visible', () => {
    render(<ActiveTrendChart points={points} coverageChanges={coverageChanges} />)

    expect(screen.getByRole('img', { name: /Historical active tracked listings/ })).toBeInTheDocument()
    expect(
      screen.getByText((_content, element) => element?.classList.contains('chart-legend') === true
        && element.textContent?.includes('Snapshot where source coverage changed') === true),
    ).toBeInTheDocument()
  })

  it('does not render the classified coverage list or the history table toggle', () => {
    render(<ActiveTrendChart points={points} coverageChanges={coverageChanges} />)

    expect(screen.queryByText('Source-coverage changes, classified')).not.toBeInTheDocument()
    expect(screen.queryByText('View active-listing history as a table')).not.toBeInTheDocument()
    expect(screen.queryByText('PlaceMe IT')).not.toBeInTheDocument()
  })
})
