export type RankedBarItem = {
  label: string
  count: number
  share?: number
  note?: string
}

type RankedBarListProps = {
  items: RankedBarItem[]
  ariaLabel: string
  countLabel?: string
  emptyMessage?: string
  selected?: string
  onSelect?: (label: string) => void
  selectLabel?: string
}

/**
 * Ranked, directly labelled horizontal bars. Rendered as real buttons (not SVG)
 * so the values are visible without hover and every bar is keyboard-operable;
 * selecting a bar applies the same filter the results list uses.
 */
export function RankedBarList({
  items,
  ariaLabel,
  countLabel = 'listings',
  emptyMessage = 'No data for this cohort.',
  selected,
  onSelect,
  selectLabel,
}: RankedBarListProps) {
  if (items.length === 0) {
    return <div className="chart-empty">{emptyMessage}</div>
  }

  const max = Math.max(...items.map((item) => item.count), 1)

  return (
    <ol className="ranked-bars" aria-label={ariaLabel}>
      {items.map((item) => {
        const isSelected = selected === item.label
        const content = (
          <>
            <span className="ranked-bar-label">{item.label}</span>
            <span className="ranked-bar-track" aria-hidden="true">
              <span className="ranked-bar-fill" style={{ width: `${(item.count / max) * 100}%` }} />
            </span>
            <span className="ranked-bar-value">
              <strong>{item.count.toLocaleString('en-NZ')}</strong>
              <small>
                {item.share != null ? `${item.share}%` : countLabel}
                {item.note ? ` · ${item.note}` : ''}
              </small>
            </span>
          </>
        )

        return (
          <li key={item.label}>
            {onSelect ? (
              <button
                type="button"
                className={`ranked-bar${isSelected ? ' selected' : ''}`}
                aria-pressed={isSelected}
                title={selectLabel ? `${selectLabel}: ${item.label}` : undefined}
                onClick={() => onSelect(item.label)}
              >
                {content}
              </button>
            ) : (
              <div className="ranked-bar">{content}</div>
            )}
          </li>
        )
      })}
    </ol>
  )
}
