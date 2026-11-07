import { RankedBarList } from './RankedBarList'
import { formatNzDateTime } from '../../lib/format'
import type { SkillDemandByType } from '../../types/api'

type SkillDemandChartProps = {
  title: string
  type: 'ProgrammingLanguage' | 'Framework'
  data: SkillDemandByType | undefined
  loading: boolean
  error: boolean
  /** Optional small note rendered after the denominator line. */
  note?: string
}

/**
 * Round 24: one lazy-loaded horizontal bar chart over the type-aware
 * skill-demand endpoint. Values are written directly beside each bar (never
 * hover-only) and the same rows are available as a real table, so the chart
 * and its alternative expose identical counts and percentages.
 */
export function SkillDemandChart({ title, type, data, loading, error, note }: SkillDemandChartProps) {
  if (loading) {
    return (
      <div className="chart-empty" aria-busy="true">
        Loading {title.toLowerCase()}…
      </div>
    )
  }

  if (error || !data) {
    return (
      <div className="empty-state error-state" role="alert">
        {title} could not be loaded. This is a request failure, not evidence of zero demand.
      </div>
    )
  }

  const typeData = data.types.find((entry) => entry.type === type)
  if (!typeData || typeData.skills.length === 0) {
    return <div className="chart-empty">No {title.toLowerCase()} were extracted for this capture.</div>
  }

  return (
    <>
      <RankedBarList
        ariaLabel={`${title}: share of active tracked listings`}
        items={typeData.skills.map((skill) => ({
          label: skill.skillName,
          count: skill.jobCount,
          share: skill.percentOfActiveListings,
        }))}
        countLabel="listings"
      />
      <p className="muted chart-caption">
        As of {formatNzDateTime(data.generatedAtUtc)} · {typeData.listingDenominator.toLocaleString('en-NZ')} active
        tracked listings · {typeData.listingsWithExtractedSkills.toLocaleString('en-NZ')} with at least one extracted
        skill ({typeData.extractionCoveragePercent}% extraction coverage) · taxonomy v{data.taxonomyVersion}. Percentages
        use the active-listing denominator and each skill counts at most once per listing.{note ? ` ${note}` : ''}
      </p>
      <details className="chart-table">
        <summary>View {title.toLowerCase()} as a table</summary>
        <table>
          <caption>
            {title}, as of {formatNzDateTime(data.generatedAtUtc)}. Same rows as the chart.
          </caption>
          <thead>
            <tr>
              <th scope="col">Skill</th>
              <th scope="col">Active listings</th>
              <th scope="col">% of active listings</th>
              <th scope="col">% of listings with skills</th>
            </tr>
          </thead>
          <tbody>
            {typeData.skills.map((skill) => (
              <tr key={skill.skillName}>
                <th scope="row">{skill.skillName}</th>
                <td>{skill.jobCount.toLocaleString('en-NZ')}</td>
                <td>{skill.percentOfActiveListings}%</td>
                <td>{skill.percentOfListingsWithSkills}%</td>
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </>
  )
}
