export type ComparisonBarItem = {
  label: string
  aValue: number
  bValue: number
  aDisplay?: string
  bDisplay?: string
  note?: string
}

type ComparisonBarsProps = {
  ariaLabel: string
  aLabel: string
  bLabel: string
  items: ComparisonBarItem[]
  unit?: string
  emptyMessage?: string
}

/**
 * Round 22 (C2): aligned horizontal bars for two cohorts on one common scale.
 * Values are rendered as text beside each bar, so nothing is hover-only and the
 * same numbers are available in the adjacent accessible table.
 */
export function ComparisonBars({
  ariaLabel,
  aLabel,
  bLabel,
  items,
  unit = '',
  emptyMessage = 'No comparable values for this cohort pair.',
}: ComparisonBarsProps) {
  if (items.length === 0) {
    return <div className="chart-empty">{emptyMessage}</div>
  }

  const max = Math.max(...items.flatMap((item) => [item.aValue, item.bValue]), 1)

  return (
    <ol className="comparison-bars" aria-label={ariaLabel}>
      {items.map((item) => (
        <li key={item.label}>
          <div className="comparison-bar-head">
            <span className="comparison-bar-label">{item.label}</span>
            {item.note ? <small>{item.note}</small> : null}
          </div>
          <div className="comparison-bar-row">
            <span className="comparison-bar-cohort">{aLabel}</span>
            <span className="comparison-bar-track" aria-hidden="true">
              <span className="comparison-bar-fill comparison-bar-a" style={{ width: `${(item.aValue / max) * 100}%` }} />
            </span>
            <span className="comparison-bar-value">
              {item.aDisplay ?? `${item.aValue.toLocaleString('en-NZ')}${unit}`}
            </span>
          </div>
          <div className="comparison-bar-row">
            <span className="comparison-bar-cohort">{bLabel}</span>
            <span className="comparison-bar-track" aria-hidden="true">
              <span className="comparison-bar-fill comparison-bar-b" style={{ width: `${(item.bValue / max) * 100}%` }} />
            </span>
            <span className="comparison-bar-value">
              {item.bDisplay ?? `${item.bValue.toLocaleString('en-NZ')}${unit}`}
            </span>
          </div>
        </li>
      ))}
    </ol>
  )
}
