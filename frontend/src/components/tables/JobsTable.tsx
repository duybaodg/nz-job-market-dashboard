import {
  flexRender,
  getCoreRowModel,
  useReactTable,
  type ColumnDef,
} from '@tanstack/react-table'
import type { Job } from '../../types/api'
import { formatSalaryRange } from '../../lib/format'

const columns: ColumnDef<Job>[] = [
  {
    header: 'Role',
    accessorKey: 'title',
    cell: ({ row }) => (
      <div className="role-cell">
        <a href={row.original.url} target="_blank" rel="noreferrer">
          {row.original.title}
        </a>
        <span>{row.original.company ?? 'Unknown company'}</span>
      </div>
    ),
  },
  {
    header: 'Region',
    accessorKey: 'region',
  },
  {
    header: 'Source',
    accessorKey: 'source',
  },
  {
    header: 'Salary',
    cell: ({ row }) => formatSalaryRange(row.original.salaryMin, row.original.salaryMax),
  },
  {
    header: 'Seniority',
    accessorKey: 'seniority',
  },
  {
    header: 'Skills',
    cell: ({ row }) => (
      <div className="skill-list">
        {row.original.skills.slice(0, 3).map((skill) => (
          <span key={skill}>{skill}</span>
        ))}
      </div>
    ),
  },
]

type JobsTableProps = {
  jobs: Job[]
}

export function JobsTable({ jobs }: JobsTableProps) {
  const table = useReactTable({
    data: jobs,
    columns,
    getCoreRowModel: getCoreRowModel(),
  })

  if (jobs.length === 0) {
    return <div className="table-empty">No roles match the current filters.</div>
  }

  return (
    <div className="table-wrap">
      <table>
        <thead>
          {table.getHeaderGroups().map((headerGroup) => (
            <tr key={headerGroup.id}>
              {headerGroup.headers.map((header) => (
                <th key={header.id}>
                  {flexRender(header.column.columnDef.header, header.getContext())}
                </th>
              ))}
            </tr>
          ))}
        </thead>
        <tbody>
          {table.getRowModel().rows.map((row) => (
            <tr key={row.id}>
              {row.getVisibleCells().map((cell) => (
                <td key={cell.id} data-label={String(cell.column.columnDef.header)}>
                  {flexRender(cell.column.columnDef.cell, cell.getContext())}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
