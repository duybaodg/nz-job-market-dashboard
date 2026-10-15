namespace Application.Jobs;

/// <summary>
/// Configuration for the optional AI field-enrichment pass. Default-off: the
/// pass only runs when <see cref="Enabled"/> is true and a non-empty
/// <see cref="ApiKey"/> is configured, so the shipped stack never calls a paid
/// API. No unit test ever calls a real endpoint (the client is mocked).
/// </summary>
public sealed class AiFieldEnrichmentOptions
{
    public bool Enabled { get; set; }

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>OpenAI-compatible base URL, e.g. https://api.openai.com/v1.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Maximum jobs enriched per worker cycle (cost control).</summary>
    public int BatchSize { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Do not re-evaluate the same job more often than this.</summary>
    public int ThrottleHours { get; set; } = 24;

    /// <summary>Discard AI readings below this confidence.</summary>
    public decimal MinConfidence { get; set; } = 0.5m;
}

/// <summary>What the model is asked to extract from one listing.</summary>
public sealed record AiFieldEnrichmentRequest(string Title, string? Location, string? Description, string? SourceSalaryText);

/// <summary>
/// A defensive reading of the model's strict-JSON answer. Every field is
/// nullable/optional because a model may omit or mis-type anything.
/// </summary>
public sealed record AiFieldEnrichmentResponse(
    decimal? SalaryMin,
    decimal? SalaryMax,
    string? Currency,
    string? Period,
    string? WorkMode,
    string? Seniority,
    decimal? Confidence,
    string? Model);

public interface IAiFieldEnrichmentClient
{
    /// <summary>Returns null when the call failed or the answer could not be parsed.</summary>
    Task<AiFieldEnrichmentResponse?> ExtractAsync(AiFieldEnrichmentRequest request, CancellationToken cancellationToken);
}
