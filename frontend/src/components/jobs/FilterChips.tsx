import { X } from 'lucide-react'

export type FilterChip<T extends string = string> = {
  name: T
  label: string
  display: string
}

type FilterChipsProps<T extends string> = {
  chips: FilterChip<T>[]
  onRemove: (name: T) => void
  onClearAll: () => void
}

export function FilterChips<T extends string>({ chips, onRemove, onClearAll }: FilterChipsProps<T>) {
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
