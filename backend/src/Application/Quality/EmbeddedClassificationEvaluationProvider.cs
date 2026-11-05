using System.Text.Json;
using Application.Metrics;
using Microsoft.Extensions.Logging;

namespace Application.Quality;

/// <summary>
/// Loads the committed, reviewed classification sample from the assembly.
/// The public API publishes only the confusion counts and scope; the review
/// records stay in the repository and the password-gated review workflow.
/// </summary>
public sealed class EmbeddedClassificationEvaluationProvider : IClassificationEvaluationProvider
{
    private const string ResourceSuffix = "classification-evaluation-2026-11-05.json";
    private readonly Lazy<QualityClassificationDto> evaluation;

    public EmbeddedClassificationEvaluationProvider(ILogger<EmbeddedClassificationEvaluationProvider>? logger = null)
    {
        evaluation = new Lazy<QualityClassificationDto>(() => Load(logger));
    }

    public QualityClassificationDto GetEvaluation() => evaluation.Value;

    private static QualityClassificationDto Load(ILogger? logger)
    {
        try
        {
            var assembly = typeof(EmbeddedClassificationEvaluationProvider).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
            if (resourceName is null)
            {
                return Unavailable("The reviewed classification sample is not embedded in this build.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return Unavailable("The reviewed classification sample could not be read.");
            }

            var file = JsonSerializer.Deserialize<EvaluationFile>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (file is null)
            {
                return Unavailable("The reviewed classification sample is empty.");
            }

            var counts = file.Counts;
            var available = counts.AccuracyDenominator > 0 && counts.AccuracyPercent is not null;
            return new QualityClassificationDto(
                available,
                available
                    ? null
                    : "The committed sample has no reviewed decisions in the accuracy denominator.",
                counts.Sampled,
                counts.Reviewed,
                counts.Correct,
                counts.FalsePositive,
                counts.FalseNegative,
                counts.Ambiguous,
                counts.Unknown,
                counts.AccuracyPercent,
                counts.AccuracyDenominator,
                file.RuleVersions.RoleFamily,
                file.Scope,
                DateTime.TryParse(file.EvaluatedAtUtc, out var evaluatedAt) ? evaluatedAt.ToUniversalTime() : null,
                $"{file.ReviewMethod} Sample: {file.Sampling.Method}");
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Could not load the reviewed classification sample.");
            return Unavailable("The reviewed classification sample could not be parsed.");
        }
    }

    private static QualityClassificationDto Unavailable(string reason) =>
        new(
            false,
            reason,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            0,
            "role-family-v1",
            "Not evaluated in this deployment.",
            null,
            null);

    private sealed record EvaluationFile(
        string EvaluationId,
        string EvaluatedAtUtc,
        string Reviewer,
        string ReviewMethod,
        SamplingFile Sampling,
        RuleVersionsFile RuleVersions,
        CountsFile Counts,
        string Scope,
        IReadOnlyList<ReviewRecordFile> Records);

    private sealed record SamplingFile(
        string Frame,
        string Method,
        int SampleSize,
        int Sources,
        int RoleFamilies,
        int TargetSize);

    private sealed record RuleVersionsFile(string RoleFamily, string Seniority);

    private sealed record CountsFile(
        int Sampled,
        int Reviewed,
        int Correct,
        int FalsePositive,
        int FalseNegative,
        int Ambiguous,
        int Unknown,
        int AccuracyDenominator,
        decimal? AccuracyPercent);

    private sealed record ReviewRecordFile(
        string JobId,
        string Source,
        string Title,
        string? AdvertisedCompany,
        string PredictedRoleFamily,
        string ReviewedRoleFamily,
        string Label,
        string PredictedSeniority,
        string ReviewedSeniority,
        string SeniorityFlag,
        IReadOnlyList<string> SeniorityLevels,
        string Notes,
        string EvidenceExcerpt,
        string EvidenceSha256,
        string RuleVersion,
        string SeniorityRuleVersion);
}
