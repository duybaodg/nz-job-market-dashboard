import type { Job, SalaryPeriod } from '../types/api'

const supportedCurrencies = new Set(['NZD', 'AUD', 'USD', 'GBP', 'EUR', 'CAD', 'INR'])

/** True when the advertised figure is a comparable permanent annual NZD amount. */
export function isComparableAnnual(job: Job): boolean {
  return (
    job.salaryCurrency === 'NZD' &&
    job.salaryPeriod === 'Year' &&
    job.employmentType !== 'Contract' &&
    job.employmentType !== 'Internship' &&
    salaryMidpoint(job) != null
  )
}

/** Midpoint of the advertised bounds, or the single explicit amount. */
export function salaryMidpoint(job: Job): number | null {
  if (job.salaryMin == null && job.salaryMax == null) return null

  return (((job.salaryMin ?? job.salaryMax) ?? 0) + ((job.salaryMax ?? job.salaryMin) ?? 0)) / 2
}

export function formatSalaryAmount(value: number, currency: string): string {
  if (supportedCurrencies.has(currency)) {
    return new Intl.NumberFormat('en-NZ', {
      style: 'currency',
      currency,
      maximumFractionDigits: 0,
    }).format(value)
  }

  return `${currency} ${value.toLocaleString('en-NZ', { maximumFractionDigits: 0 })}`
}

export function salaryPeriodSuffix(period: SalaryPeriod | string): string {
  switch (period) {
    case 'Hour':
      return ' per hour'
    case 'Day':
      return ' per day'
    case 'Year':
      return ' per year'
    default:
      return ''
  }
}

/** Human-readable advertised salary, never inferring a pay period that is unknown. */
export function formatAdvertisedSalary(job: Job): string {
  if (job.salaryMin == null && job.salaryMax == null) return 'Salary not advertised'

  const suffix = salaryPeriodSuffix(job.salaryPeriod)
  const min = job.salaryMin
  const max = job.salaryMax

  if (min != null && max != null && min !== max) {
    return `${formatSalaryAmount(min, job.salaryCurrency)} – ${formatSalaryAmount(max, job.salaryCurrency)}${suffix}`
  }

  return `${formatSalaryAmount((min ?? max)!, job.salaryCurrency)}${suffix}`
}
