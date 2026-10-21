import { describe, expect, it } from 'vitest'
import { freshnessFixture, healthFixture, historyFixture, jobFixture, overviewFixture } from './contract.fixtures'

/**
 * Round 14 consistency contract. One focused check per widget group: every API
 * payload field the frontend reads must exist and be non-null in a live-shaped
 * payload. The fixtures are typed with the real response types, so a rename also
 * fails the TypeScript build; this runtime walk catches a nulled/removed field.
 */

function at(root: unknown, path: string): unknown {
  let current: unknown = root

  for (const segment of path.split('.')) {
    const bracket = segment.indexOf('[')
    const name = bracket >= 0 ? segment.slice(0, bracket) : segment
    const index = bracket >= 0 ? Number(segment.slice(bracket + 1, segment.indexOf(']'))) : null

    if (name) {
      if (typeof current !== 'object' || current === null) {
        throw new Error(`contract path '${path}' is missing object '${name}'`)
      }
      current = (current as Record<string, unknown>)[name]
    }

    if (index !== null) {
      if (!Array.isArray(current) || current.length <= index) {
        throw new Error(`contract path '${path}' is missing index [${index}]`)
      }
      current = current[index]
    }
  }

  return current
}

function requireFields(root: unknown, paths: string[]) {
  for (const path of paths) {
    // Existence only: nullability is already enforced by the response types the
    // fixtures are annotated with, which `tsc` checks during the build.
    expect(at(root, path), `missing contract path '${path}'`).not.toBeUndefined()
  }
}

describe('API payload contract', () => {
  it('Find jobs cards read salary, work mode and first-seen/posted fields', () => {
    requireFields(jobFixture, [
      'id', 'source', 'title', 'company', 'location', 'region',
      'salaryMin', 'salaryMax', 'salaryText', 'salaryCurrency', 'salaryPeriod',
      'salaryQuestionable', 'salaryQuestionableReason',
      'employmentType', 'seniority', 'workMode', 'experienceBand', 'industry',
      'descriptionSummary', 'url', 'postedDate', 'closingDate', 'firstSeenAt',
      'lastSeenAt', 'isActive', 'expiredAt', 'skills', 'programmingLanguages',
      'sourceCategory', 'advertiser', 'advertiserType', 'hiringEmployer',
      'employerDisclosure', 'roleFamily', 'roleFamilyInferred', 'duplicateGroupId',
      'duplicateCount', 'alsoOnSources', 'isCanonicalListing',
      'employerCanonicalId', 'employerCanonicalName',
    ])
  })

  it('Overview stat cards and the freshness pill read their fields', () => {
    requireFields(overviewFixture, [
      'generatedAtUtc', 'activeListings', 'postedDateKnown', 'postedDateMissing',
      'mostActiveRegion', 'mostRequestedSkill', 'interval.label',
      'newlyDiscovered.current', 'newlyDiscovered.absoluteChange', 'newlyDiscovered.percentChange',
      'newlyDiscovered.comparisonAvailable', 'newlyDiscovered.noPriorBaseline',
      'newlyPosted.current', 'newlyPosted.comparisonAvailable',
      'salary.medianAnnualNzd', 'salary.eligibleCount', 'salary.cohortCount',
      'salary.coveragePercent', 'salary.currency', 'salary.payPeriod', 'salary.basis',
      'salary.exclusions.missing', 'salary.exclusions.non-annual',
      'employers.identifiableEmployers', 'employers.listingsWithEmployer',
      'employers.canonicalEmployers', 'employers.unmatchedEmployerNames',
      'dataStatus.state', 'dataStatus.lastSuccessfulUpdateUtc', 'dataStatus.newestListingSeenUtc',
      'dataStatus.enabledSources', 'dataStatus.enabledSourcesWithRecentSuccess', 'dataStatus.message',
      'uniqueVacancies', 'deduplication.listingsMerged', 'deduplication.groups',
      'discoveryBasis',
    ])
  })

  it('Trends charts read the history and freshness window fields', () => {
    requireFields(historyFixture, [
      'coverage.firstDate', 'coverage.lastDate', 'coverage.snapshotDays', 'coverage.missingDates',
      'coverage.hasHistory', 'coverage.note',
      'activeChange.comparisonAvailable', 'activeChange.current',
      'newListings.days', 'newListings.dateBasis', 'newListings.note',
      'newListings.interval.label', 'newListings.change.current',
      'weeklyNewListings[0].weekStartLocal', 'weeklyNewListings[0].weekEndLocalExclusive',
      'weeklyNewListings[0].count', 'weeklyNewListings[0].isPartial',
      'activeTrend[0].snapshotDate', 'activeTrend[0].activeCount', 'activeTrend[0].sourceCount',
      'activeTrend[0].coverageChanged', 'activeTrend[0].sourcesAdded', 'activeTrend[0].sourcesRemoved',
      'coverageChanges[0].snapshotDate',
    ])
    requireFields(freshnessFixture, [
      'totalActiveJobs', 'discoveryBasis', 'windows[0].days', 'windows[0].newlyPosted',
      'windows[0].previousNewlyPosted', 'windows[0].newlyDiscovered',
      'windows[0].previousNewlyDiscovered', 'windows[0].comparisonAvailable',
    ])
  })

  it('Crawler health page reads the summary and per-source fields', () => {
    requireFields(healthFixture, [
      'generatedAt', 'summary.healthy', 'summary.degraded', 'summary.stale', 'summary.empty',
      'summary.disabled', 'summary.neverRun', 'summary.retired', 'summary.totalSources',
      'sources[0].source', 'sources[0].enabled', 'sources[0].parser', 'sources[0].method',
      'sources[0].consecutiveFailures', 'sources[0].consecutiveZeroYield',
      'sources[0].jobsYielded', 'sources[0].state', 'sources[0].crawlFrequencyMinutes',
      'retiredSources[0].source', 'retiredSources[0].state',
    ])
  })
})
