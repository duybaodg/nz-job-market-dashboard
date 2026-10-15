import type { Job } from '../types/api'

/** Display helpers shared by the results list, the charts and the CSV export. */

export function locationLabel(job: Job): string {
  if (job.region === 'New Zealand' && !job.location) return 'Nationwide (NZ)'
  return job.location ?? job.region ?? 'Location not stated'
}

export function workModeLabel(job: Job): string {
  return job.workMode && job.workMode !== 'Unknown' ? job.workMode : 'Work mode not stated'
}

export function postedBasisLabel(job: Job): { basis: 'Posted' | 'First seen'; date: string } {
  return job.postedDate
    ? { basis: 'Posted', date: job.postedDate }
    : { basis: 'First seen', date: job.firstSeenAt }
}

/**
 * The hiring employer is shown first and the advertiser is always identified
 * separately. An agency-only listing never invents the agency's client.
 */
export function employerLines(job: Job): { primary: string; secondary: string | null } {
  if (job.employerDisclosure === 'Disclosed' && job.hiringEmployer) {
    const differs = Boolean(job.advertiser) && job.advertiser.toLowerCase() !== job.hiringEmployer.toLowerCase()
    const secondary = differs
      ? job.advertiserType === 'Agency'
        ? `Advertised by ${job.advertiser} (recruiter)`
        : `Advertised via ${job.advertiser}`
      : null
    return { primary: job.hiringEmployer, secondary }
  }

  if (job.employerDisclosure === 'AgencyOnly') {
    return {
      primary: 'Hiring employer not disclosed',
      secondary: `Advertised by ${job.advertiser} (recruiter)`,
    }
  }

  return {
    primary: 'Employer not stated',
    secondary: job.advertiser ? `Advertised via ${job.advertiser}` : null,
  }
}
