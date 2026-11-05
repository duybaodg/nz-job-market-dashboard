namespace Domain.Entities;

/// <summary>
/// Round 21 (Release B2): a human review decision for one sampled listing's
/// classification. Rows are append/update-only, keep the predicted rule version
/// and the advertised title, and never mutate the underlying job. A review
/// without enough evidence is stored as ambiguous or unknown rather than forced
/// into a label.
/// </summary>
public sealed class ClassificationReview
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PredictedRoleFamily { get; set; } = string.Empty;
    public string RuleVersion { get; set; } = string.Empty;
    /// <summary><c>correct</c>, <c>false-positive</c>, <c>false-negative</c>, <c>ambiguous</c> or <c>unknown</c>.</summary>
    public string Label { get; set; } = string.Empty;
    public string? ReviewedRoleFamily { get; set; }
    public string PredictedSeniority { get; set; } = "Unknown";
    public string? ReviewedSeniority { get; set; }
    /// <summary><c>correct</c>, <c>span</c>, <c>ambiguous</c> or <c>unknown</c>.</summary>
    public string SeniorityFlag { get; set; } = "correct";
    public string? SeniorityNote { get; set; }
    public string? Notes { get; set; }
    public string Reviewer { get; set; } = "admin";
    public DateTime CreatedAt { get; set; }
    public DateTime ReviewedAt { get; set; }
}
