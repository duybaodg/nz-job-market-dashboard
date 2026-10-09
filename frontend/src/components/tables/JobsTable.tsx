import {
  flexRender,
  getCoreRowModel,
  getPaginationRowModel,
  useReactTable,
  type ColumnDef,
} from '@tanstack/react-table'
import type { Job } from '../../types/api'
import { formatDate, formatSalaryRange } from '../../lib/format'
import { ArrowUpRight } from 'lucide-react'

const columns: ColumnDef<Job>[] = [
  {
    header: 'Role',
    accessorKey: 'title',
    cell: ({ row }) => (
      <div className="role-cell">
        <a href={row.original.url} target="_blank" rel="noreferrer">
          {row.original.title}
          <ArrowUpRight aria-hidden="true" />
        </a>
        <span>{row.original.company ?? 'Unknown company'}</span>
        <span>Posted {formatDate(row.original.postedDate)} · closes {formatDate(row.original.closingDate)}</span>
      </div>
    ),
  },
  {
    header: 'Location',
    cell: ({ row }) => row.original.location ?? row.original.region ?? 'Unknown',
  },
  {
    header: 'Source',
    accessorKey: 'source',
  },
  {
    header: 'Status',
    cell: ({ row }) => <span className={`status-badge ${row.original.isActive ? 'active' : 'expired'}`}>{row.original.isActive ? 'Active' : 'Expired'}</span>,
  },
  {
    header: 'Salary',
    cell: ({ row }) => formatSalaryRange(row.original.salaryMin, row.original.salaryMax),
  },
  {
    header: 'Experience',
    accessorKey: 'experienceBand',
  },
  {
    header: 'Languages',
    cell: ({ row }) => (
      <div className="skill-list">
        {row.original.programmingLanguages.length === 0 ? <span className="muted">Not specified</span> : row.original.programmingLanguages.slice(0, 3).map((language) => <span key={language}>{language}</span>)}
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
    getPaginationRowModel: getPaginationRowModel(),
    initialState: { pagination: { pageIndex: 0, pageSize: 10 } },
  })

  if (jobs.length === 0) {
    return <div className="table-empty">No roles match the current filters.</div>
  }

  return (
    <>
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
      <nav className="table-pagination" aria-label="Recent jobs pages">
        <span>Page {table.getState().pagination.pageIndex + 1} of {table.getPageCount()}</span>
        <div>
          <button type="button" onClick={() => table.previousPage()} disabled={!table.getCanPreviousPage()}>Previous</button>
          <button type="button" onClick={() => table.nextPage()} disabled={!table.getCanNextPage()}>Next</button>
        </div>
      </nav>
    </>
  )
}
