namespace Application.Metrics;

using Application.Jobs;

/// <summary>
/// Round 21 (Release B1): deliberately limited public quality measures. Every
/// measure carries its numerator, denominator, capture time and scope so the
/// public panel cannot imply precision that the underlying data does not have.
/// Nothing here exposes crawl errors, per-source operational detail or the
/// gated health payload.
/// </summary>
public sealed record QualityMeasureDto(
    string Key,
    string Label,
    int? Numerator,
    int? Denominator,
    decimal? Percent,
    string DisplayValue,
    string Scope,
    DateTime CapturedAtUtc,
    bool Available,
    string? UnavailableReason,
    string Definition);

/// <summary>
/// Accuracy is only ever present when a reviewed, labelled sample exists. The
/// denominator is the labelled role-family decisions that were not ambiguous or
/// unknown; it is never derived from the classifier's own output.
/// </summary>
public sealed record QualityClassificationDto(
    bool Available,
    string? UnavailableReason,
    int SampledListings,
    int ReviewedListings,
    int Correct,
    int FalsePositive,
    int FalseNegative,
    int Ambiguous,
    int Unknown,
    decimal? AccuracyPercent,
    int AccuracyDenominator,
    string RuleVersion,
    string Scope,
    DateTime? EvaluatedAtUtc,
    string? Method);

public sealed record QualitySummaryDto(
    DateTime GeneratedAtUtc,
    string Population,
    int ActiveListings,
    IReadOnlyList<QualityMeasureDto> Measures,
    QualityClassificationDto Classification,
    string MethodologyNote);

/// <summary>
/// Supplies the committed, reviewed classification evidence. A deployment with
/// no evidence file must return unavailable, never a fabricated accuracy.
/// </summary>
public interface IClassificationEvaluationProvider
{
    QualityClassificationDto GetEvaluation();
}

/// <summary>
/// Default provider: no reviewed sample is bundled. Keeps the public measure
/// unavailable rather than zero.
/// </summary>
public sealed class UnavailableClassificationEvaluationProvider : IClassificationEvaluationProvider
{
    public QualityClassificationDto GetEvaluation() =>
        new(
            false,
            "No reviewed labelled sample is committed for this deployment, so no classification accuracy is published.",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            0,
            RoleFamilyClassifier.RuleVersion,
            "Not evaluated in this deployment.",
            null,
            null);
}

/// <summary>
/// One place for the public definitions. The frontend renders the same text, so
/// a measure cannot be relabelled independently of its denominator.
/// </summary>
public static class QualityDefinitions
{
    public const string ActiveCohortScope =
        "Active tracked listings at the capture time (one advertisement per source, before duplicate grouping).";

    public const string SourceRefreshCoverage =
        "Enabled sources with a successful crawl run inside the freshness window divided by enabled sources. A failed or missing refresh is not counted as success.";

    public const string ComparableSalaryShare =
        "Active listings with a comparable permanent annual NZD salary midpoint divided by all active listings. Missing and incomparable salaries stay in the denominator and are never treated as zero.";

    public const string UnknownRoleShare =
        "Active listings whose deterministic title classifier returned \"Other / Unknown\" divided by all active listings. It measures coverage of the role-family classifier, not market share.";

    public const string SkillExtractionCoverage =
        "Active listings with at least one extracted skill divided by all active listings. A skill counts at most once per listing; this is extraction coverage, not extraction accuracy.";

    public const string EmployerResolution =
        "Active listings resolved to an admin-reviewed canonical employer divided by all active listings. Unmatched advertised names stay unmatched and are never fuzzy-merged.";

    public const string DeduplicationStatus =
        "Only a completed, integrity-validated deduplication pass may publish a unique-vacancy count. Groups, duplicate rows removed and review candidates are reported separately.";

    public const string SnapshotCompleteness =
        "Retained daily snapshot days divided by the calendar days between the first and latest retained snapshot. Missing days are unavailable, never zero.";

    public const string ClassificationAccuracy =
        "Correct role-family decisions divided by reviewed decisions that were not ambiguous or unknown. Only a committed, stratified, labelled sample may populate this measure.";
}
