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
    expect(at(root, path), `missing contract path '${path}'`).not.toBeUndefined()
  }
}

/**
 * Round 15 (L-1): a field the widget dereferences must not be present-but-null.
 * Existence alone let a backend null a required value while this test stayed
 * green, because the TypeScript type is often `T | null` for optional data.
 */
function requireNonNull(root: unknown, paths: string[]) {
  for (const path of paths) {
    const value = at(root, path)
    expect(value, `null contract path '${path}'`).not.toBeNull()
    expect(value, `missing contract path '${path}'`).not.toBeUndefined()
  }
}

function requireType(root: unknown, path: string, type: 'string' | 'number' | 'boolean' | 'object') {
  expect(typeof at(root, path), `wrong type for contract path '${path}'`).toBe(type)
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
    requireNonNull(jobFixture, [
      'id', 'source', 'title', 'url', 'salaryCurrency', 'salaryPeriod',
      'employmentType', 'seniority', 'workMode', 'experienceBand',
      'firstSeenAt', 'lastSeenAt', 'isActive', 'skills', 'programmingLanguages',
      'sourceCategory', 'advertiser', 'advertiserType', 'employerDisclosure',
      'roleFamily', 'roleFamilyInferred', 'duplicateCount', 'alsoOnSources',
      'isCanonicalListing',
    ])
    requireType(jobFixture, 'id', 'string')
    requireType(jobFixture, 'title', 'string')
    requireType(jobFixture, 'isActive', 'boolean')
    requireType(jobFixture, 'skills', 'object')
    requireType(jobFixture, 'duplicateCount', 'number')
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
    requireNonNull(overviewFixture, [
      'generatedAtUtc', 'interval.label', 'salary.currency', 'salary.payPeriod',
      'salary.basis', 'dataStatus.state', 'dataStatus.message',
      'employers.identifiableEmployers', 'uniqueVacancies', 'discoveryBasis',
    ])
    requireType(overviewFixture, 'activeListings', 'number')
    requireType(overviewFixture, 'newlyDiscovered.current', 'number')
    requireType(overviewFixture, 'salary.eligibleCount', 'number')
    requireType(overviewFixture, 'dataStatus.enabledSources', 'number')
  })

  it('Trends charts read the history and freshness window fields', () => {
    requireFields(historyFixture, [
      'coverage.firstDate', 'coverage.lastDate', 'coverage.snapshotDays', 'coverage.missingDates',
      'coverage.hasHistory', 'coverage.note', 'coverage.retentionDays',
      'activeChange.comparisonAvailable', 'activeChange.current',
      'newListings.days', 'newListings.dateBasis', 'newListings.note',
      'newListings.interval.label', 'newListings.change.current',
      'weeklyNewListings[0].weekStartLocal', 'weeklyNewListings[0].weekEndLocalExclusive',
      'weeklyNewListings[0].count', 'weeklyNewListings[0].isPartial',
      'activeTrend[0].snapshotDate', 'activeTrend[0].activeCount', 'activeTrend[0].sourceCount',
      'activeTrend[0].coverageChanged', 'activeTrend[0].sourcesAdded', 'activeTrend[0].sourcesRemoved',
      'coverageChanges[0].snapshotDate',
    ])
    requireNonNull(historyFixture, [
      'coverage.firstDate', 'coverage.lastDate', 'coverage.retentionDays',
      'newListings.days', 'newListings.dateBasis', 'newListings.change.current',
      'weeklyNewListings[0].weekStartLocal', 'activeTrend[0].snapshotDate',
    ])
    requireType(historyFixture, 'coverage.retentionDays', 'number')
    requireType(historyFixture, 'coverage.missingDates', 'object')
    requireType(historyFixture, 'weeklyNewListings[0].count', 'number')
    requireType(historyFixture, 'activeTrend[0].activeCount', 'number')
    requireFields(freshnessFixture, [
      'totalActiveJobs', 'discoveryBasis', 'windows[0].days', 'windows[0].newlyPosted',
      'windows[0].previousNewlyPosted', 'windows[0].newlyDiscovered',
      'windows[0].previousNewlyDiscovered', 'windows[0].comparisonAvailable',
    ])
    requireNonNull(freshnessFixture, ['totalActiveJobs', 'discoveryBasis', 'windows[0].days'])
    requireType(freshnessFixture, 'totalActiveJobs', 'number')
    requireType(freshnessFixture, 'windows[0].newlyPosted', 'number')
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
    requireNonNull(healthFixture, [
      'generatedAt', 'summary.totalSources', 'sources[0].source',
      'sources[0].enabled', 'sources[0].state', 'retiredSources[0].source',
    ])
    requireType(healthFixture, 'summary.totalSources', 'number')
    requireType(healthFixture, 'sources[0].enabled', 'boolean')
  })

  it('fails when a required field is removed or nulled (negative fixture)', () => {
    const removed = JSON.parse(JSON.stringify(jobFixture)) as Record<string, unknown>
    delete removed.title
    expect(() => requireFields(removed, ['title'])).toThrow()

    const nulled = JSON.parse(JSON.stringify(jobFixture)) as Record<string, unknown>
    nulled.url = null
    expect(() => requireNonNull(nulled, ['url'])).toThrow()
  })
})
