import { describe, expect, it } from 'vitest'
import { comparisonFixture, dataRoleExplorerFixture, freshnessFixture, healthFixture, historyFixture, jobFixture, overviewFixture, qualityFixture, skillDemandFixture } from './contract.fixtures'

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
      'employerDisclosure', 'roleFamily', 'roleFamilyInferred',
      'dataSubRoleKey', 'dataSubRole', 'dataSubRoleUncertain', 'dataSubRoleCandidates',
      'dataSubRoleUncertainReason', 'dataSubRoleVersion',
      'duplicateGroupId',
      'duplicateCount', 'alsoOnSources', 'isCanonicalListing',
      'employerCanonicalId', 'employerCanonicalName',
    ])
    requireNonNull(jobFixture, [
      'id', 'source', 'title', 'url', 'salaryCurrency', 'salaryPeriod',
      'employmentType', 'seniority', 'workMode', 'experienceBand',
      'firstSeenAt', 'lastSeenAt', 'isActive', 'skills', 'programmingLanguages',
      'sourceCategory', 'advertiser', 'advertiserType', 'employerDisclosure',
      'roleFamily', 'roleFamilyInferred', 'dataSubRoleUncertain',
      'dataSubRoleCandidates', 'dataSubRoleVersion',
      'duplicateCount', 'alsoOnSources', 'isCanonicalListing',
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
      'deduplication.listingsInGroups', 'deduplication.status', 'deduplication.unavailableReason',
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
      'filter.hasFilters', 'filter.historyFilterSupported', 'filter.note', 'scopeNote',
      'weeklyNewListings[0].weekStartLocal', 'weeklyNewListings[0].weekEndLocalExclusive',
      'weeklyNewListings[0].count', 'weeklyNewListings[0].isPartial',
      'activeTrend[0].snapshotDate', 'activeTrend[0].activeCount', 'activeTrend[0].sourceCount',
      'activeTrend[0].coverageChanged', 'activeTrend[0].sourcesAdded', 'activeTrend[0].sourcesRemoved',
      'coverageChanges[0].snapshotDate', 'coverageChanges[0].events[0].source',
      'coverageChanges[0].events[0].direction', 'coverageChanges[0].events[0].classification',
      'coverageChanges[0].events[0].evidence',
      'constantSourceComparison.available', 'constantSourceComparison.eligibilityRule',
      'constantSourceComparison.eligibleSourceCount', 'constantSourceComparison.change.absoluteChange',
      'constantSourceComparison.rawChange.absoluteChange',
      'constantSourceComparison.collectionAssociatedResidual',
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

  it('Quality panel reads every measure denominator, capture time and classification scope', () => {
    requireFields(qualityFixture, [
      'generatedAtUtc', 'population', 'activeListings', 'methodologyNote',
      'measures[0].key', 'measures[0].label', 'measures[0].numerator',
      'measures[0].denominator', 'measures[0].percent', 'measures[0].displayValue',
      'measures[0].scope', 'measures[0].capturedAtUtc', 'measures[0].available',
      'measures[0].unavailableReason', 'measures[0].definition',
      'classification.available', 'classification.unavailableReason',
      'classification.sampledListings', 'classification.reviewedListings',
      'classification.correct', 'classification.falsePositive',
      'classification.falseNegative', 'classification.ambiguous', 'classification.unknown',
      'classification.accuracyPercent', 'classification.accuracyDenominator',
      'classification.ruleVersion', 'classification.scope', 'classification.method',
    ])
    requireNonNull(qualityFixture, [
      'generatedAtUtc', 'population', 'activeListings', 'measures[0].key',
      'measures[0].denominator', 'measures[0].capturedAtUtc', 'measures[0].available',
      'classification.available', 'classification.ruleVersion', 'classification.scope',
    ])
    requireType(qualityFixture, 'activeListings', 'number')
    requireType(qualityFixture, 'measures[0].available', 'boolean')
    requireType(qualityFixture, 'classification.available', 'boolean')
    expect(qualityFixture.measures.every((measure) => measure.denominator !== null)).toBe(true)
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

describe('Data & Analytics explorer contract', () => {
  it('carries the taxonomy version, counts, salary samples and skill coverage', () => {
    requireFields(dataRoleExplorerFixture, [
      'generatedAtUtc', 'family', 'taxonomyVersion', 'taxonomyEffectiveDate',
      'taxonomyDescription', 'taxonomyScope', 'uncertainLabel', 'cohortCount',
      'classifiedCount', 'uncertainCount', 'classifiedSharePercent', 'filters',
      'roles', 'salaryBasis', 'skillBasis', 'scopeNote',
      'roles[0].key', 'roles[0].label', 'roles[0].uncertain', 'roles[0].candidateLabels',
      'roles[0].listingCount', 'roles[0].sharePercent', 'roles[0].salary.medianAnnualNzd',
      'roles[0].salary.eligibleCount', 'roles[0].salary.cohortCount',
      'roles[0].salary.coveragePercent', 'roles[0].salary.suppressed',
      'roles[0].salary.smallSample', 'roles[0].salary.currency', 'roles[0].salary.payPeriod',
      'roles[0].salary.basis', 'roles[0].listingsWithSkills', 'roles[0].skillCoveragePercent',
      'roles[0].topSkills',
    ])
    requireNonNull(dataRoleExplorerFixture, [
      'taxonomyVersion', 'taxonomyEffectiveDate', 'family', 'uncertainLabel',
      'cohortCount', 'classifiedCount', 'uncertainCount', 'classifiedSharePercent',
      'salaryBasis', 'skillBasis', 'scopeNote',
    ])
    expect(dataRoleExplorerFixture.taxonomyVersion).toBe('data-subrole-v1')
    expect(dataRoleExplorerFixture.roles.some((role) => role.uncertain)).toBe(true)
    expect(dataRoleExplorerFixture.roles.some((role) => role.salary.suppressed)).toBe(true)
  })
})

describe('Two-cohort comparison contract', () => {
  it('carries both cohort definitions, counts, skill points, salary samples and history eligibility', () => {
    requireFields(comparisonFixture, [
      'generatedAtUtc', 'days', 'taxonomyVersion', 'interval', 'a', 'b', 'activeCountDifference',
      'skillDifferences', 'skillHistory', 'dateRule', 'scopeNote', 'notes',
      'a.key', 'a.label', 'a.definition', 'a.activeListings', 'a.listingsWithSkills',
      'a.skillCoveragePercent', 'a.salary', 'a.newListings',
      'b.key', 'b.label', 'b.definition', 'b.activeListings', 'b.listingsWithSkills',
      'b.skillCoveragePercent', 'b.salary', 'b.newListings',
      'a.definition.dataSubRole', 'a.definition.region', 'a.definition.seniority',
      'a.definition.workMode', 'a.definition.skill', 'a.definition.hasFilters',
      'a.salary.medianAnnualNzd', 'a.salary.eligibleCount', 'a.salary.cohortCount',
      'a.salary.coveragePercent', 'a.salary.suppressed', 'a.salary.smallSample',
      'a.salary.currency', 'a.salary.payPeriod', 'a.salary.basis',
      'a.newListings.available', 'a.newListings.unavailableReason', 'a.newListings.interval',
      'a.newListings.currentNewListings', 'a.newListings.previousNewListings',
      'a.newListings.change', 'a.newListings.currentPeriodCovered', 'a.newListings.dateBasis',
      'a.newListings.note',
      'activeCountDifference.aCount', 'activeCountDifference.bCount',
      'activeCountDifference.absoluteDifference', 'activeCountDifference.relativeDifferencePercent',
      'activeCountDifference.baselineAvailable', 'activeCountDifference.baseline',
      'activeCountDifference.note',
      'skillDifferences[0].skillName', 'skillDifferences[0].aCount',
      'skillDifferences[0].aSharePercent', 'skillDifferences[0].bCount',
      'skillDifferences[0].bSharePercent', 'skillDifferences[0].differencePercentagePoints',
      'skillDifferences[0].aListingDenominator', 'skillDifferences[0].bListingDenominator',
      'skillHistory.available', 'skillHistory.reason', 'skillHistory.skillsCapturedSnapshotDays',
      'skillHistory.requiredCompleteDays', 'skillHistory.firstSkillsCapturedDate',
      'skillHistory.lastSkillsCapturedDate', 'skillHistory.periodDays',
      'skillHistory.currentPeriodLabel', 'skillHistory.previousPeriodLabel',
      'skillHistory.basis', 'skillHistory.aCurrentListings', 'skillHistory.aPreviousListings',
      'skillHistory.bCurrentListings', 'skillHistory.bPreviousListings', 'skillHistory.skills',
    ])
    requireNonNull(comparisonFixture, [
      'generatedAtUtc', 'days', 'taxonomyVersion', 'interval.label', 'dateRule', 'scopeNote', 'notes',
      'a.key', 'a.label', 'a.activeListings', 'a.listingsWithSkills', 'a.skillCoveragePercent',
      'a.salary.eligibleCount', 'a.salary.cohortCount', 'a.salary.coveragePercent',
      'a.salary.suppressed', 'a.salary.currency', 'a.salary.payPeriod', 'a.salary.basis',
      'a.newListings.available', 'a.newListings.currentPeriodCovered', 'a.newListings.dateBasis',
      'activeCountDifference.aCount', 'activeCountDifference.bCount',
      'activeCountDifference.absoluteDifference', 'activeCountDifference.baselineAvailable',
      'activeCountDifference.baseline', 'activeCountDifference.note',
      'skillHistory.available', 'skillHistory.skillsCapturedSnapshotDays',
      'skillHistory.requiredCompleteDays', 'skillHistory.periodDays', 'skillHistory.skills',
    ])
    expect(comparisonFixture.taxonomyVersion).toBe('data-subrole-v1')
    expect(comparisonFixture.skillDifferences[0].differencePercentagePoints).toBe(-9.2)
    expect(comparisonFixture.skillHistory.available).toBe(false)
    expect(comparisonFixture.skillHistory.reason).toContain('14 complete skill-captured days')
    expect(comparisonFixture.a.salary.suppressed).toBe(true)
    expect(comparisonFixture.b.salary.smallSample).toBe(true)
  })
})

describe('Round 24 skill-demand contract', () => {
  it('carries both denominators, the taxonomy version and every ranked value', () => {
    requireFields(skillDemandFixture, [
      'generatedAtUtc', 'taxonomyVersion', 'activeListings', 'listingsWithExtractedSkills',
      'extractionCoveragePercent', 'denominatorBasis', 'types',
      'types[0].type', 'types[0].listingDenominator', 'types[0].listingsWithExtractedSkills',
      'types[0].extractionCoveragePercent', 'types[0].skills',
      'types[0].skills[0].skillName', 'types[0].skills[0].jobCount',
      'types[0].skills[0].percentOfActiveListings', 'types[0].skills[0].percentOfListingsWithSkills',
    ])
    requireNonNull(skillDemandFixture, [
      'generatedAtUtc', 'taxonomyVersion', 'denominatorBasis', 'types[0].type',
      'types[0].skills[0].skillName',
    ])
    requireType(skillDemandFixture, 'activeListings', 'number')
    requireType(skillDemandFixture, 'extractionCoveragePercent', 'number')
    requireType(skillDemandFixture, 'types[0].skills[0].jobCount', 'number')
    expect(skillDemandFixture.types.map((type) => type.type)).toEqual(['ProgrammingLanguage', 'Framework'])
    expect(skillDemandFixture.taxonomyVersion).toBe('2')
    expect(skillDemandFixture.types[0].skills[0].jobCount).toBe(79)
  })
})
