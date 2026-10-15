import { X } from 'lucide-react'
import type { JobFilterChip, JobFilterState } from '../../lib/urlState'

type FilterChipsProps = {
  chips: JobFilterChip[]
  onRemove: (name: keyof JobFilterState) => void
  onClearAll: () => void
}

export function FilterChips({ chips, onRemove, onClearAll }: FilterChipsProps) {
  if (chips.length === 0) {
    return null
  }

  return (
    <div className="filter-chips" aria-label="Applied filters">
      {chips.map((chip) => (
        <button
          key={chip.name}
          type="button"
          className="filter-chip"
          onClick={() => onRemove(chip.name)}
          aria-label={`Remove filter ${chip.label}: ${chip.display}`}
        >
          <span>
            {chip.label}: <strong>{chip.display}</strong>
          </span>
          <X aria-hidden="true" />
        </button>
      ))}
      <button type="button" className="clear-all" onClick={onClearAll}>
        Clear all
      </button>
    </div>
  )
}
